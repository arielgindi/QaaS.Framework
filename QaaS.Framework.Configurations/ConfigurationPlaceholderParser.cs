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
    /// Resolves all the placeholders in the configuration and returns the resolved configuration.
    /// </summary>
    public IConfiguration ResolvePlaceholders()
    {
        RebuildPathIndexAndCollectPlaceholders();

        int modificationCountAtPassStart;
        do
        {
            modificationCountAtPassStart = _modificationCount;
            foreach (var pathContainingPlaceholder in _pathsContainingPlaceholders.ToArray())
            {
                var currentValueAtPath = _configuration[pathContainingPlaceholder];
                if (currentValueAtPath is not null && currentValueAtPath.Contains(PlaceholderStart, StringComparison.Ordinal))
                    ResolvePlaceholderValue(pathContainingPlaceholder);
            }
        } while (_modificationCount != modificationCountAtPassStart);

        return _configuration;
    }

    /// <summary>
    /// Single pass over the config tree: refreshes the path-index sets and the placeholder snapshot so
    /// the outer resolver iterates only the leaves that need work and path-existence checks stay O(1).
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

    private void SetValue(string path, string? value)
    {
        _configuration[path] = value;
        RefreshPlaceholderMembership(path, value);
        _modificationCount++;
    }

    /// <summary>
    /// Resolves the placeholder for the given path, and all the dependent placeholders recursively.
    /// </summary>
    /// <param name="path">The path to the placeholder</param>
    /// <returns>The <see cref="IConfigurationSection"/> of the resolved placeholder</returns>
    private IConfigurationSection? ResolvePlaceholderValue(string path)
    {
        var currentSection = GetSectionAtPath(path);
        if (currentSection is null || !IsStringLeaf(currentSection)) return currentSection;
        var lastEnd = 0;

        while (currentSection.Value is { } sectionValue)
        {
            var placeholderStartIndex = sectionValue.IndexOf(PlaceholderStart, lastEnd, StringComparison.Ordinal);
            if (placeholderStartIndex is -1) break;

            var placeholderEndIndex = FindClosingBracket(sectionValue, placeholderStartIndex + 2);
            if (placeholderEndIndex == -1) break;

            var placeholderBody = sectionValue.Substring(placeholderStartIndex + 2, placeholderEndIndex - placeholderStartIndex - 2);
            var placeholderParts = placeholderBody.Split(NullSeparator, 2);
            var referencedPath = placeholderParts[0].Trim();
            var defaultValue = placeholderParts.Length > 1 ? placeholderParts[1].Trim() : null;

            if (_resolutionStack.Contains(referencedPath))
                throw new InvalidOperationException("Circular placeholder reference detected in configuration at: " + path);

            var referencedSection = GetSectionAtPath(referencedPath);
            if (referencedSection is null && defaultValue is null) break;

            if (referencedSection is null)
            {
                sectionValue = sectionValue.Substring(0, placeholderStartIndex) + defaultValue + sectionValue.Substring(placeholderEndIndex + 1);
                SetValue(path, sectionValue);
                currentSection = ResolvePlaceholderValue(path);
                if (currentSection is null) break;
            }
            else
            {
                // try/finally so an exception during recursion or substring-validation does not leave
                // a stale entry in _resolutionStack and make a later valid resolve look falsely circular.
                _resolutionStack.Add(referencedPath);
                try
                {
                    var resolvedSection = ResolvePlaceholderValue(referencedPath);
                    if (resolvedSection is null) break;
                    var placeholderIsEmbeddedInString = !(sectionValue.StartsWith(PlaceholderStart) &&
                                                         sectionValue.EndsWith(PlaceholderEnd) &&
                                                         sectionValue.Skip(placeholderEndIndex).Any(chr => chr == CloseCurlyBracket));

                    if (!IsStringLeaf(resolvedSection) && placeholderIsEmbeddedInString)
                        throw new InvalidOperationException("Placeholder reference to an object but is a substring value at: " + path);

                    if (!IsStringLeaf(resolvedSection))
                    {
                        CopyConfigurationsByPath(referencedPath, path);
                        currentSection = resolvedSection;
                        break;
                    }

                    sectionValue = sectionValue.Substring(0, placeholderStartIndex) + resolvedSection.Value + sectionValue.Substring(placeholderEndIndex + 1);
                    SetValue(path, sectionValue);
                    lastEnd = placeholderStartIndex + resolvedSection.Value!.Length; // Value is non-null because IsStringLeaf returned true.
                }
                finally
                {
                    _resolutionStack.Remove(referencedPath);
                }
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

    private IConfigurationSection? GetSectionAtPath(string path)
    {
        return _existingPaths.Contains(path) ? _configuration.GetSection(path) : null;
    }

    private void CopyConfigurationsByPath(string sourcePath, string destinationPath)
    {
        var allEntries = _configuration.AsEnumerable().ToList();
        var removedConfigKeys = allEntries
            .Where(kvp => IsPathOrDescendant(kvp.Key, destinationPath))
            .ToList();
        var preservedConfigKeys = allEntries
            .Where(kvp => !IsPathOrDescendant(kvp.Key, destinationPath))
            .ToList();
        var newConfigKeys = preservedConfigKeys
            .Where(kvp => IsPathOrDescendant(kvp.Key, sourcePath))
            .Select(kvp => new KeyValuePair<string, string?>(RebasePathPrefix(kvp.Key, sourcePath, destinationPath), kvp.Value))
            .ToList();
        _configuration = new ConfigurationBuilder().AddInMemoryCollection(preservedConfigKeys.Concat(newConfigKeys)).Build();
        foreach (var removedConfigKey in removedConfigKeys)
            RemovePathFromIndex(removedConfigKey.Key);
        foreach (var newConfigKey in newConfigKeys)
            AddPathToIndex(newConfigKey.Key, newConfigKey.Value);
        _modificationCount++;
    }

    private void AddPathToIndex(string path, string? value)
    {
        if (_existingPaths.Add(path))
            IncrementParentRefcount(path);
        RefreshPlaceholderMembership(path, value);
    }

    private void RemovePathFromIndex(string path)
    {
        if (_existingPaths.Remove(path))
            DecrementParentRefcount(path);
        _pathsContainingPlaceholders.Remove(path);
    }

    private void RefreshPlaceholderMembership(string path, string? value)
    {
        if (value is not null && value.Contains(PlaceholderStart, StringComparison.Ordinal))
            _pathsContainingPlaceholders.Add(path);
        else
            _pathsContainingPlaceholders.Remove(path);
    }

    private void IncrementParentRefcount(string path)
    {
        var ancestorPath = path;
        int lastPathSeparatorIndex;
        while ((lastPathSeparatorIndex = ancestorPath.LastIndexOf(ConfigurationConstants.PathSeparator[0])) > 0)
        {
            ancestorPath = ancestorPath[..lastPathSeparatorIndex];
            // 0->1 makes the path visible as a parent; later increments only keep it alive.
            _parentPathRefcounts[ancestorPath] = _parentPathRefcounts.GetValueOrDefault(ancestorPath) + 1;
        }
    }

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

    private static bool IsPathOrDescendant(string candidatePath, string path)
    {
        return candidatePath.Equals(path, StringComparison.OrdinalIgnoreCase) ||
               candidatePath.StartsWith(path + ConfigurationConstants.PathSeparator, StringComparison.OrdinalIgnoreCase);
    }

    private static string RebasePathPrefix(string path, string sourcePath, string destinationPath)
    {
        return path.Length == sourcePath.Length ? destinationPath : destinationPath + path[sourcePath.Length..];
    }

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
}
