using System.Reflection;
using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;
using QaaS.Framework.Providers.Discovery;

namespace QaaS.Framework.Providers.Tests.Discovery;

[TestFixture]
[NonParallelizable]
public class PluginAssemblyDiscoveryConcurrencyTests
{
    [SetUp]
    [TearDown]
    public void ResetCache() => PluginAssemblyDiscovery.ResetCacheForTesting();

    [Test]
    public void GetCandidateAssemblies_DoesNotExposeMutableBackingArray()
    {
        var result = PluginAssemblyDiscovery.Discover(typeof(QaaS.Framework.Providers.ObjectCreation.IByNameObjectCreator).Assembly, Mock.Of<ILogger>());

        Assert.That(
            result,
            Is.Not.InstanceOf<Assembly[]>(),
            "Returning a bare Assembly[] lets callers mutate the cached state shared with concurrent readers.");
        Assert.That(((System.Collections.IList)result).IsReadOnly, Is.True);
    }

    [Test]
    public async Task GetCandidateAssemblies_ConcurrentReadersObserveStableCachedResult()
    {
        var first = PluginAssemblyDiscovery.Discover(typeof(QaaS.Framework.Providers.ObjectCreation.IByNameObjectCreator).Assembly, Mock.Of<ILogger>());

        var readers = Enumerable.Range(0, 16)
            .Select(_ => Task.Run(() => PluginAssemblyDiscovery.Discover(typeof(QaaS.Framework.Providers.ObjectCreation.IByNameObjectCreator).Assembly, Mock.Of<ILogger>())))
            .ToArray();

        var results = await Task.WhenAll(readers);

        Assert.That(results, Has.All.SameAs(first), "All concurrent readers must see the same cached instance.");
        Assert.That(results, Has.All.Matches<IReadOnlyList<Assembly>>(r => r.Count == first.Count));
    }
}
