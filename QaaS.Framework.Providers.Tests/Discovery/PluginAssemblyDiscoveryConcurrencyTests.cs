using System.Reflection;
using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;
using QaaS.Framework.Providers.Discovery;
using QaaS.Framework.Providers.ObjectCreation;

namespace QaaS.Framework.Providers.Tests.Discovery;

[TestFixture]
[NonParallelizable]
public class PluginAssemblyDiscoveryConcurrencyTests
{
    private static readonly Assembly ContractAnchorAssembly = typeof(IByNameObjectCreator).Assembly;

    [SetUp]
    [TearDown]
    public void ResetCache() => PluginAssemblyDiscovery.ResetCacheForTesting();

    [Test]
    public void GetCandidateAssemblies_ReturnsReadOnlyCollection()
    {
        var result = PluginAssemblyDiscovery.Discover(ContractAnchorAssembly, Mock.Of<ILogger>());

        Assert.That(((System.Collections.IList)result).IsReadOnly, Is.True,
            "Callers must not be able to mutate the cached list shared between threads.");
    }

    [Test]
    public async Task GetCandidateAssemblies_ConcurrentReadersObserveStableCachedResult()
    {
        var first = PluginAssemblyDiscovery.Discover(ContractAnchorAssembly, Mock.Of<ILogger>());

        var readers = Enumerable.Range(0, 16)
            .Select(_ => Task.Run(() => PluginAssemblyDiscovery.Discover(ContractAnchorAssembly, Mock.Of<ILogger>())))
            .ToArray();

        var results = await Task.WhenAll(readers);

        Assert.That(results, Has.All.SameAs(first), "All concurrent readers must see the same cached instance.");
        Assert.That(results, Has.All.Matches<IReadOnlyList<Assembly>>(r => r.Count == first.Count));
    }
}
