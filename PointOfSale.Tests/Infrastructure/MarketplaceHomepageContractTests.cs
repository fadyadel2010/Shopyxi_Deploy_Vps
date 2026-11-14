using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;
using PointOfSale.API.Attributes;
using PointOfSale.API.Controllers;
using PointOfSale.Infrastructure.MongoDB.Documents.Marketplace;
using System.Reflection;

namespace PointOfSale.Tests.Infrastructure;

public sealed class MarketplaceHomepageContractTests
{
    [Fact]
    public void TenantHomepageFilter_RendersStringGuidWithoutGlobalSerializerOverrides()
    {
        var tenantId = Guid.NewGuid();
        var filter = Builders<MarketplaceHomepageReadModelDocument>.Filter.Eq(x => x.TenantId, tenantId);
        var rendered = filter.Render(new RenderArgs<MarketplaceHomepageReadModelDocument>(
            BsonSerializer.LookupSerializer<MarketplaceHomepageReadModelDocument>(), BsonSerializer.SerializerRegistry));
        Assert.Equal(tenantId.ToString(), rendered["TenantId"].AsString);
    }

    [Fact]
    public void HomepageDocument_RoundTripsTenantAndGlobalScopes()
    {
        foreach (var tenantId in new Guid?[] { Guid.NewGuid(), null })
        {
            var document = new MarketplaceHomepageReadModelDocument {
                Id = tenantId?.ToString("N") ?? "global", TenantId = tenantId,
                Sections = [], LastUpdated = DateTime.UtcNow, LastEventOccurredAtUtc = DateTime.UtcNow
            };
            var bson = document.ToBsonDocument();
            Assert.Equal(tenantId, BsonSerializer.Deserialize<MarketplaceHomepageReadModelDocument>(bson).TenantId);
            if (tenantId.HasValue) Assert.Equal(BsonType.String, bson["TenantId"].BsonType);
            else Assert.True(bson["TenantId"].IsBsonNull);
        }
    }

    [Fact]
    public void AnonymousHomepage_DeclaresTenantBypass_WithoutWeakeningCustomerOrders()
    {
        var homepage = typeof(MarketplaceReadController).GetMethod(nameof(MarketplaceReadController.GetHomepage))!;
        var orders = typeof(MarketplaceReadController).GetMethod(nameof(MarketplaceReadController.GetOrders))!;
        Assert.NotNull(homepage.GetCustomAttribute<AllowAnonymousTenantAttribute>());
        Assert.Null(orders.GetCustomAttribute<AllowAnonymousTenantAttribute>());
        var context = new DefaultHttpContext();
        context.Request.Path = "/api/read/homepage";
        context.SetEndpoint(new Endpoint(_ => Task.CompletedTask,
            new EndpointMetadataCollection(homepage.GetCustomAttributes()), "homepage"));
        // Middleware SQL dependencies are exercised by existing tenant authority tests;
        // this assertion verifies the endpoint metadata consumed by that boundary.
        Assert.NotNull(context.GetEndpoint()!.Metadata.GetMetadata<AllowAnonymousTenantAttribute>());
    }
}
