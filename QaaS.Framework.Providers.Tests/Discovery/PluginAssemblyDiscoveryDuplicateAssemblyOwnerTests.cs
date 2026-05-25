using Microsoft.Extensions.DependencyModel;
using NUnit.Framework;
using QaaS.Framework.Providers.Discovery;

namespace QaaS.Framework.Providers.Tests.Discovery;

// When two RuntimeLibrary entries each ship the contract assembly under the same simple name
// (e.g. a primary package + back-compat shim), the reverse-walk must seed from BOTH owning
// libraries. Regression guard: prior implementation used TryAdd and silently dropped the second
// library's subgraph.
[TestFixture]
public class PluginAssemblyDiscoveryDuplicateAssemblyOwnerTests
{
    private const string ContractAssembly = "Contract.Anchor";

    [Test]
    public void FindAssembliesReferencingContract_WhenContractAssemblyIsOwnedByTwoLibraries_WalksBothSubgraphs()
    {
        // Two packages each ship lib/net10.0/Contract.Anchor.dll.
        var contractPrimary = LibraryProducing(
            packageId: "Contract.Primary",
            assetPaths: new[] { "lib/net10.0/Contract.Anchor.dll" });

        var contractShim = LibraryProducing(
            packageId: "Contract.Shim",
            assetPaths: new[] { "lib/net10.0/Contract.Anchor.dll" });

        // Plugin.Primary depends on the primary package; Plugin.Shim depends on the shim.
        // Both legitimately consume the Contract.Anchor assembly at runtime and therefore
        // both should appear in the reverse-dependency closure of `Contract.Anchor`.
        var pluginPrimary = LibraryProducing(
            packageId: "Plugin.Primary",
            assetPaths: new[] { "lib/net10.0/Plugin.Primary.dll" },
            dependencies: new[] { "Contract.Primary" });

        var pluginShim = LibraryProducing(
            packageId: "Plugin.Shim",
            assetPaths: new[] { "lib/net10.0/Plugin.Shim.dll" },
            dependencies: new[] { "Contract.Shim" });

        var context = BuildContext(contractPrimary, contractShim, pluginPrimary, pluginShim);

        var closure = PluginAssemblyDiscovery.FindAssembliesReferencingContract(context, ContractAssembly);

        Assert.That(closure, Does.Contain("Plugin.Primary"),
            "Plugin.Primary depends on Contract.Primary which ships the contract assembly; it must be in the closure.");
        Assert.That(closure, Does.Contain("Plugin.Shim"),
            "Plugin.Shim depends on Contract.Shim which also ships the contract assembly; it must be in the closure too. "
            + "Current implementation registers only the FIRST library as the owner of the assembly name via TryAdd, "
            + "so the BFS seed misses the second subgraph.");
    }

    private static DependencyContext BuildContext(params RuntimeLibrary[] libraries) =>
        new(
            new TargetInfo("net10.0", null, null, isPortable: true),
            CompilationOptions.Default,
            Array.Empty<CompilationLibrary>(),
            libraries,
            Array.Empty<RuntimeFallbacks>());

    private static RuntimeLibrary LibraryProducing(
        string packageId,
        string[] assetPaths,
        string[]? dependencies = null) =>
        new(
            type: "package",
            name: packageId,
            version: "1.0.0",
            hash: string.Empty,
            runtimeAssemblyGroups: new[]
            {
                new RuntimeAssetGroup(string.Empty, assetPaths)
            },
            nativeLibraryGroups: Array.Empty<RuntimeAssetGroup>(),
            resourceAssemblies: Array.Empty<ResourceAssembly>(),
            dependencies: (dependencies ?? Array.Empty<string>())
                .Select(d => new Dependency(d, "1.0.0"))
                .ToArray(),
            serviceable: true);
}
