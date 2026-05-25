using Microsoft.Extensions.Configuration;

namespace QaaS.Framework.Configurations;

/// <summary>
/// Resolves <c>${...}</c> placeholders inside an <see cref="IConfiguration"/>.
/// Scalar references substitute as text; object references to replace the destination subtree.
/// </summary>
public class ConfigurationPlaceholderParser(IConfiguration configuration)
{
    private const string PlaceholderStart = "${";
    private const string NullSeparator = "??";
    private const char OpenCurlyBracket = '{';
    private const char CloseCurlyBracket = '}';

    private static readonly char PathSeparatorChar = ConfigurationConstants.PathSeparator[0];

    private readonly HashSet<string> _activeResolutionPaths = [];
    private readonly HashSet<string> _existingPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string?> _configurationEntries = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _parentPathRefcounts = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _pathsContainingPlaceholders = new(StringComparer.OrdinalIgnoreCase);
    private IConfiguration _configuration = configuration;
    private int _modificationCount;

    /// <summary>
    /// Resolves every placeholder; runs passes until one produces no mutations.
    /// </summary>
    public IConfiguration ResolvePlaceholders()
    {
        RebuildPathIndexAndCollectPlaceholders();

        int modificationCountAtPassStart;
        do
        {
            modificationCountAtPassStart = _modificationCount;

            // Snapshot — ResolvePlaceholderValue can mutate _pathsContainingPlaceholders mid-iteration.
            foreach (var placeholderPath in _pathsContainingPlaceholders.ToArray())
            {
                var valueAtPlaceholderPath = _configuration[placeholderPath];
                
                if (valueAtPlaceholderPath is not null && valueAtPlaceholderPath.Contains(PlaceholderStart, StringComparison.Ordinal))
                {
                    // It may already be resolved indirectly while resolving another placeholder.
                    ResolvePlaceholderValue(placeholderPath);
                }
            }
        } while (_modificationCount != modificationCountAtPassStart);

        return _configuration;
    }

    /// <summary>
    /// One-time O(N) index population; per-Copy updates are incremental.
    /// </summary>
    private void RebuildPathIndexAndCollectPlaceholders()
    {
        _existingPaths.Clear();
        _configurationEntries.Clear();
        _parentPathRefcounts.Clear();
        _pathsContainingPlaceholders.Clear();

        // Walks the whole configuration tree as flattened paths, including deep YAML parents and leaves.
        foreach (var configurationEntry in _configuration.AsEnumerable())
        {
            _configurationEntries[configurationEntry.Key] = configurationEntry.Value;

            if (_existingPaths.Add(configurationEntry.Key))
                UpdateParentRefcounts(configurationEntry.Key, delta: +1);

            // If the value contains "${", remember this path for resolving later
            if (configurationEntry.Value is { } entryValue && entryValue.Contains(PlaceholderStart, StringComparison.Ordinal))
                _pathsContainingPlaceholders.Add(configurationEntry.Key);
        }
    }

    /// <summary>
    /// Writes a scalar value, syncs the placeholder index, bumps the modification counter.
    /// </summary>
    private void WriteValueAt(string path, string? value)
    {
        _configurationEntries[path] = value;
        _configuration[path] = value;
        RefreshPlaceholderMembership(path, value);
        _modificationCount++;
    }

