using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace PointOfSale.API.Configuration;

/// <summary>The only delegated HTTP peers are the local normalization sidecar.</summary>
public static class LoopbackForwardedHeadersPolicy
{
    public static void Register(IServiceCollection services, IConfiguration configuration)
    {
        // Validate before binding: configuration array providers merge by index.
        // A later loopback value must not conceal an off-pod value in another provider.
        Validate(configuration);
        services.PostConfigure<ForwardedHeadersOptions>(options =>
        {
            Validate(configuration);
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor |
                ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;
            options.KnownIPNetworks.Clear();
            options.KnownProxies.Clear();
            options.KnownProxies.Add(IPAddress.Loopback);
            options.KnownProxies.Add(IPAddress.IPv6Loopback);
            options.ForwardLimit = 1;
        });
    }

    public static void Validate(IConfiguration configuration)
    {
        static bool Safe(string? value) => value is "127.0.0.1" or "::1";
        static void Check(IConfiguration config)
        {
            if (config.GetSection("ForwardedHeaders:KnownProxies").GetChildren().Any(p => !Safe(p.Value)) ||
                !string.IsNullOrWhiteSpace(config["ForwardedHeaders:KnownProxies"]) ||
                config.GetSection("ForwardedHeaders:KnownNetworks").GetChildren().Any() ||
                config.GetSection("ForwardedHeaders:KnownIPNetworks").GetChildren().Any() ||
                !string.IsNullOrWhiteSpace(config["ForwardedHeaders:KnownNetworks"]) ||
                !string.IsNullOrWhiteSpace(config["ForwardedHeaders:KnownIPNetworks"]) ||
                string.Equals(config["ASPNETCORE_FORWARDEDHEADERS_ENABLED"], "true", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Forwarded-header delegation must be explicit loopback only; automatic trust is forbidden.");
        }
        Check(configuration);
        if (configuration is IConfigurationRoot root)
            foreach (var provider in root.Providers)
            {
                // Read each provider without creating another reload subscription or
                // disposing a provider owned by the application's configuration root.
                foreach (var key in provider.GetChildKeys(Array.Empty<string>(), "ForwardedHeaders:KnownProxies").Distinct())
                    if (provider.TryGet("ForwardedHeaders:KnownProxies:" + key, out var value) && !Safe(value))
                        throw new InvalidOperationException("An underlying provider delegates off-pod proxy authority.");
                if (provider.TryGet("ForwardedHeaders:KnownProxies", out var proxyScalar) && !string.IsNullOrWhiteSpace(proxyScalar))
                    throw new InvalidOperationException("Proxy authority requires explicit indexed loopback addresses.");
                foreach (var section in new[] { "ForwardedHeaders:KnownNetworks", "ForwardedHeaders:KnownIPNetworks" })
                    if (provider.GetChildKeys(Array.Empty<string>(), section).Any() ||
                        (provider.TryGet(section, out var scalar) && !string.IsNullOrWhiteSpace(scalar)))
                        throw new InvalidOperationException("An underlying provider delegates proxy network authority.");
                if (provider.TryGet("ASPNETCORE_FORWARDEDHEADERS_ENABLED", out var automatic) &&
                    string.Equals(automatic, "true", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("An underlying provider enables automatic forwarded-header trust.");
            }
        if (string.Equals(Environment.GetEnvironmentVariable("ASPNETCORE_FORWARDEDHEADERS_ENABLED"),
                "true", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Automatic forwarded-header trust is forbidden.");
    }
}
