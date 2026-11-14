using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MongoDB.Driver;
using PointOfSale.Infrastructure.MongoDB.Documents.Marketplace;

namespace PointOfSale.Infrastructure.MongoDB;

public sealed class MarketplaceReadModelIndexInitializer : IHostedService
{
    private readonly IMongoDatabase _database;
    private readonly MongoIndexManagementPolicy _indexPolicy;
    private readonly ILogger<MarketplaceReadModelIndexInitializer> _logger;

    public MarketplaceReadModelIndexInitializer(
        IMongoDatabase database,
        ILogger<MarketplaceReadModelIndexInitializer> logger,
        MongoIndexManagementPolicy? indexPolicy = null)
    {
        _database = database;
        _indexPolicy = indexPolicy ?? new MongoIndexManagementPolicy();
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken) => RunInitializationAsync(cancellationToken);

    private async Task RunInitializationAsync(CancellationToken cancellationToken)
    {
        try
        {
            await EnsureProductIndexesAsync(cancellationToken);
            await EnsureOrderIndexesAsync(cancellationToken);
            await EnsureHomepageIndexesAsync(cancellationToken);
            _logger.LogInformation("Marketplace Mongo read-model indexes ensured successfully");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize marketplace Mongo read-model indexes");
            throw;
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private async Task EnsureProductIndexesAsync(CancellationToken cancellationToken)
    {
        var collection = _database.GetCollection<MarketplaceProductReadModelDocument>(MarketplaceProductReadModelDocument.CollectionName);
        var indexBuilder = Builders<MarketplaceProductReadModelDocument>.IndexKeys;

        await _indexPolicy.EnsureAsync(collection,
            [
                new CreateIndexModel<MarketplaceProductReadModelDocument>(
                    indexBuilder.Ascending(x => x.TenantId).Ascending(x => x.ProductId),
                    new CreateIndexOptions { Unique = true, Name = "idx_marketplace_product_tenant_product" }),
                new CreateIndexModel<MarketplaceProductReadModelDocument>(
                    indexBuilder.Ascending(x => x.ProductId),
                    new CreateIndexOptions { Name = "idx_marketplace_product_product" }),
                new CreateIndexModel<MarketplaceProductReadModelDocument>(
                    indexBuilder.Ascending(x => x.TenantId).Ascending(x => x.Category),
                    new CreateIndexOptions { Name = "idx_marketplace_product_category" }),
                new CreateIndexModel<MarketplaceProductReadModelDocument>(
                    indexBuilder.Ascending(x => x.TenantId).Ascending(x => x.VendorId),
                    new CreateIndexOptions { Name = "idx_marketplace_product_vendor" }),
                new CreateIndexModel<MarketplaceProductReadModelDocument>(
                    indexBuilder.Ascending(x => x.TenantId).Descending(x => x.CreatedAtUtc),
                    new CreateIndexOptions { Name = "idx_marketplace_product_created" }),
                new CreateIndexModel<MarketplaceProductReadModelDocument>(
                    indexBuilder.Ascending(x => x.TenantId).Descending(x => x.UpdatedAtUtc),
                    new CreateIndexOptions { Name = "idx_marketplace_product_updated" })
            ],
            cancellationToken);
    }

    private async Task EnsureOrderIndexesAsync(CancellationToken cancellationToken)
    {
        var collection = _database.GetCollection<MarketplaceOrderReadModelDocument>(MarketplaceOrderReadModelDocument.CollectionName);
        var indexBuilder = Builders<MarketplaceOrderReadModelDocument>.IndexKeys;

        await _indexPolicy.EnsureAsync(collection,
            [
                new CreateIndexModel<MarketplaceOrderReadModelDocument>(
                    indexBuilder.Ascending(x => x.TenantId).Ascending(x => x.OrderId),
                    new CreateIndexOptions { Unique = true, Name = "idx_marketplace_order_tenant_order" }),
                new CreateIndexModel<MarketplaceOrderReadModelDocument>(
                    indexBuilder.Ascending(x => x.TenantId).Descending(x => x.CreatedAtUtc),
                    new CreateIndexOptions { Name = "idx_marketplace_order_created" }),
                new CreateIndexModel<MarketplaceOrderReadModelDocument>(
                    indexBuilder.Ascending(x => x.TenantId).Ascending(x => x.Status),
                    new CreateIndexOptions { Name = "idx_marketplace_order_status" })
            ],
            cancellationToken);
    }

    private async Task EnsureHomepageIndexesAsync(CancellationToken cancellationToken)
    {
        var collection = _database.GetCollection<MarketplaceHomepageReadModelDocument>(MarketplaceHomepageReadModelDocument.CollectionName);
        var indexBuilder = Builders<MarketplaceHomepageReadModelDocument>.IndexKeys;

        await _indexPolicy.EnsureAsync(collection,
            [
                new CreateIndexModel<MarketplaceHomepageReadModelDocument>(
                    indexBuilder.Ascending(x => x.ScopeKey),
                    new CreateIndexOptions { Unique = true, Name = "idx_marketplace_homepage_scope" }),
                new CreateIndexModel<MarketplaceHomepageReadModelDocument>(
                    indexBuilder.Ascending(x => x.TenantId),
                    new CreateIndexOptions { Name = "idx_marketplace_homepage_tenant" })
            ],
            cancellationToken);
    }
}
