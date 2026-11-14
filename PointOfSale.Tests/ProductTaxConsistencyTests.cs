using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PointOfSale.API.DTOs.Cart;
using PointOfSale.API.DTOs.Checkout;
using PointOfSale.API.Services.Checkout;
using PointOfSale.API.Services.Pricing;
using PointOfSale.API.Exceptions;
using PointOfSale.Core.Entities;
using PointOfSale.Core.Pricing;
using PointOfSale.Infrastructure.Data;
using PointOfSale.Infrastructure.MongoDB.Documents;

namespace PointOfSale.Tests;

public sealed class ProductTaxConsistencyTests
{
    [Theory]
    [InlineData(10, true, false, 300, 1, 30)]
    [InlineData(10, true, false, 300, 2, 60)]
    [InlineData(0, true, false, 300, 2, 0)]
    [InlineData(15, true, false, 300, 2, 90)]
    [InlineData(0.5, true, false, 300, 2, 3)]
    [InlineData(10, false, false, 300, 2, 0)]
    [InlineData(10, true, true, 330, 2, 0)]
    [InlineData(10, true, false, 0.05, 3, 0.02)]
    public async Task ProductRateOverridesLegacyTenantRate_AndCartCheckoutSharePosPolicy(
        decimal rate, bool enabled, bool inclusive, decimal price, int quantity, decimal expectedTax)
    {
        await using var db = Db();
        var tenant = Guid.NewGuid();
        var product = Product(db, tenant, rate, inclusive);
        db.SystemSettings.AddRange(new SystemSetting { TenantId = tenant, Key = "Tax.Enable", Value = enabled.ToString() },
            new SystemSetting { TenantId = tenant, Key = "Tax.Rate", Value = "27" });
        await db.SaveChangesAsync();
        var service = Pricing(db);
        var cart = new CartDto { Items = [new() { ProductId = product.Id, UnitPrice = price, Quantity = quantity }] };
        var snapshot = Snapshot(product.Id, price, quantity);
        var shipping = new ShippingMethodDocument { BaseRate = 10m };
        var preview = await service.CalculateCartPreviewAsync(tenant, cart, shipping, null, "EG");
        var checkout = await service.CalculateCheckoutAsync(tenant, snapshot, shipping, null, "EG");
        Assert.Equal(expectedTax, ProductTaxPolicy.CalculateAddedTax(price * quantity, 0, rate, inclusive, enabled));
        Assert.Equal(expectedTax, preview.Tax);
        Assert.Equal(expectedTax, checkout.Tax);
        Assert.Equal(preview.Total, checkout.Total);
        Assert.Equal(price * quantity + expectedTax + 10m, checkout.Total);
        var line = Assert.Single(checkout.TaxLines);
        Assert.Equal(price * quantity, line.TaxableBase);
        Assert.Equal(enabled ? rate : 0m, line.TaxRate);
        Assert.Equal(inclusive, line.PriceIncludesTax);
    }

    [Fact]
    public async Task OrderDiscountsStayAfterTax_AndShippingAndFeesAreNotTaxed()
    {
        await using var db = Db(); var tenant = Guid.NewGuid();
        var product = Product(db, tenant, 10m);
        await db.SaveChangesAsync();
        var snapshot = Snapshot(product.Id, 300m, 2);
        snapshot.CouponDiscountTotal = 20m;
        snapshot.PromotionDiscountTotal = 30m;
        var result = await Pricing(db).CalculateCheckoutAsync(tenant, snapshot,
            new ShippingMethodDocument { BaseRate = 10m }, new PaymentMethodDocument { Fee = 5m, FeeType = "fixed" }, "EG");
        Assert.Equal(600m, result.Subtotal); Assert.Equal(50m, result.TotalDiscount);
        Assert.Equal(600m, Assert.Single(result.TaxLines).TaxableBase);
        Assert.Equal(60m, result.Tax); Assert.Equal(625m, result.Total);
        // Unlike an order discount, a POS line discount changes the supplied taxable base.
        Assert.Equal(55m, ProductTaxPolicy.CalculateAddedTax(600m, 50m, 10m, false));
        var taxOnSameBase = await Tax(db).CalculateLinesAsync(tenant, [new(product.Id, null, 550m)], "EG");
        Assert.Equal(55m, taxOnSameBase.TaxAmount);
    }

