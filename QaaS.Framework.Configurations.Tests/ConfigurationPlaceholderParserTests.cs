using Microsoft.Extensions.Configuration;

namespace QaaS.Framework.Configurations.Tests;

[TestFixture]
public class ConfigurationPlaceholderParserTests
{
    [Test]
    public void ResolvePlaceholders_ObjectCopyDiscoversCopiedNestedPlaceholder()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["CopyTarget"] = "${Source}",
                ["Source:Child:Value"] = "${CopyTarget:Values:Resolved}",
                ["Source:Child:Literal"] = "kept",
                ["Source:Values:Resolved"] = "from-values"
            })
            .Build();

        var parsed = new ConfigurationPlaceholderParser(configuration).ResolvePlaceholders();

        Assert.Multiple(() =>
        {
            Assert.That(parsed["CopyTarget:Child:Literal"], Is.EqualTo("kept"));
            Assert.That(parsed["CopyTarget:Child:Value"], Is.EqualTo("from-values"));
        });
    }
}
