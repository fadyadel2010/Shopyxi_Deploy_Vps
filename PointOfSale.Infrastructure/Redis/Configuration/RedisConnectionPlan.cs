using System.Security.Authentication;
using PointOfSale.Infrastructure.Configuration;
using StackExchange.Redis;

namespace PointOfSale.Infrastructure.Redis.Configuration;

/// <summary>Independent monitor/data credentials and TLS; no socket or DNS discovery here.</summary>
public sealed record RedisConnectionPlan(ConfigurationOptions Data, ConfigurationOptions? Sentinel) : IDisposable
{
    private readonly List<ClientCertificateIdentity> _identities = [];
    public void Dispose() { foreach (var identity in _identities) identity.Dispose(); }
    public static RedisConnectionPlan Create(RedisSettings settings, RedisChannel? channelPrefix = null)
    {
        var identities = new List<ClientCertificateIdentity>();
        try
        {
            if (settings.Topology is not ("direct" or "sentinel")) throw new InvalidOperationException();
            if (settings.ConnectTimeoutMs is < 1 or > 60000 || settings.SyncTimeoutMs is < 1 or > 60000 || settings.AsyncTimeoutMs is < 1 or > 60000)
                throw new InvalidOperationException();
            var data = Parse(settings.ConnectionString, settings, settings.CaCertificatePath,
                settings.ClientCertificatePath, settings.ClientPrivateKeyPath, identities);
            if (channelPrefix is { } prefix) data.ChannelPrefix = prefix;
            if (!string.IsNullOrEmpty(data.ServiceName)) throw new InvalidOperationException();
            if (settings.Topology == "direct")
            {
                if (data.EndPoints.Count == 0 || !string.IsNullOrWhiteSpace(settings.SentinelConnectionString) ||
                    !string.IsNullOrWhiteSpace(settings.SentinelServiceName) || !string.IsNullOrWhiteSpace(settings.SentinelCaCertificatePath) ||
                    !string.IsNullOrWhiteSpace(settings.SentinelClientCertificatePath) || !string.IsNullOrWhiteSpace(settings.SentinelClientPrivateKeyPath))
                    throw new InvalidOperationException();
                var direct = new RedisConnectionPlan(data, null);
                direct._identities.AddRange(identities);
                return direct;
            }
            var monitor = Parse(settings.SentinelConnectionString, settings, settings.SentinelCaCertificatePath,
                settings.SentinelClientCertificatePath, settings.SentinelClientPrivateKeyPath, identities);
            if (string.IsNullOrWhiteSpace(settings.SentinelServiceName) || monitor.EndPoints.Count == 0 ||
                !string.IsNullOrEmpty(monitor.ServiceName) || !monitor.ChannelPrefix.IsNullOrEmpty || !NamedTls(data) || !NamedTls(monitor) ||
                (data.User == monitor.User && data.Password == monitor.Password)) throw new InvalidOperationException();
            // The pinned SDK replaces data seeds with discovered primary/replica addresses.
            // ServiceName belongs only on the data request passed to GetSentinelMasterConnection.
            data.ServiceName = settings.SentinelServiceName;
            var plan = new RedisConnectionPlan(data, monitor);
            plan._identities.AddRange(identities);
            return plan;
        }
        catch (Exception)
        {
            foreach (var identity in identities) identity.Dispose();
            throw new InvalidOperationException("Redis configuration requires an explicit supported topology, valid bounded options and independent verified Sentinel/data credentials; values are not reported.");
        }
    }

    public static bool NamedTls(ConfigurationOptions options) => options.Ssl &&
        !string.IsNullOrWhiteSpace(options.User) && !string.IsNullOrWhiteSpace(options.Password);

    private static ConfigurationOptions Parse(string value, RedisSettings settings, string? caPath,
        string? certificatePath, string? privateKeyPath, List<ClientCertificateIdentity> identities)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new InvalidOperationException();
        var options = ConfigurationOptions.Parse(value);
        options.ConnectTimeout = settings.ConnectTimeoutMs;
        options.SyncTimeout = settings.SyncTimeoutMs;
        options.AsyncTimeout = settings.AsyncTimeoutMs;
        options.ConnectRetry = 3;
        options.IncludeDetailInExceptions = false;
        options.IncludePerformanceCountersInExceptions = false;
        options.AbortOnConnectFail = false;
        if (options.Ssl)
        {
            var identity = ClientCertificateIdentity.Load(certificatePath, privateKeyPath);
            identities.Add(identity);
            options.CertificateSelection += (_, _, _, _, _) => identity.Certificate;
            options.SslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13;
            options.CheckCertificateRevocation = true;
            if (!string.IsNullOrWhiteSpace(caPath))
            {
                var policy = new ServerCertificatePolicy(caPath);
                options.CertificateValidation += policy.Validate;
            }
        }
        else if (!string.IsNullOrWhiteSpace(caPath) || !string.IsNullOrWhiteSpace(certificatePath) ||
                 !string.IsNullOrWhiteSpace(privateKeyPath)) throw new InvalidOperationException();
        return options;
    }
}
