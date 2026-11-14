using System.Security.Authentication;
using Microsoft.Extensions.Configuration;
using PointOfSale.Infrastructure.Configuration;

namespace PointOfSale.Infrastructure.WhatsApp.Transport;

/// <summary>Private service credentials must never follow redirects or ambient proxies.</summary>
public static class BaileysHttpClientPolicy
{
    public static HttpClientHandler CreateHandler(IConfiguration configuration)
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            UseProxy = false,
            CheckCertificateRevocationList = true,
            SslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13
        };
        try
        {
            var caPath = configuration["BaileysService:Tls:CaCertificatePath"];
            if (!string.IsNullOrWhiteSpace(caPath))
            {
                var policy = new ServerCertificatePolicy(caPath);
                handler.ServerCertificateCustomValidationCallback = (request, certificate, chain, errors) =>
                    policy.Validate(request, certificate, chain, errors);
            }
            return handler;
        }
        catch
        {
            handler.Dispose();
            throw new InvalidOperationException("Baileys TLS trust configuration is invalid; values are not reported.");
        }
    }
}