    [Fact]
    public async Task MixedRatesPersistExactLineTaxes_NotProportionalOrderTax_AndSnapshotsRemainStable()
    {
        await using var db = Db(); var tenant = Guid.NewGuid();
        var zero = Product(db, tenant, 0m); var ten = Product(db, tenant, 10m);
        await db.SaveChangesAsync();
        var snapshot = Snapshot(zero.Id, 300m, 1);
        snapshot.Items.Add(new() { ProductId = ten.Id, CurrentPrice = 300m, Quantity = 2 });
        var pricing = await Pricing(db).CalculateCheckoutAsync(tenant, snapshot, null, null, "EG");
        var order = new Order { Id = Guid.NewGuid(), TenantId = tenant, OrderNumber = "P519-unit", Currency = "EGP",
            Subtotal = pricing.Subtotal, TaxAmount = pricing.Tax, Total = pricing.Total,
            PricingSnapshotJson = JsonSerializer.Serialize(pricing) };
        foreach (var item in snapshot.Items)
            order.Items.Add(new OrderItem { Id = Guid.NewGuid(), OrderId = order.Id, ProductId = item.ProductId, Quantity = item.Quantity });
        var type = typeof(CheckoutService);
        var admissionType = type.GetNestedType("AffiliateOrderLineAdmissionSnapshot", BindingFlags.NonPublic)!;
        var admissions = Activator.CreateInstance(typeof(Dictionary<,>).MakeGenericType(typeof(Guid), admissionType))!;
        type.GetMethod("RefreshOrderItemAuthoritySnapshots", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(RuntimeHelpers.GetUninitializedObject(type), [order, snapshot.Items, 0m, pricing.TaxLines, admissions, false]);
        db.Orders.Add(order); await db.SaveChangesAsync();
        var originalJson = order.PricingSnapshotJson;
        ten.TaxRate = 17m; zero.TaxRate = 25m;
        db.SystemSettings.Add(new() { TenantId = tenant, Key = "Tax.Enable", Value = "false" });
        await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        var saved = await db.Orders.IgnoreQueryFilters().Include(o => o.Items).SingleAsync(o => o.Id == order.Id);
        Assert.Equal(60m, saved.TaxAmount); Assert.Equal(960m, saved.Total);
        Assert.Equal(0m, saved.Items.Single(i => i.ProductId == zero.Id).TaxAmount);
        Assert.Equal(60m, saved.Items.Single(i => i.ProductId == ten.Id).TaxAmount);
        Assert.Equal(300m, saved.Items.Single(i => i.ProductId == zero.Id).Total);
        Assert.Equal(660m, saved.Items.Single(i => i.ProductId == ten.Id).Total);
        Assert.Equal(saved.Total, saved.Items.Sum(i => i.Total));
        Assert.Equal(originalJson, saved.PricingSnapshotJson);
        var newQuote = await Pricing(db).CalculateCheckoutAsync(tenant, snapshot, null, null, "EG");
        Assert.Equal(0m, newQuote.Tax);
    }

    [Fact]
    public async Task MissingOrForeignProductFailsClosed_NotLegacyFallbackOrClientAuthority()
    {
        await using var db = Db(); var tenant = Guid.NewGuid();
        var foreign = Product(db, Guid.NewGuid(), 10m); await db.SaveChangesAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => Tax(db).CalculateLinesAsync(tenant, [new(foreign.Id, null, 300m)], null));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Tax(db).CalculateLinesAsync(tenant, [new(Guid.NewGuid(), null, 300m)], null));
    }

    [Fact]
    public async Task CheckoutRejectsManipulatedClientTaxAndTotals_AfterSqlTaxRecalculation()
    {
        await using var db = Db(); var tenant = Guid.NewGuid();
        var product = Product(db, tenant, 10m); await db.SaveChangesAsync();
        var pricing = await Pricing(db).CalculateCheckoutAsync(tenant, Snapshot(product.Id, 300m, 2), null, null, "EG");
        // Unknown client taxRate is not part of the checkout financial authority DTO.
        var manipulated = JsonSerializer.Deserialize<CheckoutExpectedPricingDto>(
            "{\"Subtotal\":600,\"Tax\":0,\"Total\":600,\"TaxRate\":0}")!;
        var type = typeof(CheckoutService);
        var service = RuntimeHelpers.GetUninitializedObject(type);
        type.GetField("_logger", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(service, NullLogger<CheckoutService>.Instance);
        var error = Assert.Throws<TargetInvocationException>(() => type.GetMethod("EnsureExpectedPricingMatches", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(service, [tenant, manipulated, pricing, "p519-manipulation"]));
        Assert.IsType<CheckoutTotalMismatchException>(error.InnerException);
        Assert.Empty(db.Orders);
    }

    [Theory]
    [InlineData("{\"taxRate\":0.10,\"taxSource\":\"product\"}", 0.10, "product")]
    [InlineData("{\"taxRate\":0,\"taxSource\":\"tenant-disabled\"}", 0, "tenant-disabled")]
    [InlineData(null, 0, null)]
    [InlineData("not-json", 0, "legacy-unspecified")]
    public void CheckoutResponseAndReplayUsePersistedTaxMetadata_NotCurrentConfiguration(string? json, decimal rate, string? source)
    {
        var order = new Order { Subtotal = 600m, ShippingCost = 10m, TaxAmount = 60m, Total = 670m,
            PricingSnapshotJson = json };
        var totals = (OrderTotalsDto)typeof(CheckoutService).GetMethod("BuildOrderTotals", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [order])!;
        Assert.Equal(rate, totals.TaxRate); Assert.Equal(source, totals.TaxSource);
        Assert.Equal(60m, totals.TaxAmount); Assert.Equal(670m, totals.GrandTotal);
    }

    private static CartSnapshot Snapshot(Guid product, decimal price, int quantity) => new()
    { CartUpdatedAtUtc = DateTime.UtcNow, Items = [new() { ProductId = product, CurrentPrice = price, Quantity = quantity }] };
    private static Product Product(ApplicationDbContext db, Guid tenant, decimal rate, bool inclusive = false)
    {
        var product = new Product { Id = Guid.NewGuid(), TenantId = tenant, Name = "P519 test", TaxRate = rate, PriceIncludesTax = inclusive };
        db.Products.Add(product); return product;
    }
    private static ApplicationDbContext Db() => new(new DbContextOptionsBuilder<ApplicationDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
    private static TaxCalculationService Tax(ApplicationDbContext db) => new(db, Options.Create(new StorefrontPricingOptions()), NullLogger<TaxCalculationService>.Instance);
    private static StorefrontPricingService Pricing(ApplicationDbContext db) => new(new ShippingCalculationService(), Tax(db));
}
