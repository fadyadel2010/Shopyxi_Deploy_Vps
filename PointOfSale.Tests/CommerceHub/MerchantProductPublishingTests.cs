using Microsoft.EntityFrameworkCore;
using PointOfSale.API.CommerceHub.Handlers;
using PointOfSale.API.CommerceHub.Services;
using PointOfSale.Core.Entities;
using PointOfSale.Core.Pricing;
using PointOfSale.Infrastructure.Data;

namespace PointOfSale.Tests.CommerceHub;

public sealed class MerchantProductPublishingTests
{
    [Fact]
    public async Task CategorySelection_AcceptsOnlyActiveChildOfSelectedParent()
    {
        await using var db = CreateContext();
        var electronics = Category("Electronics");
        var home = Category("Home");
        var televisions = Category("Televisions", electronics.Id, 1);
        var furniture = Category("Furniture", home.Id, 1);
        db.CommerceHubCategories.AddRange(electronics, home, televisions, furniture);
        await db.SaveChangesAsync();

        await CommerceHubCategorySelectionAuthority.ValidateAsync(
            db, electronics.Id, televisions.Id, CancellationToken.None);

        var exception = await Assert.ThrowsAsync<CommerceHubValidationException>(() =>
            CommerceHubCategorySelectionAuthority.ValidateAsync(
                db, electronics.Id, furniture.Id, CancellationToken.None));
        Assert.Contains("belongs", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CategorySelection_RequiresChildWhenParentHasChildren_ButAcceptsLeafRoot()
    {
        await using var db = CreateContext();
        var electronics = Category("Electronics");
        var televisions = Category("Televisions", electronics.Id, 1);
        var services = Category("Services");
        db.CommerceHubCategories.AddRange(electronics, televisions, services);
        await db.SaveChangesAsync();

        await Assert.ThrowsAsync<CommerceHubValidationException>(() =>
            CommerceHubCategorySelectionAuthority.ValidateAsync(
                db, electronics.Id, electronics.Id, CancellationToken.None));
        await CommerceHubCategorySelectionAuthority.ValidateAsync(
            db, services.Id, services.Id, CancellationToken.None);
    }

    [Fact]
    public async Task CategorySelection_RejectsInactiveParentOrChild()
    {
        await using var db = CreateContext();
        var parent = Category("Electronics");
        var child = Category("Televisions", parent.Id, 1);
        child.IsActive = false;
        db.CommerceHubCategories.AddRange(parent, child);
        await db.SaveChangesAsync();

        await Assert.ThrowsAsync<CommerceHubValidationException>(() =>
            CommerceHubCategorySelectionAuthority.ValidateAsync(
                db, parent.Id, child.Id, CancellationToken.None));
    }

    [Fact]
    public void PriceModes_RejectSilentFallbackAndUnsupportedPrecision()
    {
        var tenantId = Guid.NewGuid();
        var variantId = Guid.NewGuid();
        var categoryId = Guid.NewGuid();

        Assert.Throws<CommerceHubValidationException>(() =>
            PublishToCommerceHubHandler.ValidatePublishOrUpdate(
                tenantId, variantId, categoryId, null, "custom"));
        Assert.Throws<CommerceHubValidationException>(() =>
            PublishToCommerceHubHandler.ValidatePublishOrUpdate(
                tenantId, variantId, categoryId, 120m, "source"));
        Assert.Throws<CommerceHubValidationException>(() =>
            PublishToCommerceHubHandler.ValidatePublishOrUpdate(
                tenantId, variantId, categoryId, 120.001m, "custom"));
        Assert.Throws<CommerceHubValidationException>(() =>
            PublishToCommerceHubHandler.ValidatePublishOrUpdate(
                tenantId, variantId, categoryId, 0m, "custom"));
        Assert.Throws<CommerceHubValidationException>(() =>
            PublishToCommerceHubHandler.ValidatePublishOrUpdate(
                tenantId, variantId, categoryId, -1m, "custom"));

        PublishToCommerceHubHandler.ValidatePublishOrUpdate(
            tenantId, variantId, categoryId, null, "source");
        PublishToCommerceHubHandler.ValidatePublishOrUpdate(
            tenantId, variantId, categoryId, 120.00m, "custom");
    }

    [Fact]
    public async Task ListingOverride_IsIndependentFromSourcePriceAndLegacyRolloutSwitch()
    {
        CommerceHubPriceOverrideRuntimePolicy.Configure(new CommerceHubPriceOverrideRolloutOptions());
        var authority = new CommerceHubPriceAuthority();

        var custom = await authority.ResolveAsync(Guid.NewGuid(), 100m, 120m);
        var source = await authority.ResolveAsync(Guid.NewGuid(), 100m, null);

        Assert.Equal(120m, custom.FinalPrice);
        Assert.Equal(100m, custom.BaseVariantPrice);
        Assert.Equal(CommerceHubPriceSource.CommerceHubOverride, custom.PriceSource);
        Assert.Equal(100m, source.FinalPrice);
        Assert.Equal(CommerceHubPriceSource.ProductVariantEffectiveOnlinePrice, source.PriceSource);
    }

    private static ApplicationDbContext CreateContext() => new(
        new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static CommerceHubCategory Category(string name, Guid? parentId = null, int level = 0) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        Slug = name.ToLowerInvariant(),
        ParentId = parentId,
        Level = level,
        IsActive = true
    };
}
