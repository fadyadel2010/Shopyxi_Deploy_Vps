using MongoDB.Driver;
using Npgsql;
using StackExchange.Redis;
using PointOfSale.Infrastructure.Redis.Configuration;

namespace PointOfSale.API.Configuration;

/// <summary>Offline production transport checks, before clients or workers are constructed.</summary>
public static class ProductionConfigurationPolicy
{
    public static void Validate(IConfiguration configuration, IHostEnvironment environment)
    {
        if (!environment.IsProduction()) return;
        Check("PostgreSQL", () =>
        {
            var p = new NpgsqlConnectionStringBuilder(configuration.GetConnectionString("DefaultConnection"));
            return p.SslMode == SslMode.VerifyFull && !string.IsNullOrWhiteSpace(p.Host) &&
                !string.IsNullOrWhiteSpace(p.Username) && !string.IsNullOrWhiteSpace(p.Password) &&
                !p.IncludeErrorDetail && !p.LogParameters && !p.PersistSecurityInfo;
        });
        Check("MongoDB", () =>
        {
            // Settings parsing only: never instantiate MongoClient or resolve SRV here.
            var m = MongoClientSettings.FromConnectionString(configuration["MongoDB:ConnectionString"]);
            return m.UseTls && !m.AllowInsecureTls && m.Credential is not null;
        });
        Check("Redis availability policy", () => Enabled(configuration, "Redis:Enabled", true));
        if (Enabled(configuration, "Redis:Enabled", true))
            Check("Redis", () =>
            {
                if (configuration["Redis:Topology"] is not ("direct" or "sentinel")) return false;
                var settings = configuration.GetSection("Redis").Get<RedisSettings>() ?? new RedisSettings();
                using var plan = RedisConnectionPlan.Create(settings);
                return RedisConnectionPlan.NamedTls(plan.Data);
            });
        Check("RabbitMQ", () =>
            Uri.TryCreate(configuration["RabbitMQ:Host"], UriKind.Absolute, out var uri) && uri.Scheme == "amqps" &&
            string.IsNullOrEmpty(uri.UserInfo) && !string.IsNullOrWhiteSpace(configuration["RabbitMQ:Username"]) &&
            !string.Equals(configuration["RabbitMQ:Username"], "guest", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(configuration["RabbitMQ:Password"]));
        if (Enabled(configuration, "Elasticsearch:Enabled", true))
            Check("OpenSearch", () =>
                Uri.TryCreate(configuration["Elasticsearch:Url"], UriKind.Absolute, out var uri) && uri.Scheme == "https" &&
                string.IsNullOrEmpty(uri.UserInfo) && !string.IsNullOrWhiteSpace(configuration["Elasticsearch:Username"]) &&
                !string.IsNullOrWhiteSpace(configuration["Elasticsearch:Password"]) && !configuration.GetValue("Elasticsearch:EnableDebugMode", false));
        Check("Baileys", () => SecureOrigin(configuration["BaileysService:BaseUrl"]) &&
            configuration["BaileysService:ApiKey"] is { } key && key.Trim().Length >= 32 && !key.Any(char.IsControl));
        Check("CORS", () => configuration.GetSection("Cors:AllowedOrigins").GetChildren()
            .All(origin => SecureOrigin(origin.Value)));
        Check("Known proxies", () => configuration.GetSection("ForwardedHeaders:KnownProxies").GetChildren()
            .All(proxy => System.Net.IPAddress.TryParse(proxy.Value, out var address) &&
                !address.Equals(System.Net.IPAddress.Any) && !address.Equals(System.Net.IPAddress.IPv6Any)));
        Check("Generation", () => new[] { "ExternalWritesEnabled", "AutomaticRecoveryEnabled", "AutomaticCutoverEnabled", "GenerationReadersEnabled" }
            .All(key => !configuration.GetValue("Marketplace:Generation:" + key, false)));
        Check("SyntheticQa", () => !configuration.GetValue("SyntheticQa:Enabled", false));
        Check("Database diagnostic privacy", () =>
            !configuration.GetValue("NpgsqlDiagnostics:CaptureCommandTextInActivities", false) &&
            !configuration.GetValue("NpgsqlDiagnostics:CaptureConnectionIdentityInActivities", false));
        Check("Telemetry transport", () => string.IsNullOrWhiteSpace(configuration["OpenTelemetry:OtlpEndpoint"]) ||
            (Uri.TryCreate(configuration["OpenTelemetry:OtlpEndpoint"], UriKind.Absolute, out var endpoint) &&
             endpoint.Scheme == "https" && string.IsNullOrEmpty(endpoint.UserInfo) &&
             string.IsNullOrEmpty(endpoint.Query) && string.IsNullOrEmpty(endpoint.Fragment)));
        Check("WhatsApp hourly limits", () =>
            configuration.GetValue("WhatsAppRateLimits:AccountHourlyLimit", 100) > 0 &&
            configuration.GetValue("WhatsAppRateLimits:OtpAccountHourlyLimit", 50) > 0);
    }

    private static bool SecureOrigin(string? value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        uri.Scheme == "https" && !uri.IsLoopback && !uri.Host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase) &&
        string.IsNullOrEmpty(uri.UserInfo) && string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment) &&
        uri.AbsolutePath == "/" && uri.Port > 0 && !uri.Host.Contains('*');

    private static void Check(string component, Func<bool> validate)
    {
        try { if (validate()) return; }
        catch (Exception) { }
        throw new InvalidOperationException("Production configuration rejected for " + component + ". Check required credentials, verified TLS and safe diagnostics; values are not reported.");
    }

    private static bool Enabled(IConfiguration configuration, string key, bool fallback)
    {
        try { return configuration.GetValue(key, fallback); }
        catch (Exception) { throw new InvalidOperationException("Production configuration requires a valid boolean for " + key + "; values are not reported."); }
    }
}
