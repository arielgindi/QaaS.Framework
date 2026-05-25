using System.Reflection;
using Microsoft.Extensions.DependencyModel;
using Microsoft.Extensions.Logging;

namespace QaaS.Framework.Providers.Discovery;

/// <summary>
/// Resolves the assemblies that may contain plugin implementations of a given contract by combining
/// loaded AppDomain assemblies, the manifest reverse-walk from the contract anchor, and a base-directory
/// DLL scan. Successful results are cached per contract anchor for the process lifetime.
/// </summary>
public static class PluginAssemblyDiscovery
{
    private static readonly Dictionary<string, IReadOnlyList<Assembly>> CachedDiscoveryResults =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly Lock DiscoveryLock = new();

    /// <summary>
    /// Returns the candidate plugin assemblies for <paramref name="contractAnchor"/>.
    /// </summary>
    /// <param name="contractAnchor">Assembly that defines the contract whose implementors should be discovered.</param>
    /// <param name="logger">Logger used to record load failures and skipped assemblies.</param>
    /// <returns>The candidate assemblies, in undefined order; safe to enumerate concurrently.</returns>
    public static IReadOnlyList<Assembly> Discover(Assembly contractAnchor, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(contractAnchor);
        ArgumentNullException.ThrowIfNull(logger);

        var cacheKey = contractAnchor.FullName ?? contractAnchor.GetName().Name;

        lock (DiscoveryLock)
        {
            if (cacheKey is not null && CachedDiscoveryResults.TryGetValue(cacheKey, out var cached))
                return cached;

            var (assemblies, fromManifest) =
                FindCandidateAssemblies(DependencyContext.Default, contractAnchor, logger);

            if (fromManifest && cacheKey is not null)
                CachedDiscoveryResults[cacheKey] = assemblies;

            return assemblies;
        }
    }

    /// <summary>
    /// Builds the candidate set from AppDomain assemblies, the manifest reverse-walk (when usable),
    /// and a base-directory DLL scan. <c>FromManifest</c> is <c>true</c> iff the manifest walk
    /// produced at least one referencing assembly name; the caller uses this to decide whether
    /// the result is stable enough to cache.
    /// </summary>
    internal static (IReadOnlyList<Assembly> Assemblies, bool FromManifest) FindCandidateAssemblies(
        DependencyContext? dependencyContext,
        Assembly contractAnchor,
        ILogger logger)
    {
        var assembliesByFullName = new Dictionary<string, Assembly>(StringComparer.OrdinalIgnoreCase);
        var simpleNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        SeedFromAppDomain(assembliesByFullName, simpleNames);
        var fromManifest = AddManifestReferencingAssemblies(
            dependencyContext, contractAnchor, assembliesByFullName, simpleNames, logger);
        AddBaseDirectoryAssemblies(assembliesByFullName, simpleNames, logger);

        return ([.. assembliesByFullName.Values], fromManifest);
    }

    private static bool AddManifestReferencingAssemblies(
        DependencyContext? dependencyContext,
        Assembly contractAnchor,
        Dictionary<string, Assembly> assembliesByFullName,
        HashSet<string> simpleNames,
        ILogger logger)
    {
        var contractName = contractAnchor.GetName().Name;
        if (string.IsNullOrEmpty(contractName))
        {
            logger.LogInformation(
                "Plugin discovery skipping manifest walk: contract anchor has no simple name. {ContractAssembly}",
                contractAnchor.FullName);
            return false;
        }

        if (dependencyContext is null)
        {
            logger.LogInformation(
                "Plugin discovery skipping manifest walk: DependencyContext.Default is unavailable for {ContractAssembly}.",
                contractName);
            return false;
        }

        IReadOnlySet<string> referencingAssemblyNames;
        try
        {
            referencingAssemblyNames = FindAssembliesReferencingContract(dependencyContext, contractName);
        }
        catch (Exception exception) when (!IsFatalException(exception))
        {
            logger.LogWarning(exception, "Reverse-dependency walk failed; relying on AppDomain and base-directory scan.");
            return false;
        }

        if (referencingAssemblyNames.Count == 0)
        {
            logger.LogInformation(
                "Plugin discovery skipping manifest walk: contract assembly {ContractAssembly} not present in dependency manifest.",
                contractName);
            return false;
        }

        foreach (var assemblyName in referencingAssemblyNames)
        {
            if (simpleNames.Contains(assemblyName))
                continue;
            try
            {
                AddAssembly(assembliesByFullName, simpleNames, Assembly.Load(new AssemblyName(assemblyName)));
            }
            catch (Exception exception) when (!IsFatalException(exception))
            {
                logger.LogWarning(
                    exception,
                    "Could not load candidate plugin assembly {AssemblyName}; skipping.",
                    assemblyName);
            }
        }

        return true;
    }

    /// <summary>
    /// Walks the dependency graph in reverse from every library that ships <paramref name="contractAssemblyName"/>
    /// and returns the runtime assembly names of every library that transitively depends on it. Cycles are
    /// tolerated; disconnected libraries are excluded. Empty when the contract is absent from the manifest.
    /// </summary>
    internal static IReadOnlySet<string> FindAssembliesReferencingContract(
        DependencyContext dependencyContext,
        string contractAssemblyName)
    {
        var assembliesByLibrary = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        var librariesOwningAssembly = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var library in dependencyContext.RuntimeLibraries)
        {
            var names = ExtractRuntimeAssemblyNames(library, dependencyContext);
            assembliesByLibrary[library.Name] = names;
            foreach (var name in names)
            {
                if (!librariesOwningAssembly.TryGetValue(name, out var owners))
                    librariesOwningAssembly[name] = owners = [];
                owners.Add(library.Name);
            }
        }

