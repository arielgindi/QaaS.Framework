using System.Collections.Concurrent;
using System.Reflection;
using Microsoft.Extensions.DependencyModel;
using Microsoft.Extensions.Logging;

namespace QaaS.Framework.Providers.Discovery;

/// <summary>
/// Resolves the assemblies that may contain plugin implementations of a given contract.
/// Walks the runtime dependency manifest (<see cref="DependencyContext.Default"/>) in reverse
/// from the contract anchor and returns every library that transitively depends on it. Falls
/// back to a base-directory DLL scan when the manifest is unusable. Successful manifest-driven
/// results are cached per contract anchor for the process lifetime; fallback results bypass the
/// cache so a transient failure cannot poison subsequent calls.
/// </summary>
internal static class PluginAssemblyDiscovery
{
    /// <summary>
    /// Per-contract-anchor cache of manifest-driven discovery results. Keyed by the anchor's
    /// full assembly name (which includes version + culture + public-key token) so a side-by-side
    /// load of two different versions of the same contract are cached independently.
    /// </summary>
    private static readonly ConcurrentDictionary<string, IReadOnlyList<Assembly>> CachedDiscoveryResults =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Returns the candidate plugin assemblies for <paramref name="contractAnchor"/>. Cached
    /// after the first manifest-driven success; fallback results are not cached.
    /// </summary>
    /// <param name="contractAnchor">Assembly that defines the contract whose implementors should be discovered.</param>
    /// <param name="logger">Logger used to record fallback decisions and load failures.</param>
    /// <returns>The candidate assemblies, in undefined order; safe to enumerate concurrently.</returns>
    public static IReadOnlyList<Assembly> Discover(Assembly contractAnchor, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(contractAnchor);
        ArgumentNullException.ThrowIfNull(logger);

        var cacheKey = contractAnchor.FullName ?? contractAnchor.GetName().Name ?? string.Empty;
        if (!string.IsNullOrEmpty(cacheKey) && CachedDiscoveryResults.TryGetValue(cacheKey, out var cached))
            return cached;

        var (assemblies, fromManifest) =
            FindCandidateAssemblies(DependencyContext.Default, contractAnchor, logger);

        if (fromManifest && !string.IsNullOrEmpty(cacheKey))
            CachedDiscoveryResults.TryAdd(cacheKey, assemblies);

        return assemblies;
    }

    /// <summary>
    /// Finds the candidate plugin assemblies for <paramref name="contractAnchor"/>. Returns the
    /// assemblies together with a flag indicating whether the manifest produced the result;
    /// the flag is <c>false</c> whenever the base-directory scan ran so the caller can skip
    /// caching that outcome.
    /// </summary>
    /// <param name="dependencyContext">Runtime dependency manifest, or <c>null</c> to force the fallback path.</param>
    /// <param name="contractAnchor">Assembly that defines the contract whose implementors should be discovered.</param>
    /// <param name="logger">Logger used to record fallback decisions and load failures.</param>
    /// <returns>
    /// <c>Assemblies</c>: the candidate assemblies.
    /// <c>FromManifest</c>: <c>true</c> when the manifest walk succeeded; <c>false</c> when the base-directory scan ran.
    /// </returns>
    internal static (IReadOnlyList<Assembly> Assemblies, bool FromManifest) FindCandidateAssemblies(
        DependencyContext? dependencyContext,
        Assembly contractAnchor,
        ILogger logger)
    {
        var contractName = contractAnchor.GetName().Name;
        if (string.IsNullOrEmpty(contractName))
        {
            logger.LogInformation("Plugin discovery falling back: contract anchor has no simple name.");
            return (ScanBaseDirectory(logger), FromManifest: false);
        }

        if (dependencyContext is null)
        {
            logger.LogInformation("Plugin discovery falling back: DependencyContext.Default is unavailable.");
            return (ScanBaseDirectory(logger), FromManifest: false);
        }

        IReadOnlySet<string> referencingAssemblyNames;
        try
        {
            referencingAssemblyNames = FindAssembliesReferencingContract(dependencyContext, contractName);
        }
        catch (Exception exception) when (!IsFatalException(exception))
        {
            logger.LogWarning(exception, "Reverse-dependency walk failed; falling back to base-directory scan.");
            return (ScanBaseDirectory(logger), FromManifest: false);
        }

        if (referencingAssemblyNames.Count == 0)
        {
            logger.LogInformation(
                "Plugin discovery falling back: contract assembly {ContractAssembly} not present in dependency manifest.",
                contractName);
            return (ScanBaseDirectory(logger), FromManifest: false);
        }

        var discoveredAssemblies = SeedFromAppDomain();
        foreach (var assemblyName in referencingAssemblyNames)
        {
            if (discoveredAssemblies.ContainsSimpleName(assemblyName))
                continue;

            try
            {
                discoveredAssemblies.Add(Assembly.Load(new AssemblyName(assemblyName)));
            }
            catch (Exception exception) when (!IsFatalException(exception))
            {
                logger.LogWarning(
                    exception,
                    "Could not load candidate plugin assembly {AssemblyName}; skipping.",
                    assemblyName);
            }
        }

        logger.LogDebug(
            "Plugin discovery resolved {Count} candidate assemblies for {ContractAssembly}.",
            discoveredAssemblies.Count,
            contractName);

        return (discoveredAssemblies.Snapshot(), FromManifest: true);
    }

