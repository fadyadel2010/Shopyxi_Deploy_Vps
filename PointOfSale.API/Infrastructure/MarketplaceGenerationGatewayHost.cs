using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Npgsql;
using PointOfSale.Infrastructure.Marketplace.Generation;

namespace PointOfSale.API.Infrastructure;

/// <summary>
/// Separate executable mode, before ALL application registrations, jobs, migrations
/// and initializers. Default disabled. Its provider/SQL secrets must be isolated
/// from application principals, which must have no Generation update grant.
/// </summary>
internal static class MarketplaceGenerationGatewayHost
{
    internal static async Task RunAsync()
    {
        if (Environment.GetEnvironmentVariable("SHOPIXY_GENERATION_GATEWAY_ENABLED") != "true")
            throw new InvalidOperationException("GENERATION_GATEWAY_RELEASE_AUTHORIZATION_REQUIRED");
        static string Required(string key) => Environment.GetEnvironmentVariable("SHOPIXY_GENERATION_GATEWAY_" + key) is { Length: > 0 } value
            ? value : throw new InvalidOperationException("Gateway configuration missing: " + key);
        var endpoint = new Uri(Required("LISTEN"));
        if (endpoint.Scheme != "https" || endpoint.AbsolutePath != "/" || endpoint.Query.Length != 0 || endpoint.Fragment.Length != 0 ||
            endpoint.UserInfo.Length != 0 || !IPAddress.TryParse(endpoint.Host, out var address) ||
            address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any))
            throw new InvalidOperationException("Gateway requires one explicit HTTPS listening IP, not a wildcard or URL prefix.");
        var pins = Required("CLIENT_CERT_SHA256").Split(',', StringSplitOptions.TrimEntries);
        if (pins.Length == 0 || pins.Any(pin => pin.Length != 64 || pin.Any(c => !char.IsAsciiHexDigit(c))))
            throw new InvalidOperationException("Exact mTLS client certificate SHA-256 pins required.");
        using var certificate = X509CertificateLoader.LoadPkcs12FromFile(Required("SERVER_CERT_PFX"), Required("SERVER_CERT_PASSWORD"));
        if (!certificate.HasPrivateKey) throw new InvalidOperationException("Gateway server certificate private key required.");
        var connection = new NpgsqlConnectionStringBuilder(Required("POSTGRES"));
        if (connection.SslMode != SslMode.VerifyFull || string.IsNullOrWhiteSpace(connection.RootCertificate))
            throw new InvalidOperationException("Gateway SQL requires VerifyFull and an explicit CA.");
        if (string.IsNullOrWhiteSpace(connection.Host) || connection.Host.Contains(','))
            throw new InvalidOperationException("One authoritative PostgreSQL origin required; automatic multi-host failover is not certified.");
        await using var source = NpgsqlDataSource.Create(connection.ConnectionString);
        using var gateway = new MarketplaceGenerationGatewayServer(source, new Uri(Required("PROVIDER")),
            Required("PROVIDER_USERNAME"), Required("PROVIDER_PASSWORD"), Required("PROVIDER_CA"));
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [], ApplicationName = typeof(MarketplaceGenerationGatewayHost).Assembly.GetName().Name });
        builder.Configuration.Sources.Clear();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(server =>
        {
            server.Limits.MaxRequestBodySize = 16 * 1024 * 1024;
            server.Listen(address, endpoint.Port, listen => listen.UseHttps(tls =>
            {
                tls.ServerCertificate = certificate;
                tls.ClientCertificateMode = ClientCertificateMode.RequireCertificate;
                tls.ClientCertificateValidation = (peer, _, _) => peer.NotBefore.ToUniversalTime() <= DateTime.UtcNow &&
                    peer.NotAfter.ToUniversalTime() > DateTime.UtcNow && pins.Any(pin =>
                        CryptographicOperations.FixedTimeEquals(Convert.FromHexString(pin), peer.GetCertHash(HashAlgorithmName.SHA256)));
            }));
        });
        await using var app = builder.Build();
        app.MapPost("/generation/{phase}", async (string phase, HttpContext context) =>
        {
            if (phase is not ("lookup" or "submit" or "verify")) return Results.NotFound();
            try
            {
                using var budget = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
                budget.CancelAfter(TimeSpan.FromSeconds(30));
                using var reader = new StreamReader(context.Request.Body);
                var body = await reader.ReadToEndAsync(budget.Token);
                var receipt = await gateway.ExecuteAsync(phase, body, budget.Token);
                return Results.Text(receipt, "application/json");
            }
            catch (Exception) { return Results.StatusCode(503); }
        });
        await app.RunAsync();
    }
}
