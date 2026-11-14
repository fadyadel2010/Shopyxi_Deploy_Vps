using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using PointOfSale.API.Controllers;
using PointOfSale.API.Services;
using PointOfSale.Infrastructure.MongoDB.Configuration;
using PointOfSale.Infrastructure.MongoDB.Services;
using System.Text.Json;
using PointOfSale.Infrastructure.MongoDB.Documents.Affiliate;

namespace PointOfSale.Tests.Affiliate;

public sealed class AffiliateP54bCategoryTests
{
    [Fact]
    public void PublicFilter_UsesQaOwnership_NotNames_AndEnforcesPriceStockAndCategory()
    {
        var ordinaryStore = Guid.NewGuid();
        AffiliateProductIntelligenceDocument Doc(Guid store, string slug = "electronics", decimal price = 10, bool outOfStock = false) =>
            new() { TenantId = store, ProductId = Guid.NewGuid(), ProductName = "P53B cancellation fixture",
                AffiliateCategorySlug = slug, BasePrice = price, IsOutOfStock = outOfStock };
        var valid = Doc(ordinaryStore);
        var qa = Doc(Guid.Parse("51510000-2026-4022-a000-000000000001"));
        var zero = Doc(ordinaryStore, price: 0);
        var unavailable = Doc(ordinaryStore, outOfStock: true);
        var foreignCategory = Doc(ordinaryStore, "fashion");
        var result = AffiliatePortalController.FilterPublicAffiliateDocuments([valid, qa, zero, unavailable, foreignCategory], null, "electronics");
        Assert.Same(valid, Assert.Single(result));
        Assert.Empty(AffiliatePortalController.FilterPublicAffiliateDocuments([valid], null, "unknown"));
    }

    [Fact]
    public async Task PrimaryReadOnly_DiscoveryOmitsEmptyCategories_CountsMatchPublicList_AndScopesByStore()
    {
        await using var db = await AffiliateP53ePrimarySupport.Open("P54B-category-readonly");
        await using var tx = await db.Database.BeginTransactionAsync();
        await db.Database.ExecuteSqlRawAsync("SET TRANSACTION READ ONLY");
        var config = AffiliateP53ePrimarySupport.Config;
        var mongo = new MongoDbContext(Options.Create(config.GetSection("MongoDB").Get<MongoDbSettings>()!), NullLogger<MongoDbContext>.Instance);
        var resolver = new StorefrontUrlResolver(config, NullLogger<StorefrontUrlResolver>.Instance, Mock.Of<IServiceScopeFactory>());
        var controller = new AffiliatePortalController(db, mongo, null!, null!, null!, resolver, NullLogger<AffiliatePortalController>.Instance);
        var categories = Assert.IsType<List<AffiliateCategoryDiscoveryItem>>(
            Assert.IsType<OkObjectResult>((await controller.DiscoverCategories(default)).Result).Value);
        Assert.NotEmpty(categories);
        Assert.DoesNotContain(categories, c => c.Slug == "p51-fb1473f1c92f4321817074cb2414e259");
        Assert.All(categories, c => Assert.True(c.ProductCount > 0));
        Assert.Contains(categories, c => c.Id == Guid.Parse("11111111-1111-1111-1111-111111111111") && c.Slug == "electronics");
        foreach (var category in categories)
        {
            var list = Assert.IsType<AffiliatePublicProductDiscoveryResponse>(Assert.IsType<OkObjectResult>(
                (await controller.DiscoverPublicProducts(categorySlug: category.Slug)).Result).Value);
            Assert.Equal(category.ProductCount, list.TotalCount);
            Assert.All(list.Items, p => Assert.Equal(category.Slug, p.CategorySlug));
        }
        var scoped = Assert.IsType<List<AffiliateCategoryDiscoveryItem>>(Assert.IsType<OkObjectResult>(
            (await controller.DiscoverCategories(default, AffiliateP53ePrimarySupport.Store)).Result).Value);
        Assert.All(scoped, c => Assert.InRange(c.ProductCount, 1, categories.Single(x => x.Id == c.Id).ProductCount));
        Assert.Empty(Assert.IsType<List<AffiliateCategoryDiscoveryItem>>(Assert.IsType<OkObjectResult>(
            (await controller.DiscoverCategories(default, Guid.NewGuid())).Result).Value));
        Assert.Empty(Assert.IsType<List<AffiliateCategoryDiscoveryItem>>(Assert.IsType<OkObjectResult>(
            (await controller.DiscoverCategories(default, Guid.Parse("51510000-2026-4022-a000-000000000001"))).Result).Value));
        var fixture = await db.AffiliateCategories.AsNoTracking().Where(c => c.Name == "P53B cancellation fixture").FirstAsync();
        Assert.True(fixture.IsActive); Assert.False(fixture.IsDeleted);
        Assert.DoesNotContain(categories, c => c.Id == fixture.Id);
        foreach (var slug in new[] { fixture.Slug, "missing-p54b-category" })
        {
            var empty = Assert.IsType<AffiliatePublicProductDiscoveryResponse>(Assert.IsType<OkObjectResult>(
                (await controller.DiscoverPublicProducts(categorySlug: slug)).Result).Value);
            Assert.Equal(0, empty.TotalCount); Assert.Empty(empty.Items);
        }
        Assert.Empty(db.ChangeTracker.Entries());
        // Assertions above are the regression contract. Never regenerate or
        // overwrite retained historical evidence as a test side effect.
    }
}