    /// <summary>
    /// Walks the dependency graph in reverse, starting from every library that ships
    /// <paramref name="contractAssemblyName"/>, and returns the runtime assembly names for
    /// every library that transitively depends on it. Cycles are tolerated; disconnected
    /// libraries are excluded.
    /// </summary>
    /// <param name="dependencyContext">Manifest providing the runtime and compile graphs.</param>
    /// <param name="contractAssemblyName">Simple name of the contract assembly to root the walk at.</param>
    /// <returns>
    /// The case-insensitive set of runtime assembly simple names that own or transitively
    /// depend on <paramref name="contractAssemblyName"/>. Empty when the contract is absent
    /// from the manifest.
    /// </returns>
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

    /// <summary>
    /// Appends reverse-direction edges (dependency → dependents) from the supplied libraries
    /// into <paramref name="dependentsByLibrary"/>. Called once for runtime libraries and
    /// once for compile-only libraries so the BFS sees every link.
    /// </summary>
    /// <param name="dependentsByLibrary">Adjacency map being populated, keyed by dependency name with the list of dependents as value.</param>
    /// <param name="libraries">Source libraries with their forward dependency lists.</param>
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

    /// <summary>
    /// Returns the runtime assembly simple-names that <paramref name="library"/> contributes
    /// for the current RID. Falls back to the library name when the manifest exposes no
    /// runtime assets (e.g. metapackages).
    /// </summary>
    /// <param name="library">Library to inspect.</param>
    /// <param name="dependencyContext">Owning dependency context (provides the target RID).</param>
    /// <returns>The simple names of every runtime assembly contributed by <paramref name="library"/>; never empty.</returns>
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

    /// <summary>
    /// Last-resort discovery used when the manifest is unusable: enumerates every <c>*.dll</c>
    /// in the base directory and loads what it can. Slower than the manifest walk and not
    /// cached by the caller.
    /// </summary>
    /// <param name="logger">Logger used to record unloadable files at debug level.</param>
    /// <returns>The successfully loaded assemblies plus any already loaded into the AppDomain.</returns>
    private static IReadOnlyList<Assembly> ScanBaseDirectory(ILogger logger)
    {
        var discoveredAssemblies = SeedFromAppDomain();
        var baseDirectory = AppDomain.CurrentDomain.BaseDirectory;
        if (string.IsNullOrEmpty(baseDirectory) || !Directory.Exists(baseDirectory))
            return discoveredAssemblies.Snapshot();

        foreach (var assemblyPath in Directory.EnumerateFiles(baseDirectory, "*.dll"))
        {
            try
            {
                var assemblyName = AssemblyName.GetAssemblyName(assemblyPath);
                if (discoveredAssemblies.ContainsSimpleName(assemblyName.Name ?? string.Empty))
                    continue;

                discoveredAssemblies.Add(Assembly.LoadFrom(assemblyPath));
            }
            catch (Exception exception) when (!IsFatalException(exception))
            {
                logger.LogDebug(exception, "Skipping unloadable assembly at {AssemblyPath}.", assemblyPath);
            }
        }

        return discoveredAssemblies.Snapshot();
    }

