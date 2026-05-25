using Microsoft.Extensions.Configuration;

namespace QaaS.Framework.Configurations;

/// <summary>
/// Class that contains functionality for parsing the placeholder values in a configuration.
/// </summary>
public class ConfigurationPlaceholderParser(IConfiguration configuration)
{
    private const string PlaceholderStart = "${";
    private const string PlaceholderEnd = "}";
    private const string NullSeparator = "??";
    private const char OpenCurlyBracket = '{';
    private const char CloseCurlyBracket = '}';

    private readonly HashSet<string> _resolutionStack = [];
    private readonly HashSet<string> _existingPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _parentPathRefcounts = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _pathsContainingPlaceholders = new(StringComparer.OrdinalIgnoreCase);
    private IConfiguration _configuration = configuration;
    private int _modificationCount;

    /// <summary>
    /// Resolves every placeholder in the configuration and returns the resolved configuration.
    /// Runs a fixed-point loop: each pass walks the paths that still contain a placeholder and
    /// repeats until a pass produces no mutations.
    /// </summary>
    public IConfiguration ResolvePlaceholders()
    {
        RebuildPathIndexAndCollectPlaceholders();

        int modificationCountAtPassStart;
        do
        {
            modificationCountAtPassStart = _modificationCount;
            foreach (var placeholderPath in _pathsContainingPlaceholders.ToArray())
            {
                var currentValue = _configuration[placeholderPath];
                if (currentValue is not null && currentValue.Contains(PlaceholderStart, StringComparison.Ordinal))
                    ResolvePlaceholderValue(placeholderPath);
            }
        } while (_modificationCount != modificationCountAtPassStart);

        return _configuration;
    }

    /// <summary>
    /// Walks the whole configuration tree once and (re)populates the three indexes used by the
    /// resolver: <see cref="_existingPaths"/>, <see cref="_parentPathRefcounts"/>, and
    /// <see cref="_pathsContainingPlaceholders"/>. Called once at the start of resolution; per-Copy
    /// updates are then incremental (see <see cref="AddPathToIndex"/> / <see cref="RemovePathFromIndex"/>).
    /// </summary>
    private void RebuildPathIndexAndCollectPlaceholders()
    {
        _existingPaths.Clear();
        _parentPathRefcounts.Clear();
        _pathsContainingPlaceholders.Clear();
        foreach (var configurationEntry in _configuration.AsEnumerable())
        {
            if (_existingPaths.Add(configurationEntry.Key))
                IncrementParentRefcount(configurationEntry.Key);
            if (configurationEntry.Value is { } entryValue && entryValue.Contains(PlaceholderStart, StringComparison.Ordinal))
                _pathsContainingPlaceholders.Add(configurationEntry.Key);
        }
    }

    /// <summary>
    /// Writes <paramref name="value"/> at <paramref name="path"/> in the configuration and keeps
    /// the placeholder-membership index in sync. Bumps <see cref="_modificationCount"/> so the
    /// outer fixed-point loop knows another pass is needed.
    /// </summary>
    private void WriteValueAt(string path, string? value)
    {
        _configuration[path] = value;
        RefreshPlaceholderMembership(path, value);
        _modificationCount++;
    }

    /// <summary>
    /// Resolves every <c>${...}</c> placeholder inside the value at <paramref name="path"/>,
    /// recursing into referenced paths as needed. Returns the resolved section, or null if the
    /// path no longer exists in the configuration (which can happen for stale paths in the outer
    /// loop's snapshot after a Copy).
    /// </summary>
    private IConfigurationSection? ResolvePlaceholderValue(string path)
    {
        var currentSection = GetSectionAtPath(path);
        if (currentSection is null || !IsStringLeaf(currentSection)) return currentSection;

        var nextScanIndex = 0;
        while (currentSection.Value is { } sectionValue)
        {
            if (TryParseNextPlaceholder(sectionValue, nextScanIndex) is not { } placeholder) break;

            var referencedSection = GetSectionAtPath(placeholder.ReferencedPath);
            if (referencedSection is null && placeholder.DefaultValue is null) break;

            if (referencedSection is null)
            {
                var valueWithDefault = SpliceReplacementIntoValue(sectionValue, placeholder, placeholder.DefaultValue);
                WriteValueAt(path, valueWithDefault);
                currentSection = ResolvePlaceholderValue(path);
                if (currentSection is null) break;
                continue;
            }

            if (_resolutionStack.Contains(placeholder.ReferencedPath))
                throw new InvalidOperationException("Circular placeholder reference detected in configuration at: " + path);

            // try/finally so an exception during recursion or substring-validation does not leave
            // a stale entry in _resolutionStack and make a later valid resolve look falsely circular.
            _resolutionStack.Add(placeholder.ReferencedPath);
            try
            {
                var resolvedSection = ResolvePlaceholderValue(placeholder.ReferencedPath);
                if (resolvedSection is null) break;

                if (!IsStringLeaf(resolvedSection))
                {
                    if (IsPlaceholderEmbeddedInString(sectionValue, placeholder))
                        throw new InvalidOperationException("Placeholder reference to an object but is a substring value at: " + path);

                    CopyConfigurationsByPath(placeholder.ReferencedPath, path);
                    currentSection = resolvedSection;
                    break;
                }

                sectionValue = SpliceReplacementIntoValue(sectionValue, placeholder, resolvedSection.Value);
                WriteValueAt(path, sectionValue);
                nextScanIndex = placeholder.StartIndex + resolvedSection.Value!.Length;
            }
            finally
            {
                _resolutionStack.Remove(placeholder.ReferencedPath);
            }
        }

        return currentSection;
    }