    /// <summary>
    /// Resolves every <c>${...}</c> in the value at <paramref name="path"/>, recursing into
    /// referenced paths. Returns null if the path is gone (stale snapshot after a Copy).
    /// </summary>
    private IConfigurationSection? ResolvePlaceholderValue(string path)
    {
        var currentSection = GetSectionAtPath(path);
        if (currentSection is null || !IsStringLeaf(currentSection)) return currentSection;

        var nextScanIndex = 0;
        while (currentSection.Value is { } sectionValue)
        {
            if (TryParseNextPlaceholder(sectionValue, nextScanIndex) is not { } placeholder) break;

            // Get the config path that ${...} points to
            var referencedSection = GetSectionAtPath(placeholder.ReferencedPath);
            if (referencedSection is null && placeholder.DefaultValue is null) break;

            if (referencedSection is null)
            {
                // Missing source with a default: splice the default in and recurse for nested placeholders.
                string sectionValueWithDefaultApplied = SpliceReplacementIntoValue(sectionValue, placeholder, placeholder.DefaultValue);
                WriteValueAt(path, sectionValueWithDefaultApplied);
                currentSection = ResolvePlaceholderValue(path);
                if (currentSection is null) break;
                continue;
            }

            if (!_activeResolutionPaths.Add(placeholder.ReferencedPath))
            {
                throw new InvalidOperationException(
                    $"Configuration placeholder loop found: '{path}' refers back to '{placeholder.ReferencedPath}'. " +
                    "Check the YAML placeholders that reference each other.");
            }

            // try/finally so an exception during recursion doesn't leave the path stuck on the stack.
            try
            {
                var resolvedSection = ResolvePlaceholderValue(placeholder.ReferencedPath);
                if (resolvedSection is null) break;

                if (!IsStringLeaf(resolvedSection))
                {
                    // Object references are only legal when ${X} is the whole value — can't splice an object into a string.
                    if (IsPlaceholderEmbeddedInString(sectionValue, placeholder))
                        throw new InvalidOperationException("Placeholder reference to an object but is a substring value at: " + path);

                    CopyConfigurationsByPath(placeholder.ReferencedPath, path);
                    currentSection = resolvedSection;
                    break;
                }

                var replacement = resolvedSection.Value ?? string.Empty;
                sectionValue = SpliceReplacementIntoValue(sectionValue, placeholder, replacement);
                WriteValueAt(path, sectionValue);
                nextScanIndex = placeholder.StartIndex + replacement.Length;
            }
            finally
            {
                _activeResolutionPaths.Remove(placeholder.ReferencedPath);
            }
        }

        return currentSection;
    }

    /// <summary>
    /// True when the section has a value and no descendants.
    /// </summary>
    private bool IsStringLeaf(IConfigurationSection section) =>
        section.Value != null && !_parentPathRefcounts.ContainsKey(section.Path);

    private IConfigurationSection? GetSectionAtPath(string path) =>
        _existingPaths.Contains(path) ? _configuration.GetSection(path) : null;

    /// <summary>
    /// Replaces the destination subtree with the source subtree using the cached flattened entries.
    /// </summary>
    private void CopyConfigurationsByPath(string sourcePath, string destinationPath)
    {
        var removedEntries = _configurationEntries
            .Where(entry => IsPathOrDescendant(entry.Key, destinationPath))
            .ToList();
        var addedEntries = _configurationEntries
            .Where(entry => !IsPathOrDescendant(entry.Key, destinationPath) &&
                            IsPathOrDescendant(entry.Key, sourcePath))
            .Select(entry => new KeyValuePair<string, string?>(RebasePathPrefix(entry.Key, sourcePath, destinationPath), entry.Value))
            .ToList();

        foreach (var removedEntry in removedEntries)
            _configurationEntries.Remove(removedEntry.Key);
        foreach (var addedEntry in addedEntries)
            _configurationEntries[addedEntry.Key] = addedEntry.Value;

        _configuration = new ConfigurationBuilder().AddInMemoryCollection(_configurationEntries).Build();

        foreach (var removedEntry in removedEntries)
            RemovePathFromIndex(removedEntry.Key);
        foreach (var addedEntry in addedEntries)
            AddPathToIndex(addedEntry.Key, addedEntry.Value);
        _modificationCount++;
    }

    private void AddPathToIndex(string path, string? value)
    {
        if (_existingPaths.Add(path))
            UpdateParentRefcounts(path, delta: +1);
        RefreshPlaceholderMembership(path, value);
    }

    private void RemovePathFromIndex(string path)
    {
        if (_existingPaths.Remove(path))
            UpdateParentRefcounts(path, delta: -1);
        _pathsContainingPlaceholders.Remove(path);
    }

