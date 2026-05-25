using Microsoft.Extensions.Configuration;

namespace QaaS.Framework.Configurations.Tests;

[TestFixture]
public class ConfigurationPlaceholderParserRepeatedObjectCopyTests
{
    [Test]
    public void ResolvePlaceholders_WhenMultipleDestinationsReferenceSameObject_CopiesResolvedSubtreeToEachDestination()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["configuration:shared:rabbitmq:host"] = "${configuration:runtime:rabbitmq:host}",
                ["configuration:shared:rabbitmq:port"] = "${configuration:runtime:rabbitmq:port}",
                ["configuration:shared:rabbitmq:exchange"] = "qaas.events",
                ["configuration:shared:rabbitmq:credentials:username"] =
                    "${configuration:secrets:rabbitmq:username}",
                ["configuration:runtime:rabbitmq:host"] = "rabbitmq.internal",
                ["configuration:runtime:rabbitmq:port"] = "5672",
                ["configuration:secrets:rabbitmq:username"] = "qaas-user",
                ["configuration:services:orders:rabbitmq"] = "${configuration:shared:rabbitmq}",
                ["configuration:services:billing:rabbitmq"] = "${configuration:shared:rabbitmq}"
            })
            .Build();

        var parsed = new ConfigurationPlaceholderParser(configuration).ResolvePlaceholders();

        Assert.Multiple(() =>
        {
            Assert.That(parsed["configuration:services:orders:rabbitmq:host"], Is.EqualTo("rabbitmq.internal"));
            Assert.That(parsed["configuration:services:orders:rabbitmq:port"], Is.EqualTo("5672"));
            Assert.That(parsed["configuration:services:orders:rabbitmq:exchange"], Is.EqualTo("qaas.events"));
            Assert.That(parsed["configuration:services:orders:rabbitmq:credentials:username"],
                Is.EqualTo("qaas-user"));

            Assert.That(parsed["configuration:services:billing:rabbitmq:host"], Is.EqualTo("rabbitmq.internal"));
            Assert.That(parsed["configuration:services:billing:rabbitmq:port"], Is.EqualTo("5672"));
            Assert.That(parsed["configuration:services:billing:rabbitmq:exchange"], Is.EqualTo("qaas.events"));
            Assert.That(parsed["configuration:services:billing:rabbitmq:credentials:username"],
                Is.EqualTo("qaas-user"));
        });
    }
}
