using System.Net.Security;
using System.Security.Authentication;
using MassTransit;
using PointOfSale.Infrastructure.Configuration;
using RabbitMQ.Client;

namespace PointOfSale.API.Configuration;

/// <summary>One endpoint, credentials and TLS policy for both bus and health.</summary>
public sealed class RabbitMqConnectionSettings : IDisposable
{
    private readonly ServerCertificatePolicy? _certificatePolicy;
    private readonly ClientCertificateIdentity? _identity;
    public void Dispose() => _identity?.Dispose();
    public Uri Address { get; }
    public string Host => Address.Host;
    public int Port => Address.Port;
    public string VirtualHost { get; }
    public string Username { get; }
    private string Password { get; }
    public bool TlsEnabled => Address.Scheme == "amqps";
    public string ServerName { get; }

    private RabbitMqConnectionSettings(Uri address, string virtualHost, string username, string password,
        string serverName, ServerCertificatePolicy? certificatePolicy, ClientCertificateIdentity? identity)
    {
        Address = address;
        VirtualHost = virtualHost;
        Username = username;
        Password = password;
        ServerName = serverName;
        _certificatePolicy = certificatePolicy;
        _identity = identity;
    }

    public static RabbitMqConnectionSettings FromConfiguration(IConfiguration configuration)
    {
        var section = configuration.GetSection("RabbitMQ");
        var host = section["Host"]?.Trim();
        if (string.IsNullOrWhiteSpace(host))
            throw new InvalidOperationException("RabbitMQ host is not configured.");
        if (!Uri.TryCreate(host.Contains("://", StringComparison.Ordinal) ? host : "amqp://" + host,
                UriKind.Absolute, out var endpoint) ||
            (endpoint.Scheme != "amqp" && endpoint.Scheme != "amqps") ||
            string.IsNullOrWhiteSpace(endpoint.Host) || !string.IsNullOrEmpty(endpoint.UserInfo) ||
            !string.IsNullOrEmpty(endpoint.Query) || !string.IsNullOrEmpty(endpoint.Fragment) || endpoint.Port == 0)
            throw new InvalidOperationException("RabbitMQ requires an AMQP or AMQPS endpoint without embedded credentials, query or fragment.");

        var pathVhost = endpoint.AbsolutePath is "" or "/" ? null : Uri.UnescapeDataString(endpoint.AbsolutePath[1..]);
        var configuredVhost = section["VirtualHost"];
        if (pathVhost is not null && configuredVhost is not null && pathVhost != configuredVhost)
            throw new InvalidOperationException("RabbitMQ endpoint and VirtualHost settings conflict.");
        var vhost = configuredVhost ?? pathVhost ?? "/";
        if (string.IsNullOrEmpty(vhost)) throw new InvalidOperationException("RabbitMQ VirtualHost cannot be empty.");
        var tls = endpoint.Scheme == "amqps";
        var serverName = section["Tls:ServerName"] ?? endpoint.Host;
        var caPath = section["Tls:CaCertificatePath"];
        var certificatePath = section["Tls:ClientCertificatePath"];
        var privateKeyPath = section["Tls:ClientPrivateKeyPath"];
        if (!tls && (!string.IsNullOrWhiteSpace(caPath) || section["Tls:ServerName"] is not null ||
            certificatePath is not null || privateKeyPath is not null))
            throw new InvalidOperationException("RabbitMQ TLS settings require an AMQPS endpoint.");
        if (string.IsNullOrWhiteSpace(serverName)) throw new InvalidOperationException("RabbitMQ TLS server name cannot be empty.");
        var address = new UriBuilder(endpoint)
        {
            Port = endpoint.Port > 0 ? endpoint.Port : tls ? 5671 : 5672,
            Path = vhost == "/" ? "/" : Uri.EscapeDataString(vhost)
        }.Uri;
        return new RabbitMqConnectionSettings(address, vhost, section["Username"] ?? "guest", section["Password"] ?? "guest",
            serverName, tls ? new ServerCertificatePolicy(caPath) : null,
            tls ? ClientCertificateIdentity.Load(certificatePath, privateKeyPath) : null);
    }

    public void ConfigureBus(IRabbitMqBusFactoryConfigurator configurator)
    {
        configurator.Host(Address, host =>
        {
            host.Username(Username);
            host.Password(Password);
            if (TlsEnabled)
                host.UseSsl(ssl =>
                {
                    ssl.Protocol = SslProtocols.Tls12 | SslProtocols.Tls13;
                    ssl.ServerName = ServerName;
                    ssl.Certificate = _identity!.Certificate;
                    ssl.CertificateSelectionCallback = (_, _, _, _, _) => _identity!.Certificate;
                    ssl.EnforcePolicyErrors(SslPolicyErrors.RemoteCertificateNotAvailable |
                        SslPolicyErrors.RemoteCertificateNameMismatch | SslPolicyErrors.RemoteCertificateChainErrors);
                    ssl.CertificateValidationCallback = _certificatePolicy!.Validate;
                });
        });
    }

    public ConnectionFactory CreateHealthConnectionFactory(TimeSpan timeout)
    {
        var factory = new ConnectionFactory
        {
            HostName = Host, Port = Port, VirtualHost = VirtualHost,
            UserName = Username, Password = Password,
            AutomaticRecoveryEnabled = false,
            RequestedConnectionTimeout = timeout,
            ClientProvidedName = "PointOfSale.RabbitMqDependencyHealthCheck"
        };
        if (TlsEnabled)
        {
            factory.Ssl = new SslOption
            {
                Enabled = true, ServerName = ServerName,
                Version = SslProtocols.Tls12 | SslProtocols.Tls13,
                AcceptablePolicyErrors = SslPolicyErrors.None,
                CheckCertificateRevocation = true,
                Certs = new System.Security.Cryptography.X509Certificates.X509CertificateCollection { _identity!.Certificate },
                CertificateSelectionCallback = (_, _, _, _, _) => _identity!.Certificate,
                CertificateValidationCallback = _certificatePolicy!.Validate
            };
        }
        return factory;
    }
}
