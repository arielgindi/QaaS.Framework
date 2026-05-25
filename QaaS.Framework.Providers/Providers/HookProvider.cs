using System.Reflection;
using Microsoft.Extensions.Logging;
using QaaS.Framework.Providers.Discovery;
using QaaS.Framework.Providers.ObjectCreation;
using QaaS.Framework.SDK.ContextObjects;
using QaaS.Framework.SDK.Hooks;

namespace QaaS.Framework.Providers.Providers;

/// <inheritdoc />
public class HookProvider<THook> : IHookProvider<THook> where THook : IHook
{
    private readonly Context _context;
    private readonly Assembly[] _hookAssemblies;
    private readonly Lock _hookTypeCacheLock = new();
    private readonly IByNameObjectCreator _objectCreator;
    private readonly Dictionary<string, Type[]> _supportedHookTypesByAssembly = new(StringComparer.Ordinal);

    /// <summary>
    /// Constructor
    /// </summary>
    /// <param name="context"> The context to initialize hooks with </param>
    /// <param name="objectCreator"> The object creator used to create hooks </param>
    public HookProvider(Context context, IByNameObjectCreator objectCreator)
    {
        _context = context;
        _objectCreator = objectCreator;
        _hookAssemblies = PluginAssemblyDiscovery
            .Discover(typeof(THook).Assembly, _context.Logger)
            .OrderBy(GetAssemblyPriority)
            .ThenBy(assembly => assembly.FullName ?? assembly.GetName().Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static int GetAssemblyPriority(Assembly assembly)
    {
        var assemblyName = assembly.GetName().Name ?? string.Empty;
        if (assemblyName.StartsWith("QaaS.", StringComparison.OrdinalIgnoreCase))
            return 0;
        if (assemblyName.StartsWith("Common.", StringComparison.OrdinalIgnoreCase))
            return 1;
        return 2;
    }

    private Type[] GetSupportedHookTypesFromAssembly(Assembly assembly)
    {
        var assemblyKey = assembly.FullName ?? assembly.GetName().Name ?? assembly.ToString();

        lock (_hookTypeCacheLock)
        {
            if (_supportedHookTypesByAssembly.TryGetValue(assemblyKey, out var cachedTypes))
                return cachedTypes;
        }

        Type[] loadableTypes;
        try
        {
            loadableTypes = assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException reflectionTypeLoadException)
        {
            loadableTypes = reflectionTypeLoadException.Types.Where(type => type is not null).ToArray()!;
            _context.Logger.LogDebug(
                "Partially loaded assembly {AssemblyFullName} while searching for {HookType} hooks. " +
                "Continuing with {ResolvedTypeCount} loadable types.",
                assembly.FullName, typeof(THook).FullName, loadableTypes.Length);
        }
        catch (Exception unexpectedException)
        {
            _context.Logger.LogDebug(
                "Could not search assembly {AssemblyFullName} for {HookType} hooks, skipping it.\n " +
                "Encountered the following exception when searching it:\n {Exception}",
                assembly.FullName, typeof(THook).FullName, unexpectedException);
            loadableTypes = [];
        }

        var supportedHookTypesInAssembly = loadableTypes.Where(_objectCreator.IsTypeSubClassOfT<THook>).ToArray();
        lock (_hookTypeCacheLock)
        {
            // First-writer-wins: a peer thread that ran through the slow path concurrently may
            // have observed a successful GetTypes while we observed a transient failure (empty).
            // Returning the already-cached value preserves that successful result.
            if (_supportedHookTypesByAssembly.TryGetValue(assemblyKey, out var existingCachedHookTypes))
                return existingCachedHookTypes;
            _supportedHookTypesByAssembly[assemblyKey] = supportedHookTypesInAssembly;
        }

        return supportedHookTypesInAssembly;
    }

    private Type ResolveSupportedHookType(string instanceName)
    {
        var isExactTypeName = instanceName.Contains('.', StringComparison.Ordinal) ||
                              instanceName.Contains(',', StringComparison.Ordinal);
        if (isExactTypeName)
        {
            Type? fullNameMatch = null;
            foreach (var hookAssembly in _hookAssemblies)
            {
                var fullNameMatchesInAssembly = GetSupportedHookTypesFromAssembly(hookAssembly)
                    .Where(type => string.Equals(type.FullName, instanceName, StringComparison.Ordinal) ||
                                   string.Equals(type.AssemblyQualifiedName, instanceName, StringComparison.Ordinal))
                    .Distinct()
                    .ToList();

                if (fullNameMatchesInAssembly.Count > 1)
                    throw new ArgumentException(
                        $"Found multiple {typeof(THook).Name} hook instances with the exact type name {instanceName}. " +
                        "Use the hook's assembly-qualified name instead." +
                        $"\n- {string.Join("\n- ", fullNameMatchesInAssembly.Select(type => $"{type.FullName} ({type.Assembly.FullName})"))}");

                if (fullNameMatchesInAssembly.Count == 1)
                {
                    if (fullNameMatch is not null)
                        throw new ArgumentException(
                            $"Found multiple {typeof(THook).Name} hook instances with the exact type name {instanceName}. " +
                            "Use the hook's assembly-qualified name instead." +
                            $"\n- {fullNameMatch.FullName} ({fullNameMatch.Assembly.FullName})" +
                            $"\n- {fullNameMatchesInAssembly[0].FullName} ({fullNameMatchesInAssembly[0].Assembly.FullName})");

                    fullNameMatch = fullNameMatchesInAssembly[0];
                }
            }

            if (fullNameMatch is not null)
                return fullNameMatch;
        }

        var simpleNameMatches = new List<Type>();
        foreach (var hookAssembly in _hookAssemblies)
        {
            var simpleNameMatchesInAssembly = GetSupportedHookTypesFromAssembly(hookAssembly)
                .Where(type => string.Equals(type.Name, instanceName, StringComparison.Ordinal))
                .Distinct()
                .ToList();

            if (simpleNameMatchesInAssembly.Count > 1)
                throw new ArgumentException(
                    $"Found multiple {typeof(THook).Name} hook instances named {instanceName} in assembly {hookAssembly.FullName}. " +
                    "Use the hook's full type name instead." +
                    $"\n- {string.Join("\n- ", simpleNameMatchesInAssembly.Select(type => type.FullName))}");

            if (simpleNameMatchesInAssembly.Count == 1)
                simpleNameMatches.Add(simpleNameMatchesInAssembly[0]);
        }

        if (simpleNameMatches.Count == 1)
            return simpleNameMatches[0];

        if (simpleNameMatches.Count > 1)
        {
            var resolvedType = simpleNameMatches[0];
            _context.Logger.LogInformation(
                "Found multiple {HookType} hook instances named {InstanceName}. Resolving to {ResolvedHookType} " +
                "from assembly {AssemblyName} because it appears first in hook discovery order. Candidates:{CandidateList}",
                typeof(THook).Name,
                instanceName,
                resolvedType.FullName,
                resolvedType.Assembly.FullName,
                $"{Environment.NewLine}- " +
                string.Join(
                    $"{Environment.NewLine}- ",
                    simpleNameMatches.Select(type => $"{type.FullName} ({type.Assembly.FullName})")));
            return resolvedType;
        }

        throw new ArgumentException($"{typeof(THook).Name} hook instance {instanceName} " +
                                     "not found in any of the provided assemblies." +
                                     $"\n- {string.Join("\n- ", _hookAssemblies.Select(asm => asm.FullName))}");
    }

    private THook GetInstanceFromResolvedType(Type hookType)
    {
        var hookInstance = _objectCreator.GetInstanceOfSubClassOfTByNameFromAssemblies<THook>(
            hookType.FullName!,
            [hookType.Assembly]);
        hookInstance.Context = _context;
        return hookInstance;
    }

    /// <inheritdoc />
    public THook GetSupportedInstanceByName(string instanceName)
    {
        _context.Logger.LogDebug("Looking for {HookType} hook instance {InstanceName} in provided assemblies"
            , typeof(THook).Name, instanceName);
        var hookType = ResolveSupportedHookType(instanceName);
        _context.Logger.LogInformation("Found {HookType} hook instance {InstanceName} in provided assembly {AssemblyName}",
            typeof(THook).Name, instanceName, hookType.Assembly.FullName);
        return GetInstanceFromResolvedType(hookType);
    }
}
