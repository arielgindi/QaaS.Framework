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

    private readonly HashSet<string> _activeResolutionPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string?> _entriesByPath = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<CachedConfigurationEntry>> _sourceSubtreesByPath =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _descendantCountByParentPath = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _placeholderPaths = new(StringComparer.OrdinalIgnoreCase);
    private IConfiguration _configuration = configuration;
    private int _modificationCount;

    /// <summary>
    /// Resolves every placeholder; runs passes until one produces no mutations.
    /// </summary>
    public IConfiguration ResolvePlaceholders()
    {
        RebuildEntryCacheAndIndexes();

        var modificationCountAtResolveStart = _modificationCount;
        try
        {
            ResolvePlaceholderPathsUntilStable();
        }
        finally
        {
            if (_modificationCount != modificationCountAtResolveStart)
                RebuildConfigurationFromEntries();
        }

        return _configuration;
    }

    /// <summary>
    /// One-time O(N) cache/index population; later writes and copies update those structures incrementally.
    /// </summary>
    private void RebuildEntryCacheAndIndexes()
    {
        _entriesByPath.Clear();
        _sourceSubtreesByPath.Clear();
        _descendantCountByParentPath.Clear();
        _placeholderPaths.Clear();

        // Walks the whole configuration tree as flattened paths, including deep YAML parents and leaves.
        foreach (var configurationEntry in _configuration.AsEnumerable())
        {
            SetEntry(configurationEntry.Key, configurationEntry.Value);
        }
    }

    private void ResolvePlaceholderPathsUntilStable()
    {
        int modificationCountAtPassStart;
        do
        {
            modificationCountAtPassStart = _modificationCount;
            ResolveCurrentPlaceholderPathSnapshot();
        } while (_modificationCount != modificationCountAtPassStart);
    }

    private void ResolveCurrentPlaceholderPathSnapshot()
    {
        // ResolvePlaceholderValue can mutate _placeholderPaths mid-iteration.
        foreach (var placeholderPath in _placeholderPaths.ToArray())
        {
            if (!PathValueContainsPlaceholder(placeholderPath))
                continue;

            // It may already be resolved indirectly while resolving another placeholder.
            ResolvePlaceholderValue(placeholderPath);
        }
    }

    private bool PathValueContainsPlaceholder(string path) =>
        TryGetValueAtPath(path, out var value) && ValueContainsPlaceholder(value);

    private static bool ValueContainsPlaceholder(string? value) =>
        value?.Contains(PlaceholderStart, StringComparison.Ordinal) == true;

    /// <summary>
    /// Writes a scalar value, syncs the placeholder index, bumps the modification counter.
    /// </summary>
    private void WriteValueAt(string path, string? value)
    {
        SetEntry(path, value);
        InvalidateSourceSubtreeCache(path);
        _modificationCount++;
    }

    /// <summary>
    /// Resolves every <c>${...}</c> in the value at <paramref name="path"/>, recursing into
    /// referenced paths. Returns null if the path is gone (stale snapshot after a Copy).
    /// </summary>
    private CachedConfigurationEntry? ResolvePlaceholderValue(string path)
    {
        if (!TryGetEntryAtPath(path, out var currentEntry)) return null;
        if (!HasDirectValue(currentEntry)) return currentEntry;

        var nextScanIndex = 0;
        while (TryGetValueAtPath(path, out var valueAtPath) && valueAtPath is not null)
        {
            if (TryParseNextPlaceholder(valueAtPath, nextScanIndex) is not { } placeholder) break;

            if (!PathExists(placeholder.ReferencedPath))
            {
                if (placeholder.DefaultValue is null) break;
                if (!ApplyDefaultAndResolve(path, valueAtPath, placeholder)) break;
                continue;
            }

            var resolvedEntry = ResolveReferencedPath(path, placeholder);
            if (resolvedEntry is null) break;
            var referencedEntry = resolvedEntry.Value;

            if (!IsScalarEntry(referencedEntry))
            {
                CopyObjectReference(path, valueAtPath, placeholder);
                break;
            }

            var replacement = referencedEntry.Value ?? string.Empty;
            ReplaceScalarPlaceholder(path, valueAtPath, placeholder, replacement);
            nextScanIndex = placeholder.StartIndex + replacement.Length;
        }

        return GetEntryAtPath(path);
    }

    private bool ApplyDefaultAndResolve(string path, string valueAtPath, ParsedPlaceholder placeholder)
    {
        var valueWithDefaultApplied = SpliceReplacementIntoValue(
            valueAtPath,
            placeholder,
            placeholder.DefaultValue);

        WriteValueAt(path, valueWithDefaultApplied);
        return ResolvePlaceholderValue(path) is not null;
    }

    private CachedConfigurationEntry? ResolveReferencedPath(string path, ParsedPlaceholder placeholder)
    {
        if (!_activeResolutionPaths.Add(placeholder.ReferencedPath))
            throw CreateCircularReferenceException(path, placeholder.ReferencedPath);

        try
        {
            return ResolvePlaceholderValue(placeholder.ReferencedPath);
        }
        finally
        {
            _activeResolutionPaths.Remove(placeholder.ReferencedPath);
        }
    }

    private void CopyObjectReference(string destinationPath, string valueAtDestinationPath, ParsedPlaceholder placeholder)
    {
        // Object references are only legal when ${X} is the whole value; they cannot be spliced into a string.
        if (IsPlaceholderEmbeddedInString(valueAtDestinationPath, placeholder))
            throw new InvalidOperationException(
                $"Configuration placeholder at '{destinationPath}' references object '{placeholder.ReferencedPath}', " +
                "but it is embedded inside a string.");

        CopySubtreeByPath(placeholder.ReferencedPath, destinationPath);
    }

    private void ReplaceScalarPlaceholder(
        string path,
        string valueAtPath,
        ParsedPlaceholder placeholder,
        string replacement)
    {
        var resolvedValue = SpliceReplacementIntoValue(valueAtPath, placeholder, replacement);
        WriteValueAt(path, resolvedValue);
    }

    private static InvalidOperationException CreateCircularReferenceException(string path, string referencedPath) =>
        new(
            $"Configuration placeholder loop found: '{path}' refers back to '{referencedPath}'. " +
            "Check the YAML placeholders that reference each other.");

    /// <summary>
    /// True when the cached entry has a value and no descendants.
    /// </summary>
    private bool IsScalarEntry(CachedConfigurationEntry entry) =>
        entry.Value != null && !HasDescendants(entry.Path);

    private static bool HasDirectValue(CachedConfigurationEntry entry) =>
        entry.Value != null;

    private bool HasDescendants(string path) =>
        _descendantCountByParentPath.ContainsKey(path);

    private CachedConfigurationEntry? GetEntryAtPath(string path) =>
        TryGetEntryAtPath(path, out var entry) ? entry : null;

    private bool PathExists(string path) =>
        _entriesByPath.ContainsKey(path);

    private bool TryGetEntryAtPath(string path, out CachedConfigurationEntry entry)
    {
        if (_entriesByPath.TryGetValue(path, out var value))
        {
            entry = new CachedConfigurationEntry(path, value);
            return true;
        }

        entry = default;
        return false;
    }

    private bool TryGetValueAtPath(string path, out string? value) =>
        _entriesByPath.TryGetValue(path, out value);

    /// <summary>
    /// Replaces the destination subtree in the cached entries; the IConfiguration rebuild is delayed.
    /// </summary>
    private void CopySubtreeByPath(string sourcePath, string destinationPath)
    {
        var removedEntries = GetDestinationEntriesToRemove(destinationPath);
        var addedEntries = GetSourceSubtreeEntries(sourcePath)
            .Where(entry => !IsPathOrDescendant(entry.Path, destinationPath))
            .Select(entry => RebaseEntry(entry, sourcePath, destinationPath))
            .ToList();

        foreach (var removedEntry in removedEntries)
            RemoveEntry(removedEntry.Path);
        foreach (var addedEntry in addedEntries)
            SetEntry(addedEntry.Path, addedEntry.Value);
        InvalidateSourceSubtreeCache(destinationPath);
        _modificationCount++;
    }

    private List<CachedConfigurationEntry> GetDestinationEntriesToRemove(string destinationPath)
    {
        if (HasDescendants(destinationPath))
        {
            return _entriesByPath
                .Where(entry => IsPathOrDescendant(entry.Key, destinationPath))
                .Select(entry => new CachedConfigurationEntry(entry.Key, entry.Value))
                .ToList();
        }

        return _entriesByPath.TryGetValue(destinationPath, out var value)
            ? [new CachedConfigurationEntry(destinationPath, value)]
            : [];
    }

    private List<CachedConfigurationEntry> GetSourceSubtreeEntries(string sourcePath)
    {
        if (_sourceSubtreesByPath.TryGetValue(sourcePath, out var cachedEntries))
            return cachedEntries;

        var sourceEntries = _entriesByPath
            .Where(entry => IsPathOrDescendant(entry.Key, sourcePath))
            .Select(entry => new CachedConfigurationEntry(entry.Key, entry.Value))
            .ToList();
        _sourceSubtreesByPath[sourcePath] = sourceEntries;
        return sourceEntries;
    }

    private void InvalidateSourceSubtreeCache(string changedPath)
    {
        var staleSourcePaths = _sourceSubtreesByPath.Keys
            .Where(sourcePath => SourceSubtreeCacheOverlapsChange(sourcePath, changedPath))
            .ToArray();

        foreach (var cachedSourcePath in staleSourcePaths)
            _sourceSubtreesByPath.Remove(cachedSourcePath);
    }

    private static bool SourceSubtreeCacheOverlapsChange(string sourcePath, string changedPath) =>
        IsPathOrDescendant(changedPath, sourcePath) || IsPathOrDescendant(sourcePath, changedPath);

    private void RebuildConfigurationFromEntries() =>
        _configuration = new ConfigurationBuilder().AddInMemoryCollection(_entriesByPath).Build();

    private void SetEntry(string path, string? value)
    {
        if (_entriesByPath.TryAdd(path, value))
        {
            UpdateAncestorDescendantCounts(path, delta: +1);
        }
        else
        {
            _entriesByPath[path] = value;
        }

        RefreshPlaceholderMembership(path, value);
    }

    private void RemoveEntry(string path)
    {
        if (_entriesByPath.Remove(path))
            UpdateAncestorDescendantCounts(path, delta: -1);
        _placeholderPaths.Remove(path);
    }

    private void RefreshPlaceholderMembership(string path, string? value)
    {
        if (ValueContainsPlaceholder(value))
            _placeholderPaths.Add(path);
        else
            _placeholderPaths.Remove(path);
    }

    /// <summary>
    /// Walks ancestors and adjusts each one's descendant count by <paramref name="delta"/>.
    /// The 0→1 transition marks an ancestor as a parent; the 1→0 transition drops it.
    /// </summary>
    private void UpdateAncestorDescendantCounts(string path, int delta)
    {
        var ancestorPath = path;
        int lastPathSeparatorIndex;
        while ((lastPathSeparatorIndex = ancestorPath.LastIndexOf(PathSeparatorChar)) > 0)
        {
            ancestorPath = ancestorPath[..lastPathSeparatorIndex];
            var nextCount = _descendantCountByParentPath.GetValueOrDefault(ancestorPath) + delta;

            // Remove only on 1->0; other descendants may still keep this parent alive.
            if (nextCount == 0)
                _descendantCountByParentPath.Remove(ancestorPath);
            else
                _descendantCountByParentPath[ancestorPath] = nextCount;
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
    private static string SpliceReplacementIntoValue(
        string sectionValue,
        ParsedPlaceholder placeholder,
        string? replacement) =>
        sectionValue.Substring(0, placeholder.StartIndex) +
        replacement +
        sectionValue.Substring(placeholder.EndIndex + 1);

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

    private static CachedConfigurationEntry RebaseEntry(
        CachedConfigurationEntry entry,
        string sourcePath,
        string destinationPath) =>
        new(RebasePathPrefix(entry.Path, sourcePath, destinationPath), entry.Value);

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

    private readonly record struct CachedConfigurationEntry(
        string Path,
        string? Value);
}