    /// <summary>
    /// Returns a collection pre-populated with the entry assembly and every assembly currently
    /// loaded into the <see cref="AppDomain"/>, so already-resident assemblies are never reloaded
    /// by the manifest walk or the fallback scan.
    /// </summary>
    /// <returns>A fresh collection seeded with currently loaded assemblies.</returns>
    private static AssemblyCollection SeedFromAppDomain()
    {
        var discoveredAssemblies = new AssemblyCollection();
        discoveredAssemblies.Add(Assembly.GetEntryAssembly());
        foreach (var loadedAssembly in AppDomain.CurrentDomain.GetAssemblies())
            discoveredAssemblies.Add(loadedAssembly);
        return discoveredAssemblies;
    }

    /// <summary>
    /// Identifies exceptions that must never be swallowed (OOM, stack overflow, AV, thread
    /// abort). Used as a filter on every <c>catch</c> so process-fatal errors propagate.
    /// </summary>
    /// <param name="exception">Exception to classify.</param>
    /// <returns><c>true</c> when <paramref name="exception"/> must propagate; <c>false</c> when it is safe to log and continue.</returns>
    private static bool IsFatalException(Exception exception) =>
        exception is OutOfMemoryException
            or StackOverflowException
            or AccessViolationException
            or ThreadAbortException;

    /// <summary>
    /// Test-only hook that clears the per-anchor manifest cache so each test observes a fresh discovery.
    /// </summary>
    internal static void ResetCacheForTesting() => CachedDiscoveryResults.Clear();

    /// <summary>
    /// Mutable working set used during discovery. Deduplicates by full identity while also
    /// tracking simple names so the manifest walk can cheaply skip already-known libraries.
    /// </summary>
    private sealed class AssemblyCollection
    {
        private readonly Dictionary<string, Assembly> _assembliesByFullName = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _simpleNames = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Number of distinct assemblies currently in the collection.
        /// </summary>
        public int Count => _assembliesByFullName.Count;

        /// <summary>
        /// Adds <paramref name="assembly"/> if it has a usable full name and is not already
        /// present. Null, dynamic, and unnamed assemblies are silently ignored.
        /// </summary>
        /// <param name="assembly">Assembly to add; may be <c>null</c>.</param>
        public void Add(Assembly? assembly)
        {
            if (assembly is null || assembly.IsDynamic)
                return;

            var fullName = assembly.FullName;
            if (string.IsNullOrEmpty(fullName) || !_assembliesByFullName.TryAdd(fullName, assembly))
                return;

            var simpleName = assembly.GetName().Name;
            if (!string.IsNullOrEmpty(simpleName))
                _simpleNames.Add(simpleName);
        }

        /// <summary>
        /// Returns <c>true</c> when at least one stored assembly has the given simple name.
        /// </summary>
        /// <param name="simpleName">Assembly simple name to probe for.</param>
        /// <returns><c>true</c> when an assembly with that simple name is present; otherwise <c>false</c>.</returns>
        public bool ContainsSimpleName(string simpleName) => _simpleNames.Contains(simpleName);

        /// <summary>
        /// Returns an immutable view of the contained assemblies for safe handoff to callers.
        /// </summary>
        /// <returns>A snapshot list that callers may retain without affecting later mutations.</returns>
        public IReadOnlyList<Assembly> Snapshot() => [.. _assembliesByFullName.Values];
    }
}
