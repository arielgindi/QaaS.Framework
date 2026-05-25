using Microsoft.Extensions.Configuration;

namespace QaaS.Framework.Configurations;

/// <summary>
/// Class that contains functionality for parsing the Placeholder values in a configuration
/// </summary>
public class ConfigurationPlaceholderParser(IConfiguration configuration)
{
    private const string Prefix = "${";
    private const string Suffix = "}";
    private const string NullSeparator = "??";
    private const char OpenCurlyBracket = '{';
    private const char CloseCurlyBracket = '}';

    private readonly HashSet<string> _resolutionStack = new();
    private readonly HashSet<string> _existingPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _parentPaths = new(StringComparer.OrdinalIgnoreCase);
    private List<string> _pathsContainingPlaceholders = [];
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
            foreach (var pathContainingPlaceholder in _pathsContainingPlaceholders)
            {
                var currentValueAtPath = configuration[pathContainingPlaceholder];
                if (currentValueAtPath is not null && currentValueAtPath.Contains(Prefix, StringComparison.Ordinal))
                    ResolvePlaceholderValue(pathContainingPlaceholder);
            }
        } while (_modificationCount != modificationCountAtPassStart);

        return configuration;
    }

    // Single pass over the config tree: refreshes the path-index sets and the placeholder snapshot so
    // the outer resolver iterates only the leaves that need work and path-existence checks stay O(1).
    private void RebuildPathIndexAndCollectPlaceholders()
    {
        _existingPaths.Clear();
        _parentPaths.Clear();
        var pathsContainingPlaceholders = new List<string>();
        foreach (var configurationEntry in configuration.AsEnumerable())
        {
            _existingPaths.Add(configurationEntry.Key);
            var ancestorPath = configurationEntry.Key;
            int lastPathSeparatorIndex;
            while ((lastPathSeparatorIndex = ancestorPath.LastIndexOf(ConfigurationConstants.PathSeparator[0])) > 0)
            {
                ancestorPath = ancestorPath[..lastPathSeparatorIndex];
                if (!_parentPaths.Add(ancestorPath)) break;
            }
            if (configurationEntry.Value is { } entryValue && entryValue.Contains(Prefix, StringComparison.Ordinal))
                pathsContainingPlaceholders.Add(configurationEntry.Key);
        }
        _pathsContainingPlaceholders = pathsContainingPlaceholders;
    }

    private void SetValue(string path, string? value)
    {
        configuration[path] = value;
        _modificationCount++;
    }

    /// <summary>
    /// Resolves the place holder for the given paths, and all the dependent placeholders recursively
    /// </summary>
    /// <param name="path">The path to the placeholder</param>
    /// <returns>The <see cref="IConfigurationSection"/> of the resolved placeholder</returns>
    private IConfigurationSection ResolvePlaceholderValue(string path)
    {
        var currentSection = GetObjectFromConfiguration(path);
        if (currentSection is null || !IsConfigurationSectionString(currentSection)) return currentSection!;
        var lastEnd = 0;

        while (currentSection.Value is not null)
        {
            var sectionValue = currentSection.Value;
            var placeholderStartIndex = sectionValue?.IndexOf(Prefix, lastEnd, StringComparison.Ordinal) ?? -1;
            if (placeholderStartIndex is -1) break; // If no Prefix for a placeholder was found, break.

            var end = FindClosingBracket(sectionValue!, placeholderStartIndex + 2);
            if (end == -1) break; // Continues only if the section has a string value containing placeholder.

            // Finds the placeholder value path and default value.
            var placeholder = sectionValue!.Substring(placeholderStartIndex + 2, end - placeholderStartIndex - 2);
            var placeholderParts = placeholder.Split(NullSeparator, 2);
            var placeholderValuePath = placeholderParts[0].Trim();
            var defaultValue = placeholderParts.Length > 1 ? placeholderParts[1].Trim() : null;

            if (_resolutionStack.Contains(placeholderValuePath))
                throw new InvalidOperationException("Circular placeholder reference detected in configuration at: " +
                                                    path);

            var placeholderResolvedConfigurationObject = GetObjectFromConfiguration(placeholderValuePath);
            if (placeholderResolvedConfigurationObject == null && defaultValue == null) break;

            // If placeholder was not found but there is a default value, sets the default value to be the placeholder value and call the function again.
            if (placeholderResolvedConfigurationObject == null)
            {
                sectionValue = sectionValue.Substring(0, placeholderStartIndex) + defaultValue +
                               sectionValue.Substring(end + 1);
                SetValue(path, sectionValue);
                currentSection = ResolvePlaceholderValue(path);
            }
            else
            {
                // try/finally so an exception during recursion or substring-validation does not leave
                // a stale entry in _resolutionStack and make a later valid resolve look falsely circular.
                _resolutionStack.Add(placeholderValuePath);
                try
                {
                    var resolvedSection = ResolvePlaceholderValue(placeholderValuePath);
                    var hasLeadingTrailingCharsFromPlaceholder = !(sectionValue.StartsWith(Prefix) &&
                                                                   sectionValue.EndsWith(Suffix) && sectionValue.Skip(end)
                                                                       .Any(chr => chr == CloseCurlyBracket));

                    if (!IsConfigurationSectionString(resolvedSection) && hasLeadingTrailingCharsFromPlaceholder)
                        throw new InvalidOperationException(
                            "Placeholder reference to an object but is a substring value at: " + path);

                    if (!IsConfigurationSectionString(resolvedSection))
                    {
                        CopyConfigurationsByPath(placeholderValuePath, path);
                        currentSection = resolvedSection;
                        break;
                    }

                    // If the placeholder value is a string, replaces the placeholder with the string value and continues to find another placeholders.
                    sectionValue = sectionValue.Substring(0, placeholderStartIndex) + resolvedSection.Value +
                                   sectionValue.Substring(end + 1);
                    currentSection.Value = sectionValue;
                    SetValue(path, sectionValue);
                    lastEnd = placeholderStartIndex + resolvedSection.Value!.Length; // Section is tested not to be null at IsConfigurationSectionString
                }
                finally
                {
                    _resolutionStack.Remove(placeholderValuePath);
                }
            }

        }

        return currentSection;
    }

    // A section is a "string leaf" when it has a value AND no descendants — i.e. its path
    // is not an ancestor of any other key, tracked in _parentPaths by RebuildPathIndexAndCollectPlaceholders.
    private bool IsConfigurationSectionString(IConfigurationSection section)
    {
        return section.Value != null && !_parentPaths.Contains(section.Path);
    }

    /// <summary>
    /// Gets the configuration object from the path
    /// </summary>
    private IConfigurationSection GetObjectFromConfiguration(string path)
    {
        return _existingPaths.Contains(path) ? configuration.GetSection(path) : null!;
    }

    /// <summary>
    /// Copies the configuration object from the source path to destination path
    /// </summary>
    private void CopyConfigurationsByPath(string sourcePath, string destinationPath)
    {
        var configKeys = configuration.AsEnumerable()
            .Where(kvp => !(kvp.Key.Equals(destinationPath) || kvp.Key.StartsWith(destinationPath + ConfigurationConstants.PathSeparator))).ToList();
        var newConfigKeys = configKeys.Where(kvp => kvp.Key.Equals(sourcePath) ||kvp.Key.StartsWith(sourcePath + ConfigurationConstants.PathSeparator))
            .Select(kvp => new KeyValuePair<string, string?>(kvp.Key.Replace(sourcePath, destinationPath), kvp.Value))
            .ToList();
        configKeys = configKeys.Concat(newConfigKeys).ToList();
        configuration = new ConfigurationBuilder().AddInMemoryCollection(configKeys).Build();
        _modificationCount++;
        // Tree was replaced — rebuild indexes so the outer pass sees the newly-copied paths.
        RebuildPathIndexAndCollectPlaceholders();
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