        if (!librariesOwningAssembly.TryGetValue(contractAssemblyName, out var librariesShippingContract))
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var dependentsByLibrary = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        AddReverseEdges(dependentsByLibrary, dependencyContext.RuntimeLibraries.Select(library => (library.Name, library.Dependencies)));
        AddReverseEdges(dependentsByLibrary, dependencyContext.CompileLibraries.Select(library => (library.Name, library.Dependencies)));

        var referencingLibraries = new HashSet<string>(librariesShippingContract, StringComparer.OrdinalIgnoreCase);
        var librariesToVisit = new Queue<string>(librariesShippingContract);
        while (librariesToVisit.TryDequeue(out var currentLibrary))
        {
            if (!dependentsByLibrary.TryGetValue(currentLibrary, out var dependents))
                continue;
            foreach (var dependent in dependents)
                if (referencingLibraries.Add(dependent))
                    librariesToVisit.Enqueue(dependent);
        }

        var referencingAssemblyNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var libraryName in referencingLibraries)
            if (assembliesByLibrary.TryGetValue(libraryName, out var names))
                foreach (var name in names)
                    referencingAssemblyNames.Add(name);

        return referencingAssemblyNames;
    }

    private static void AddReverseEdges(
        Dictionary<string, List<string>> dependentsByLibrary,
        IEnumerable<(string Name, IReadOnlyList<Dependency> Dependencies)> libraries)
    {
        foreach (var (libraryName, dependencies) in libraries)
            foreach (var dependency in dependencies)
            {
                if (!dependentsByLibrary.TryGetValue(dependency.Name, out var dependents))
                    dependentsByLibrary[dependency.Name] = dependents = [];
                dependents.Add(libraryName);
            }
    }

    private static IReadOnlyList<string> ExtractRuntimeAssemblyNames(
        RuntimeLibrary library,
        DependencyContext dependencyContext)
    {
        var runtimeIdentifier = dependencyContext.Target.Runtime;
        var resolvedAssemblyNames = string.IsNullOrEmpty(runtimeIdentifier)
            ? library.GetDefaultAssemblyNames(dependencyContext)
            : library.GetRuntimeAssemblyNames(dependencyContext, runtimeIdentifier);

        var simpleNames = new List<string>();
        foreach (var assemblyName in resolvedAssemblyNames)
            if (!string.IsNullOrEmpty(assemblyName.Name))
                simpleNames.Add(assemblyName.Name);

        if (simpleNames.Count == 0)
            simpleNames.Add(library.Name);

        return simpleNames;
    }

    private static void AddBaseDirectoryAssemblies(
        Dictionary<string, Assembly> assembliesByFullName,
        HashSet<string> simpleNames,
        ILogger logger)
    {
        var baseDirectory = AppDomain.CurrentDomain.BaseDirectory;
        if (string.IsNullOrEmpty(baseDirectory) || !Directory.Exists(baseDirectory))
            return;

        foreach (var assemblyPath in Directory.EnumerateFiles(baseDirectory, "*.dll"))
        {
            try
            {
                var assemblyName = AssemblyName.GetAssemblyName(assemblyPath);
                if (simpleNames.Contains(assemblyName.Name ?? string.Empty))
                    continue;
                AddAssembly(assembliesByFullName, simpleNames, Assembly.LoadFrom(assemblyPath));
            }
            catch (Exception exception) when (!IsFatalException(exception))
            {
                logger.LogDebug(exception, "Skipping unloadable assembly at {AssemblyPath}.", assemblyPath);
            }
        }
    }

    private static void SeedFromAppDomain(
        Dictionary<string, Assembly> assembliesByFullName,
        HashSet<string> simpleNames)
    {
        AddAssembly(assembliesByFullName, simpleNames, Assembly.GetEntryAssembly());
        foreach (var loadedAssembly in AppDomain.CurrentDomain.GetAssemblies())
            AddAssembly(assembliesByFullName, simpleNames, loadedAssembly);
    }

    private static void AddAssembly(
        Dictionary<string, Assembly> assembliesByFullName,
        HashSet<string> simpleNames,
        Assembly? assembly)
    {
        if (assembly is null || assembly.IsDynamic)
            return;

        var fullName = assembly.FullName;
        if (string.IsNullOrEmpty(fullName) || !assembliesByFullName.TryAdd(fullName, assembly))
            return;

        var simpleName = assembly.GetName().Name;
        if (!string.IsNullOrEmpty(simpleName))
            simpleNames.Add(simpleName);
    }

    private static bool IsFatalException(Exception exception) =>
        exception is OutOfMemoryException
            or StackOverflowException
            or AccessViolationException
            or ThreadAbortException;

    internal static void ResetCacheForTesting()
    {
        lock (DiscoveryLock)
            CachedDiscoveryResults.Clear();
    }
}