    /// <summary>
    /// A section is a "string leaf" when it has a value AND no descendants — i.e. its path
    /// is not an ancestor of any other key, tracked in <see cref="_parentPathRefcounts"/>.
    /// </summary>
    private bool IsStringLeaf(IConfigurationSection section)
    {
        return section.Value != null && !_parentPathRefcounts.ContainsKey(section.Path);
    }

    /// <summary>
    /// Returns the section at <paramref name="path"/>, or null if <paramref name="path"/> is not
    /// in the current configuration. The check uses <see cref="_existingPaths"/> rather than
    /// inspecting <see cref="_configuration"/> directly so we avoid the cost of creating an
    /// <see cref="IConfigurationSection"/> for paths we know aren't there.
    /// </summary>
    private IConfigurationSection? GetSectionAtPath(string path)
    {
        return _existingPaths.Contains(path) ? _configuration.GetSection(path) : null;
    }

    /// <summary>
    /// Replaces the subtree at <paramref name="destinationPath"/> with the subtree at
    /// <paramref name="sourcePath"/> (rebased to start with <paramref name="destinationPath"/>).
    /// Updates all three indexes incrementally rather than rebuilding them, so the cost is
    /// O(K × depth) — K = touched keys, depth = path depth — instead of O(N) over the whole tree.
    /// </summary>
    private void CopyConfigurationsByPath(string sourcePath, string destinationPath)
    {
        var allEntries = _configuration.AsEnumerable().ToList();
        var removedEntries = allEntries
            .Where(entry => IsPathOrDescendant(entry.Key, destinationPath))
            .ToList();
        var preservedEntries = allEntries
            .Where(entry => !IsPathOrDescendant(entry.Key, destinationPath))
            .ToList();
        var addedEntries = preservedEntries
            .Where(entry => IsPathOrDescendant(entry.Key, sourcePath))
            .Select(entry => new KeyValuePair<string, string?>(RebasePathPrefix(entry.Key, sourcePath, destinationPath), entry.Value))
            .ToList();
        _configuration = new ConfigurationBuilder().AddInMemoryCollection(preservedEntries.Concat(addedEntries)).Build();
        foreach (var removedEntry in removedEntries)
            RemovePathFromIndex(removedEntry.Key);
        foreach (var addedEntry in addedEntries)
            AddPathToIndex(addedEntry.Key, addedEntry.Value);
        _modificationCount++;
    }

    /// <summary>
    /// Registers <paramref name="path"/> in all three indexes: marks it existing, increments
    /// every ancestor's parent refcount, and adds it to the placeholder set if its value
    /// contains a <c>${</c> marker.
    /// </summary>
    private void AddPathToIndex(string path, string? value)
    {
        if (_existingPaths.Add(path))
            IncrementParentRefcount(path);
        RefreshPlaceholderMembership(path, value);
    }

    /// <summary>
    /// De-registers <paramref name="path"/> from all three indexes: drops it from the existing
    /// set, decrements every ancestor's parent refcount, and drops it from the placeholder set.
    /// </summary>
    private void RemovePathFromIndex(string path)
    {
        if (_existingPaths.Remove(path))
            DecrementParentRefcount(path);
        _pathsContainingPlaceholders.Remove(path);
    }

    /// <summary>
    /// Adds or removes <paramref name="path"/> in <see cref="_pathsContainingPlaceholders"/>
    /// based on whether <paramref name="value"/> currently contains a <c>${</c> marker. Called
    /// from <see cref="WriteValueAt"/> and <see cref="AddPathToIndex"/> to keep the placeholder
    /// snapshot honest as values change.
    /// </summary>
    private void RefreshPlaceholderMembership(string path, string? value)
    {
        if (value is not null && value.Contains(PlaceholderStart, StringComparison.Ordinal))
            _pathsContainingPlaceholders.Add(path);
        else
            _pathsContainingPlaceholders.Remove(path);
    }