    private void RefreshPlaceholderMembership(string path, string? value)
    {
        if (value is not null && value.Contains(PlaceholderStart, StringComparison.Ordinal))
            _pathsContainingPlaceholders.Add(path);
        else
            _pathsContainingPlaceholders.Remove(path);
    }

    /// <summary>
    /// Walks ancestors and adjusts each one's refcount by <paramref name="delta"/>.
    /// The 0→1 transition marks an ancestor as a parent; the 1→0 transition drops it.
    /// </summary>
    private void UpdateParentRefcounts(string path, int delta)
    {
        var ancestorPath = path;
        int lastPathSeparatorIndex;
        while ((lastPathSeparatorIndex = ancestorPath.LastIndexOf(PathSeparatorChar)) > 0)
        {
            ancestorPath = ancestorPath[..lastPathSeparatorIndex];
            var nextCount = _parentPathRefcounts.GetValueOrDefault(ancestorPath) + delta;

            // Remove only on 1->0; other descendants may still keep this parent alive.
            if (nextCount == 0)
                _parentPathRefcounts.Remove(ancestorPath);
            else
                _parentPathRefcounts[ancestorPath] = nextCount;
        }
    }

    /// <summary>
    /// Finds the next <c>${X??default}</c> at or after <paramref name="searchFromIndex"/>, or null.
    /// </summary>
    private static ParsedPlaceholder? TryParseNextPlaceholder(string sectionValue, int searchFromIndex)
    {
        var startIndex = sectionValue.IndexOf(PlaceholderStart, searchFromIndex, StringComparison.Ordinal);
        if (startIndex == -1) return null;

        var endIndex = FindClosingBracket(sectionValue, startIndex + 2);
        if (endIndex == -1) return null;

        var body = sectionValue.Substring(startIndex + 2, endIndex - startIndex - 2);
        var parts = body.Split(NullSeparator, 2);
        return new ParsedPlaceholder(
            startIndex,
            endIndex,
            parts[0].Trim(),
            parts.Length > 1 ? parts[1].Trim() : null);
    }

    /// <summary>
    /// Replaces the parsed placeholder span with the replacement while preserving surrounding text.
    /// </summary>
    private static string SpliceReplacementIntoValue(string sectionValue, ParsedPlaceholder placeholder, string? replacement) =>
        sectionValue.Substring(0, placeholder.StartIndex) + replacement + sectionValue.Substring(placeholder.EndIndex + 1);

    /// <summary>
    /// True when there is any text or whitespace before or after the placeholder.
    /// </summary>
    private static bool IsPlaceholderEmbeddedInString(string sectionValue, ParsedPlaceholder placeholder) =>
        placeholder.StartIndex != 0 || placeholder.EndIndex != sectionValue.Length - 1;

    private static bool IsPathOrDescendant(string candidatePath, string path) =>
        candidatePath.StartsWith(path, StringComparison.OrdinalIgnoreCase) &&
        (candidatePath.Length == path.Length || candidatePath[path.Length] == PathSeparatorChar);

    private static string RebasePathPrefix(string path, string sourcePath, string destinationPath) =>
        path.Length == sourcePath.Length ? destinationPath : destinationPath + path[sourcePath.Length..];

    /// <summary>
    /// Finds the matching <c>}</c> for the <c>${</c> at <paramref name="startIndex"/> - 2, tracking nested depth.
    /// </summary>
    private static int FindClosingBracket(string str, int startIndex)
    {
        var depth = 1;
        for (var currentIndex = startIndex + 1; currentIndex < str.Length; currentIndex++)
        {
            if (str[currentIndex] == OpenCurlyBracket) depth++;
            if (str[currentIndex] == CloseCurlyBracket) depth--;
            if (depth == 0) return currentIndex;
        }

        return -1;
    }

    private readonly record struct ParsedPlaceholder(
        int StartIndex,
        int EndIndex,
        string ReferencedPath,
        string? DefaultValue);
}
