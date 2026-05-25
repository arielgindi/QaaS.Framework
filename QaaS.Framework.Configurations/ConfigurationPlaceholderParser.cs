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
    /// Clears any previous cached view of the configuration and reads it again from the current configuration.
    /// Later writes and copies update the cache without reading the whole configuration again.
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

    /// <summary>
    /// Keeps running placeholder passes until a full pass makes no changes.
    /// </summary>
    private void ResolvePlaceholderPathsUntilStable()
    {
        int modificationCountAtPassStart;
        do
        {
            modificationCountAtPassStart = _modificationCount;
            ResolveCurrentPlaceholderPathSnapshot();
        } while (_modificationCount != modificationCountAtPassStart);
    }

    /// <summary>
    /// Resolves the paths that currently contain placeholders. The list is copied first because resolving one path can
    /// add, remove, or fully resolve other paths.
    /// </summary>
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

    /// <summary>
    /// Checks whether the current cached value at this path still looks like it has a placeholder.
    /// </summary>
    private bool PathValueContainsPlaceholder(string path) =>
        TryGetValueAtPath(path, out var value) && ValueContainsPlaceholder(value);

    /// <summary>
    /// Checks only for the placeholder opening token. Full placeholder parsing happens later.
    /// </summary>
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
    private CachedConfigurationEntry? ResolvePlaceholderValue(string pathToResolve)
    {
        // The path may be gone if another placeholder copied over its parent subtree.
        if (!TryGetEntryAtPath(pathToResolve, out var entryToResolve)) return null;

        // Object-only paths have no string value, so there is nothing to scan for ${...}.
        if (!HasDirectValue(entryToResolve)) return entryToResolve;

        var nextScanIndex = 0;
        while (TryGetValueAtPath(pathToResolve, out var currentValue) && currentValue is not null)
        {
            if (TryParseNextPlaceholder(currentValue, nextScanIndex) is not { } placeholder) break;

            // Missing references without defaults intentionally stay unresolved.
            if (!PathExists(placeholder.ReferencedPath))
            {
                if (placeholder.DefaultValue is null) break;
                if (!ApplyDefaultAndResolve(pathToResolve, currentValue, placeholder)) break;
                continue;
            }

            // Resolve the source first, so ${A}->${B}->value works before replacing ${A}.
            var resolvedReference = ResolveReferencedPath(pathToResolve, placeholder);
            if (resolvedReference is null) break;
            var referencedEntry = resolvedReference.Value;

            if (!IsScalarEntry(referencedEntry))
            {
                // Object references replace the destination subtree.
                CopyObjectReference(pathToResolve, currentValue, placeholder);
                break;
            }

            // Scalar references can be inserted into the current string value.
            var replacement = referencedEntry.Value ?? string.Empty;
            ReplaceScalarPlaceholder(pathToResolve, currentValue, placeholder, replacement);
            nextScanIndex = placeholder.StartIndex + replacement.Length;
        }

        return GetEntryAtPath(pathToResolve);
    }

    /// <summary>
    /// Uses the default value for a missing reference, then resolves the same path again because the default may contain
    /// another placeholder.
    /// </summary>
    private bool ApplyDefaultAndResolve(string path, string valueAtPath, ParsedPlaceholder placeholder)
    {
        var valueWithDefaultApplied = SpliceReplacementIntoValue(
            valueAtPath,
            placeholder,
            placeholder.DefaultValue);

        WriteValueAt(path, valueWithDefaultApplied);
        return ResolvePlaceholderValue(path) is not null;
    }

    /// <summary>
    /// Resolves the path referenced by the placeholder before using its value, while tracking active paths to catch loops.
    /// </summary>
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

    /// <summary>
    /// Copies an object-shaped reference into the destination path. This is allowed only when the placeholder is the
    /// whole destination value.
    /// </summary>
    private void CopyObjectReference(string destinationPath, string valueAtDestinationPath, ParsedPlaceholder placeholder)
    {
        // Object references are only legal when ${X} is the whole value; they cannot be spliced into a string.
        if (IsPlaceholderEmbeddedInString(valueAtDestinationPath, placeholder))
            throw new FormatException(
                $"Configuration placeholder at '{destinationPath}' references object '{placeholder.ReferencedPath}', " +
                "but it is embedded inside a string.");

        CopySubtreeByPath(placeholder.ReferencedPath, destinationPath);
    }

    /// <summary>
    /// Replaces one scalar placeholder inside the current value and writes the resolved value back to the cache.
    /// </summary>
    private void ReplaceScalarPlaceholder(
        string path,
        string valueAtPath,
        ParsedPlaceholder placeholder,
        string replacement)
    {
        var resolvedValue = SpliceReplacementIntoValue(valueAtPath, placeholder, replacement);
        WriteValueAt(path, resolvedValue);
    }

    /// <summary>
    /// Creates an error that points to the path being resolved and the reference that closed the loop.
    /// </summary>
    private static InvalidOperationException CreateCircularReferenceException(string path, string referencedPath) =>
        new(
            $"Configuration placeholder loop found: '{path}' refers back to '{referencedPath}'. " +
            "Check the YAML placeholders that reference each other.");

    /// <summary>
    /// True when the cached entry has a value and no descendants.
    /// </summary>
    private bool IsScalarEntry(CachedConfigurationEntry entry) =>
        entry.Value != null && !HasDescendants(entry.Path);

    /// <summary>
    /// True when the path itself has a value. It may still have children from lower-priority configuration providers.
    /// </summary>
    private static bool HasDirectValue(CachedConfigurationEntry entry) =>
        entry.Value != null;

    /// <summary>
    /// True when another cached path exists below this path.
    /// </summary>
    private bool HasDescendants(string path) =>
        _descendantCountByParentPath.ContainsKey(path);

    /// <summary>
    /// Returns a cached entry if the path still exists; otherwise returns null.
    /// </summary>
    private CachedConfigurationEntry? GetEntryAtPath(string path) =>
        TryGetEntryAtPath(path, out var entry) ? entry : null;

    /// <summary>
    /// Checks whether the path exists in the cached configuration view.
    /// </summary>
    private bool PathExists(string path) =>
        _entriesByPath.ContainsKey(path);

    /// <summary>
    /// Reads one cached path as a small entry object with both path and value.
    /// </summary>
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

    /// <summary>
    /// Reads only the cached value for a path when the caller does not need the path wrapped with it.
    /// </summary>
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

    /// <summary>
    /// Finds the destination entries that must be removed before copying. Leaf destinations are handled without scanning
    /// the whole configuration.
    /// </summary>
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

    /// <summary>
    /// Gets all cached entries under a source path. Repeated copies from the same source reuse this list.
    /// </summary>
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

    /// <summary>
    /// Drops cached source subtrees that might include, or be included by, the path that just changed.
    /// </summary>
    private void InvalidateSourceSubtreeCache(string changedPath)
    {
        var staleSourcePaths = _sourceSubtreesByPath.Keys
            .Where(sourcePath => SourceSubtreeCacheOverlapsChange(sourcePath, changedPath))
            .ToArray();

        foreach (var cachedSourcePath in staleSourcePaths)
            _sourceSubtreesByPath.Remove(cachedSourcePath);
    }

    /// <summary>
    /// True when a cached source subtree and a changed path touch the same branch of the configuration tree.
    /// </summary>
    private static bool SourceSubtreeCacheOverlapsChange(string sourcePath, string changedPath) =>
        IsPathOrDescendant(changedPath, sourcePath) || IsPathOrDescendant(sourcePath, changedPath);

    /// <summary>
    /// Builds the public IConfiguration once after all cached placeholder changes are done.
    /// </summary>
    private void RebuildConfigurationFromEntries() =>
        _configuration = new ConfigurationBuilder().AddInMemoryCollection(_entriesByPath).Build();

    /// <summary>
    /// Adds or replaces one cached path and keeps the parent/placeholder indexes in sync.
    /// </summary>
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

    /// <summary>
    /// Removes one cached path and keeps the parent/placeholder indexes in sync.
    /// </summary>
    private void RemoveEntry(string path)
    {
        if (_entriesByPath.Remove(path))
            UpdateAncestorDescendantCounts(path, delta: -1);
        _placeholderPaths.Remove(path);
    }

    /// <summary>
    /// Keeps the placeholder path list accurate after a value changes.
    /// </summary>
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

    /// <summary>
    /// True when the candidate is exactly the path or one of its descendants.
    /// </summary>
    private static bool IsPathOrDescendant(string candidatePath, string path) =>
        candidatePath.StartsWith(path, StringComparison.OrdinalIgnoreCase) &&
        (candidatePath.Length == path.Length || candidatePath[path.Length] == PathSeparatorChar);

    /// <summary>
    /// Rewrites a copied source path so it lives under the destination path.
    /// </summary>
    private static string RebasePathPrefix(string path, string sourcePath, string destinationPath) =>
        path.Length == sourcePath.Length ? destinationPath : destinationPath + path[sourcePath.Length..];

    /// <summary>
    /// Creates one copied entry with the source prefix replaced by the destination prefix.
    /// </summary>
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

    /// <summary>
    /// The parsed pieces of one placeholder found inside a string value.
    /// </summary>
    private readonly record struct ParsedPlaceholder(
        int StartIndex,
        int EndIndex,
        string ReferencedPath,
        string? DefaultValue);

    /// <summary>
    /// A lightweight cached view of one flattened configuration path and its value.
    /// </summary>
    private readonly record struct CachedConfigurationEntry(
        string Path,
        string? Value);
}