    /// <summary>
    /// Walks the ancestor chain of <paramref name="path"/> and increments each ancestor's
    /// refcount in <see cref="_parentPathRefcounts"/>. The 0→1 transition makes the ancestor
    /// visible as a parent; later increments just keep the entry alive.
    /// </summary>
    private void IncrementParentRefcount(string path)
    {
        var ancestorPath = path;
        int lastPathSeparatorIndex;
        while ((lastPathSeparatorIndex = ancestorPath.LastIndexOf(ConfigurationConstants.PathSeparator[0])) > 0)
        {
            ancestorPath = ancestorPath[..lastPathSeparatorIndex];
            _parentPathRefcounts[ancestorPath] = _parentPathRefcounts.GetValueOrDefault(ancestorPath) + 1;
        }
    }

    /// <summary>
    /// Walks the ancestor chain of <paramref name="path"/> and decrements each ancestor's
    /// refcount. The 1→0 transition removes the ancestor from <see cref="_parentPathRefcounts"/>,
    /// at which point it stops being treated as a parent path.
    /// </summary>
    private void DecrementParentRefcount(string path)
    {
        var ancestorPath = path;
        int lastPathSeparatorIndex;
        while ((lastPathSeparatorIndex = ancestorPath.LastIndexOf(ConfigurationConstants.PathSeparator[0])) > 0)
        {
            ancestorPath = ancestorPath[..lastPathSeparatorIndex];
            if (_parentPathRefcounts[ancestorPath] == 1)
                _parentPathRefcounts.Remove(ancestorPath);
            else
                _parentPathRefcounts[ancestorPath]--;
        }
    }

    /// <summary>
    /// Finds the next <c>${...}</c> placeholder in <paramref name="sectionValue"/> starting at
    /// or after <paramref name="searchFromIndex"/>. Returns null if no complete placeholder is
    /// found. The body is split on <see cref="NullSeparator"/> into a referenced path and an
    /// optional default value.
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
    /// Returns <paramref name="sectionValue"/> with the text from the placeholder's start to its
    /// end replaced by <paramref name="replacement"/>. The placeholder's start and end indexes
    /// are inclusive of the <c>${</c> and <c>}</c> markers themselves.
    /// </summary>
    private static string SpliceReplacementIntoValue(string sectionValue, ParsedPlaceholder placeholder, string? replacement)
    {
        return sectionValue.Substring(0, placeholder.StartIndex) + replacement + sectionValue.Substring(placeholder.EndIndex + 1);
    }

    /// <summary>
    /// True when the placeholder does not span the entire value — i.e. there are characters
    /// before <c>${</c> or after the matching <c>}</c>. Object-typed references are only legal
    /// when the value IS the placeholder (so we can replace it with a subtree); embedding an
    /// object reference as a substring inside a larger string is rejected.
    /// </summary>
    private static bool IsPlaceholderEmbeddedInString(string sectionValue, ParsedPlaceholder placeholder)
    {
        return placeholder.StartIndex != 0 || placeholder.EndIndex != sectionValue.Length - 1;
    }

    /// <summary>
    /// True when <paramref name="candidatePath"/> is exactly <paramref name="path"/> or sits
    /// underneath it in the configuration tree (separated by <see cref="ConfigurationConstants.PathSeparator"/>).
    /// </summary>
    private static bool IsPathOrDescendant(string candidatePath, string path)
    {
        return candidatePath.Equals(path, StringComparison.OrdinalIgnoreCase) ||
               candidatePath.StartsWith(path + ConfigurationConstants.PathSeparator, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Returns <paramref name="path"/> with its <paramref name="sourcePath"/> prefix replaced by
    /// <paramref name="destinationPath"/>. Used to rebase keys when an object subtree is copied
    /// from one place in the configuration to another.
    /// </summary>
    private static string RebasePathPrefix(string path, string sourcePath, string destinationPath)
    {
        return path.Length == sourcePath.Length ? destinationPath : destinationPath + path[sourcePath.Length..];
    }

    /// <summary>
    /// Scans forward from <paramref name="startIndex"/> in <paramref name="str"/> to find the
    /// position of the closing <c>}</c> that matches the <c>{</c> at <paramref name="startIndex"/> - 1.
    /// Tracks brace depth so nested braces are handled correctly. Returns -1 if no match exists.
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

    /// <summary>
    /// One parsed <c>${ReferencedPath??DefaultValue}</c> placeholder occurrence inside a section's value.
    /// <c>StartIndex</c> points at the <c>$</c> of <c>${</c>; <c>EndIndex</c> points at the matching <c>}</c>.
    /// </summary>
    private readonly record struct ParsedPlaceholder(
        int StartIndex,
        int EndIndex,
        string ReferencedPath,
        string? DefaultValue);
}
