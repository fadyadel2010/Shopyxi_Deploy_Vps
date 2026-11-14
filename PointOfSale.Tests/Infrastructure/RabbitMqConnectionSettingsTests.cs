using Microsoft.Extensions.Configuration;
using PointOfSale.API.Configuration;

namespace PointOfSale.Tests.Infrastructure;

public sealed class RabbitMqConnectionSettingsTests : IDisposable
{
    private readonly MtlsTestIdentity _identity = new();
    public void Dispose() => _identity.Dispose();
    private IConfiguration Config(string host, string? vhost = null) =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["RabbitMQ:Host"] = host, ["RabbitMQ:VirtualHost"] = vhost,
            ["RabbitMQ:Username"] = "fixture-user", ["RabbitMQ:Password"] = "fixture-only",
            ["RabbitMQ:Tls:ClientCertificatePath"] = host.StartsWith("amqps://", StringComparison.Ordinal) ? _identity.CertificatePath : null,
            ["RabbitMQ:Tls:ClientPrivateKeyPath"] = host.StartsWith("amqps://", StringComparison.Ordinal) ? _identity.PrivateKeyPath : null
        }).Build();

    [Theory]
    [InlineData("localhost", 5672, false)]
    [InlineData("rabbitmq:5678", 5678, false)]
    [InlineData("amqp://rabbitmq", 5672, false)]
    [InlineData("amqps://rabbitmq", 5671, true)]
    [InlineData("amqps://rabbitmq:5678", 5678, true)]
    [InlineData("amqps://[::1]:5671", 5671, true)]
    public void BusAndHealthShareEndpointAndTls(string endpoint, int port, bool tls)
    {
        using var settings = RabbitMqConnectionSettings.FromConfiguration(Config(endpoint, "fixture"));
        var factory = settings.CreateHealthConnectionFactory(TimeSpan.FromSeconds(1));
        Assert.Equal(port, settings.Address.Port);
        Assert.Equal(port, factory.Port);
        Assert.Equal(settings.Host, factory.HostName);
        Assert.Equal("fixture", factory.VirtualHost);
        Assert.Equal(tls, factory.Ssl.Enabled);
        Assert.Equal(tls ? "amqps" : "amqp", settings.Address.Scheme);
        if (tls)
        {
            Assert.Equal(settings.Host, factory.Ssl.ServerName);
            Assert.Equal(System.Net.Security.SslPolicyErrors.None, factory.Ssl.AcceptablePolicyErrors);
            Assert.True(factory.Ssl.CheckCertificateRevocation);
            Assert.NotNull(factory.Ssl.CertificateValidationCallback);
        }
    }

    [Theory]
    [InlineData("https://rabbitmq")]
    [InlineData("amqps://user:secret@rabbitmq")]
    [InlineData("amqps://rabbitmq?password=secret")]
    [InlineData("amqps://rabbitmq#ignored")]
    [InlineData("amqps://rabbitmq:0")]
    [InlineData("")]
    public void InvalidOrAmbiguousEndpointsFailClosed(string endpoint) =>
        Assert.Throws<InvalidOperationException>(() => RabbitMqConnectionSettings.FromConfiguration(Config(endpoint)));

    [Fact]
    public void VhostInUriIsPreservedAndConflictRejected()
    {
        using var settings = RabbitMqConnectionSettings.FromConfiguration(Config("amqps://rabbitmq/a%2Fb"));
        Assert.Equal("a/b", settings.VirtualHost);
        Assert.Equal("/a%2Fb", settings.Address.AbsolutePath);
        Assert.Throws<InvalidOperationException>(() => RabbitMqConnectionSettings.FromConfiguration(Config("amqps://rabbitmq/a", "b")));
    }

    [Fact]
    public void PlaintextCannotSilentlyIgnoreTlsConfiguration()
    {
        var configuration = Config("amqp://rabbitmq");
        configuration["RabbitMQ:Tls:ServerName"] = "rabbitmq";
        Assert.Throws<InvalidOperationException>(() => RabbitMqConnectionSettings.FromConfiguration(configuration));
    }
}
