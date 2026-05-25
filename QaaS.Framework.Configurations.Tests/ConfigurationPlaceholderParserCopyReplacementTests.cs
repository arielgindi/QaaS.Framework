using Microsoft.Extensions.Configuration;

namespace QaaS.Framework.Configurations.Tests;

[TestFixture]
public class ConfigurationPlaceholderParserCopyReplacementTests
{
    [Test]
    public void ResolvePlaceholders_ObjectCopyIntoExistingSubtree_ReplacesDestinationSubtree()
    {
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["Source:Nested:Value"] = "copied",
            ["Source:OnlyInSource"] = "kept",
            ["Destination"] = "${Source}",
            ["Destination:Nested:Stale"] = "removed",
            ["Destination:OnlyInDestination"] = "removed"
        });

        var parsed = new ConfigurationPlaceholderParser(configuration).ResolvePlaceholders();

        Assert.Multiple(() =>
        {
            Assert.That(parsed["Destination:Nested:Value"], Is.EqualTo("copied"));
            Assert.That(parsed["Destination:OnlyInSource"], Is.EqualTo("kept"));
            Assert.That(parsed["Destination:Nested:Stale"], Is.Null);
            Assert.That(parsed["Destination:OnlyInDestination"], Is.Null);
            Assert.That(parsed["Destination"], Is.Null);
        });
    }

    [Test]
    public void ResolvePlaceholders_ObjectCopyIntoLeafDestination_ReplacesLeafWithSourceSubtree()
    {
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["Source:Nested:Value"] = "copied",
            ["Destination"] = "${Source}"
        });

        var parsed = new ConfigurationPlaceholderParser(configuration).ResolvePlaceholders();

        Assert.Multiple(() =>
        {
            Assert.That(parsed["Destination:Nested:Value"], Is.EqualTo("copied"));
            Assert.That(parsed["Destination"], Is.Null);
        });
    }

    [Test]
    public void ResolvePlaceholders_RepeatedObjectCopiesCreateIndependentDestinations()
    {
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["Source:Nested:Value"] = "copied",
            ["FirstCopy"] = "${Source}",
            ["SecondCopy"] = "${Source}"
        });

        var parsed = new ConfigurationPlaceholderParser(configuration).ResolvePlaceholders();

        parsed["FirstCopy:Nested:Value"] = "changed-after-copy";

        Assert.Multiple(() =>
        {
            Assert.That(parsed["FirstCopy:Nested:Value"], Is.EqualTo("changed-after-copy"));
            Assert.That(parsed["SecondCopy:Nested:Value"], Is.EqualTo("copied"));
            Assert.That(parsed["Source:Nested:Value"], Is.EqualTo("copied"));
        });
    }

    private static IConfiguration BuildConfiguration(Dictionary<string, string?> values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();
}
