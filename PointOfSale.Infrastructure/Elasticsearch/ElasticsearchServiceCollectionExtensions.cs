using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenSearch.Client;
using OpenSearch.Net;
using PointOfSale.Infrastructure.Configuration;
using PointOfSale.Infrastructure.Elasticsearch.Configuration;
using PointOfSale.Infrastructure.Elasticsearch.Documents;
using PointOfSale.Infrastructure.Elasticsearch.Services;
using PointOfSale.Infrastructure.ReadModels.Evolution;
using PointOfSale.Infrastructure.Marketplace.Generation;

namespace PointOfSale.Infrastructure.Elasticsearch;

/// <summary>
/// Extension methods for registering Elasticsearch services in DI container.
/// </summary>
public static class ElasticsearchServiceCollectionExtensions
{
    /// <summary>
    /// Adds Elasticsearch services to the dependency injection container.
    /// 
    /// ARCHITECTURE: Shopify-Grade Multilingual Search
    /// ================================================
    /// 
    /// Registers:
    /// - ElasticsearchSettings (bound from configuration)
    /// - IOpenSearchClient (singleton - connection pooled internally)
    /// - IProductSearchIndexService (singleton)
    /// - IUnifiedSearchIndexService (singleton)
    /// - ElasticsearchIndexInitializer (hosted service)
    /// 
    /// Key Principle: Search intelligence lives in Elasticsearch analyzers.
    /// NO translation dictionaries. NO external services for search.
    /// 
    /// No external Elasticsearch plugins required (uses built-in analyzers).
    /// </summary>
    public static IServiceCollection AddElasticsearch(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // Bind Elasticsearch settings from configuration
        services.Configure<ElasticsearchSettings>(
            configuration.GetSection(ElasticsearchSettings.SectionName));

        // Get settings for client configuration
        var settings = configuration
            .GetSection(ElasticsearchSettings.SectionName)
            .Get<ElasticsearchSettings>() ?? new ElasticsearchSettings();

        // Disabled search must neither require an endpoint nor contact an old
        // configured cluster. Keep DI consumers resolvable with a failing,
        // in-memory transport; it cannot manufacture successful search results.
        ConnectionSettings connectionSettings;
        if (!settings.Enabled)
        {
            connectionSettings = new ConnectionSettings(
                new SingleNodeConnectionPool(new Uri("http://127.0.0.1")),
                new InMemoryConnection(Array.Empty<byte>(), statusCode: 503));
        }
        else
        {
            // Other index owners must not target Marketplace via configuration. This
            // catches direct names and multi-index/wildcard selectors before any client
            // or hosted initializer can execute lifecycle operations on that topology.
            foreach (var index in new[] { settings.ProductsIndexName, settings.PublicAffiliateProductsIndexName,
                         settings.CommerceHubProductsIndexName })
            {
                if (!MarketplaceSearchIndexOwnership.IsOtherOwnerIndexName(index))
                    throw new InvalidOperationException("Configured search index ownership overlaps or can select Marketplace topology.");
            }
            if (!Uri.TryCreate(settings.Url, UriKind.Absolute, out var endpoint) ||
                (endpoint.Scheme != Uri.UriSchemeHttp && endpoint.Scheme != Uri.UriSchemeHttps))
                throw new InvalidOperationException("Enabled Elasticsearch requires an absolute HTTP or HTTPS endpoint.");
            if (endpoint.AbsolutePath != "/" || !string.IsNullOrEmpty(endpoint.UserInfo) || !string.IsNullOrEmpty(endpoint.Query) || !string.IsNullOrEmpty(endpoint.Fragment))
                throw new InvalidOperationException("Elasticsearch endpoint requires one origin without a path prefix, credentials, query or fragment.");
            var hasUsername = !string.IsNullOrWhiteSpace(settings.Username);
            var hasPassword = !string.IsNullOrEmpty(settings.Password);
            if (hasUsername != hasPassword)
                throw new InvalidOperationException("Elasticsearch username and password must be configured together.");
            if (endpoint.Scheme != Uri.UriSchemeHttps && (hasUsername || !string.IsNullOrWhiteSpace(settings.CaCertificatePath)))
                throw new InvalidOperationException("Elasticsearch authentication and custom CA require HTTPS.");
            var handler = new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false, UseProxy = false, UseDefaultCredentials = false };
            if (endpoint.Scheme == Uri.UriSchemeHttps)
            {
                var transportPolicy = new ServerCertificatePolicy(settings.CaCertificatePath);
                handler.ServerCertificateCustomValidationCallback = transportPolicy.Validate;
            }
            var guardedHttp = new HttpClient(new MarketplaceApplicationTransportGuard(handler)) { Timeout = Timeout.InfiniteTimeSpan };
            connectionSettings = new ConnectionSettings(new SingleNodeConnectionPool(endpoint), new HttpConnection(guardedHttp));
            if (hasUsername)
                connectionSettings = connectionSettings.BasicAuthentication(settings.Username, settings.Password);
            if (endpoint.Scheme == Uri.UriSchemeHttps)
            {
                var certificatePolicy = new ServerCertificatePolicy(settings.CaCertificatePath);
                connectionSettings = connectionSettings.ServerCertificateValidationCallback(certificatePolicy.Validate);
            }
        }

        connectionSettings = connectionSettings
            .DefaultIndex(settings.ProductsIndexName)
            .DefaultMappingFor<MarketplaceServiceDocument>(m => m
                .IndexName("marketplace_services")
                .IdProperty(p => p.ServiceId)
            )
            .RequestTimeout(TimeSpan.FromSeconds(settings.RequestTimeoutSeconds))
            .DisableDirectStreaming(settings.EnableDebugMode)
            .ThrowExceptions(false)
            .UseSystemTextJson(false);

        if (settings.EnableDebugMode)
        {
            connectionSettings = connectionSettings.PrettyJson();
        }

        var client = new OpenSearchClient(connectionSettings);
        services.AddSingleton<IOpenSearchClient>(client);

        // Register search telemetry (OpenTelemetry-compatible metrics)
        services.AddSingleton<SearchTelemetry>();
        services.AddSingleton<IReadModelEvolutionSnapshotProvider, ReadModelEvolutionSnapshotProvider>();
        services.AddSingleton<IReadModelEvolutionConvergenceService, ReadModelEvolutionConvergenceService>();

        // Register index services as singletons
        services.AddSingleton<IProductSearchIndexService, ProductSearchIndexService>();
        services.AddSingleton<IUnifiedSearchIndexService, UnifiedSearchIndexService>();
        services.AddScoped<IProductDiscoveryService, ProductDiscoveryService>();
        services.AddScoped<IAffiliateProductDiscoveryService, AffiliateProductDiscoveryService>();
        services.AddSingleton<ICommerceHubSearchIndexService, CommerceHubSearchIndexService>();
        services.AddSingleton<ICommerceHubSearchReadService, CommerceHubSearchReadService>();

        // Register index initializer as hosted service
        services.AddHostedService<ElasticsearchIndexInitializer>();
        services.AddHostedService<CommerceHubElasticsearchIndexInitializer>();

        return services;
    }
}
