using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using PointOfSale.API.Configuration;
using PointOfSale.API.Controllers;
using PointOfSale.API.Helpers;
using PointOfSale.API.Middleware;
using PointOfSale.API.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using PointOfSale.API.Services.Affiliate;
using PointOfSale.API.Services.Payments;
using PointOfSale.API.Services.Payments.Fawry;
using PointOfSale.API.Services.Payments.ManualPayment;
using PointOfSale.API.Services.Payments.Paymob;
using PointOfSale.API.Services.Payments.Stripe;
using PointOfSale.API.Services.Pricing;
using PointOfSale.API.Services.Storefront;
using PointOfSale.Core.Entities;
using PointOfSale.Core.Entities.Affiliate;
using PointOfSale.Core.Enums;
using PointOfSale.Infrastructure.Data;
using PointOfSale.Infrastructure.Instrumentation;
using PointOfSale.Infrastructure.MongoDB.Services;
using PointOfSale.Infrastructure.MultiTenancy;
using PointOfSale.Infrastructure.Redis.Services;

namespace PointOfSale.Tests.Affiliate;

/// <summary>Real SQL-model recomposition and checkout runtime authorities, no network/provider execution.</summary>
public sealed class AffiliatePublishingReadinessTests
{
    [Theory]
    [InlineData("ready", null)]
    [InlineData("store-disabled", "STOREFRONT_NOT_ENABLED")]
    [InlineData("store-maintenance", "STOREFRONT_NOT_ACTIVE")]
    [InlineData("store-missing", "STOREFRONT_NOT_ENABLED")]
    [InlineData("tenant-inactive", "STOREFRONT_NOT_ACTIVE")]
    [InlineData("payment-none", "PAYMENT_METHOD_REQUIRED")]
    [InlineData("payment-disabled", "PAYMENT_METHOD_REQUIRED")]
    [InlineData("payment-deleted", "PAYMENT_METHOD_REQUIRED")]
    [InlineData("payment-capability-disabled", "PAYMENT_METHOD_REQUIRED")]
    [InlineData("payment-unsupported", "PAYMENT_METHOD_REQUIRED")]
    [InlineData("payment-manual-incomplete", "PAYMENT_METHOD_REQUIRED")]
    [InlineData("payment-manual-runtime-disabled", "PAYMENT_METHOD_REQUIRED")]
    [InlineData("payment-manual-valid", null)]
    [InlineData("payment-multiple", null)]
    [InlineData("shipping-none", "SHIPPING_METHOD_REQUIRED")]
    [InlineData("shipping-inactive", "SHIPPING_METHOD_REQUIRED")]
    [InlineData("shipping-deleted", "SHIPPING_METHOD_REQUIRED")]
    [InlineData("shipping-no-warehouse", "SHIPPING_METHOD_REQUIRED")]
    [InlineData("shipping-inactive-warehouse", "SHIPPING_METHOD_REQUIRED")]
    [InlineData("shipping-foreign-warehouse", "SHIPPING_METHOD_REQUIRED")]
    [InlineData("shipping-unsupported-carrier", "SHIPPING_METHOD_REQUIRED")]
    [InlineData("shipping-multiple", null)]
    [InlineData("product-inactive", "PRODUCT_NOT_ACTIVE")]
    [InlineData("product-draft", "PRODUCT_NOT_PUBLISHED")]
    [InlineData("product-archived", "PRODUCT_ARCHIVED")]
    [InlineData("product-hidden", "PRODUCT_NOT_SELLABLE")]
    [InlineData("product-not-searchable", "PRODUCT_NOT_SELLABLE")]
    [InlineData("product-price-missing", "PRODUCT_NOT_SELLABLE")]
    [InlineData("product-unit-missing", "PRODUCT_CONFIGURATION_INVALID")]
    [InlineData("product-variant-inactive", "PRODUCT_CONFIGURATION_INVALID")]
    [InlineData("product-name-missing", "PRODUCT_CONFIGURATION_INVALID")]
    [InlineData("product-currency-invalid", "PRODUCT_CONFIGURATION_INVALID")]
    [InlineData("product-stock-empty", "PRODUCT_NOT_SELLABLE")]
    [InlineData("product-backorder", null)]
    [InlineData("product-service", null)]
    [InlineData("product-variants", null)]
    [InlineData("product-split-price-stock", "PRODUCT_NOT_SELLABLE")]
    public async Task CurrentOperationalRules_AreEnforcedIndependently(string fault, string? code)
    {
        await using var fixture = await Fixture.Create();
        await fixture.Apply(fault);
        var result = await fixture.Evaluate();
        if (code == null) Assert.True(result.IsPromotionEligible, string.Join(",", result.BlockerCodes));
        else { Assert.False(result.IsPromotionEligible); Assert.Contains(code, result.BlockerCodes); }
        Assert.True(fixture.Policy.IsEnabled);
        Assert.Equal(.1m, fixture.Policy.CommissionRate);
        Assert.Empty(fixture.Db.ChangeTracker.Entries().Where(e => e.State is EntityState.Modified or EntityState.Added or EntityState.Deleted));
    }

    [Theory]
    [InlineData("foreign")]
    [InlineData("deleted")]
    [InlineData("missing")]
    public async Task ForeignDeletedAndMissingProducts_AreIndistinguishable(string fault)
    {
        await using var fixture = await Fixture.Create();
        if (fault == "deleted") { fixture.Product.IsDeleted = true; await fixture.Db.SaveChangesAsync(); }
        var result = await fixture.Service.EvaluateAsync(fault == "foreign" ? Guid.NewGuid() : fixture.Tenant.Id,
            fault == "missing" ? Guid.NewGuid() : fixture.Product.Id);
        Assert.False(result.ProductFound);
        Assert.False(result.IsPromotionEligible);
        Assert.Empty(result.BlockerCodes);
    }

    [Fact]
    public async Task MultipleBlockers_AreDeterministic_AndPartialRestorationDoesNotAdmit()
    {
        await using var f = await Fixture.Create();
        await f.Apply("store-disabled"); await f.Apply("payment-disabled"); await f.Apply("shipping-inactive");
        var first = await f.Evaluate();
        Assert.Equal(new[] { "STOREFRONT_NOT_ENABLED", "STOREFRONT_NOT_ACTIVE", "PAYMENT_METHOD_REQUIRED", "SHIPPING_METHOD_REQUIRED" }, first.BlockerCodes);
        Assert.Equal(first.BlockerCodes, (await f.Evaluate()).BlockerCodes);
        f.Store.StorefrontStatus = "live"; await f.Db.SaveChangesAsync();
        var partial = await f.Evaluate();
        Assert.False(partial.IsPromotionEligible);
        Assert.Equal(new[] { "PAYMENT_METHOD_REQUIRED", "SHIPPING_METHOD_REQUIRED" }, partial.BlockerCodes);
        f.Payment.IsEnabled = true; f.Shipping.IsActive = true; await f.Db.SaveChangesAsync();
        Assert.True((await f.Evaluate()).IsPromotionEligible);
    }

    [Theory]
    [InlineData("store-disabled")]
    [InlineData("payment-disabled")]
    [InlineData("shipping-inactive")]
    [InlineData("product-archived")]
    public async Task ReadinessRegressionAndRestoration_PreserveParticipationAndProductHistory(string fault)
    {
        await using var f = await Fixture.Create();
        Assert.True((await f.Evaluate()).IsPromotionEligible);
        var policy = JsonSerializer.Serialize(f.Policy);
        await f.Apply(fault);
        var product = ProductState(f.Product);
        Assert.False((await f.Evaluate()).IsPromotionEligible);
        Assert.Equal(policy, JsonSerializer.Serialize(f.Policy));
        Assert.Equal(product, ProductState(f.Product));
        Assert.Empty(await f.Db.Orders.IgnoreQueryFilters().ToListAsync());
        Assert.Empty(await f.Db.AffiliateCommissionLedger.IgnoreQueryFilters().ToListAsync());
        f.Store.StorefrontStatus = "live"; f.Payment.IsEnabled = true; f.Shipping.IsActive = true;
        f.Product.PublishStatus = PublishStatus.Published; await f.Db.SaveChangesAsync();
        Assert.True((await f.Evaluate()).IsPromotionEligible);
        Assert.Equal(policy, JsonSerializer.Serialize(f.Policy));
    }

    [Fact]
    public async Task PolicyDisabled_DoesNotConfuseValidPrerequisitesWithParticipation()
    {
        await using var f = await Fixture.Create();
        f.Policy.Disable(); await f.Db.SaveChangesAsync();
        var result = await f.Evaluate();
        Assert.True(result.PrerequisitesReady);
        Assert.False(result.IsPromotionEligible);
        Assert.Equal(new[] { "AFFILIATE_POLICY_DISABLED" }, result.BlockerCodes);
    }

    [Fact]
    public async Task AdminSettingsAndList_ReturnCurrentReadiness_AndPersistedParticipation()
    {
        await using var f = await Fixture.Create();
        await f.Apply("payment-disabled");
        var settings = new AdminProductAffiliateController(f.Db, Mock.Of<PointOfSale.Core.Interfaces.Affiliate.IAffiliateService>(),
            Mock.Of<PointOfSale.Core.Interfaces.Affiliate.IAffiliateReadinessService>(), f.Context,
            NullLogger<AdminProductAffiliateController>.Instance, f.Service)
            { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() } };
        var response = Assert.IsType<ProductAffiliateSettingsResponse>(Assert.IsType<OkObjectResult>((await settings.GetSettings(f.Product.Id, default)).Result).Value);
        Assert.True(response.IsPolicyEnabled); Assert.False(response.IsEnabled);
        Assert.Contains("PAYMENT_METHOD_REQUIRED", response.Readiness!.BlockerCodes);
        var list = new AdminAffiliateProductsController(f.Db, f.Context, NullLogger<AdminAffiliateProductsController>.Instance, f.Service)
            { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() } };
        var item = Assert.Single(Assert.IsType<List<AffiliateProductListItem>>(Assert.IsType<OkObjectResult>((await list.List()).Result).Value));
        Assert.True(item.IsAffiliateEnabled); Assert.False(item.Readiness!.IsPromotionEligible);
    }

    private static string ProductState(Product p) => JsonSerializer.Serialize(new
    { p.Id, p.TenantId, p.Name, p.IsActive, p.IsOnlineVisible, p.IsSearchable, p.PublishStatus, p.TrackStock, p.AllowBackorder });

    [Theory]
    [InlineData("store-disabled")]
    [InlineData("payment-disabled")]
    [InlineData("shipping-inactive")]
    [InlineData("product-archived")]
    public async Task LegacyProtectedReferral_RechecksCurrentReadiness_WithoutChangingHistory(string fault)
    {
        await using var f = await Fixture.Create();
        var userId = Guid.NewGuid();
        var account = AffiliateAccount.Create(Guid.Empty, userId, "LEGACYREADY", "ISOLATED readiness");
        account.Id = Guid.NewGuid(); account.Activate();
        f.Db.Add(account); await f.Db.SaveChangesAsync();
        var tokens = new AffiliateAttributionEvidenceTokenService(f.Protection, TimeProvider.System,
            NullLogger<AffiliateAttributionEvidenceTokenService>.Instance);
        var cookie = tokens.Create(f.Tenant.Id, account.Id, account.ReferralCode, null);
        var path = "/api/storefront/landing/" + account.ReferralCode + "/" + f.Product.Slug;
        async Task<HttpContext> Request()
        {
            var context = new DefaultHttpContext(); context.Request.Path = path;
            context.Request.Method = "GET"; context.Response.Body = new MemoryStream();
            context.Request.Headers.Cookie = "AFF_ATTR=" + cookie;
            var continued = false;
            var middleware = new AffiliateReferralMiddleware(_ => { continued = true; return Task.CompletedTask; },
                NullLogger<AffiliateReferralMiddleware>.Instance, new ConfigurationBuilder().Build(),
                Mock.Of<IHostEnvironment>(host => host.EnvironmentName == "Production"));
            await middleware.InvokeAsync(context, f.Context, f.Db,
                Mock.Of<PointOfSale.Core.Interfaces.IEventPublisher>(), Mock.Of<IAffiliateModeMetricsService>(),
                tokens, Mock.Of<IAffiliateShoppingJourneyService>(), f.Service);
            Assert.True(continued);
            return context;
        }
        Assert.True(AffiliateModeResolver.IsAffiliateMode(await Request()));
        await f.Apply(fault);
        Assert.False(AffiliateModeResolver.IsAffiliateMode(await Request()));
        Assert.True(f.Policy.IsEnabled); Assert.Equal(.1m, f.Policy.CommissionRate);
        Assert.Empty(await f.Db.Orders.IgnoreQueryFilters().ToListAsync());
        Assert.Empty(await f.Db.AffiliateCommissionLedger.IgnoreQueryFilters().ToListAsync());
    }

    internal sealed class Fixture : IAsyncDisposable
    {
        public required ApplicationDbContext Db;
        public required TenantContext Context;
        public required Tenant Tenant;
        public required Product Product;
        public required ProductVariant Variant;
        public required ProductUnit ProductUnit;
        public required StoreConfig Store;
        public required TenantPaymentMethod Payment;
        public required ShippingMethod Shipping;
        public required Warehouse Warehouse;
        public required AffiliateProductPolicy Policy;
        public required EphemeralDataProtectionProvider Protection;
        public required AffiliatePromotionReadinessService Service;
        public required MutableManualOptions ManualOptions;
        public required string InMemoryDatabaseName;
        internal sealed class MutableManualOptions(ManualPaymentOptions value) : IOptions<ManualPaymentOptions>
        {
            public ManualPaymentOptions Value { get; set; } = value;
        }
        public Task<PointOfSale.Core.Interfaces.Affiliate.AffiliatePromotionReadiness> Evaluate() => Service.EvaluateAsync(Tenant.Id, Product.Id);

        public static async Task<Fixture> Create(ApplicationDbContext? database = null, TenantContext? context = null)
        {
            var tenant = new Tenant { Id = Guid.NewGuid(), Name = "ISOLATED readiness", Slug = "readiness-" + Guid.NewGuid().ToString("N"), IsActive = true, Status = TenantStatus.Active, Currency = "EGP" };
            context ??= new TenantContext(); context.SetTenantId(tenant.Id);
            var databaseName = Guid.NewGuid().ToString();
            var db = database ?? new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseInMemoryDatabase(databaseName).Options, context);
            var regularCategory = new Category { Id = Guid.NewGuid(), TenantId = tenant.Id, Name = "Readiness catalog" };
            var product = new Product { Id = Guid.NewGuid(), TenantId = tenant.Id, CategoryId = regularCategory.Id, Name = "Readiness item", SKU = "READY", Slug = "ready-item", IsActive = true, IsOnlineVisible = true, IsSearchable = true, PublishStatus = PublishStatus.Published, TrackStock = false };
            var unit = new PointOfSale.Core.Entities.Unit { Id = Guid.NewGuid(), TenantId = tenant.Id, Name = "Piece", Symbol = "pc", IsActive = true };
            var pu = new ProductUnit { Id = Guid.NewGuid(), ProductId = product.Id, Product = product, UnitId = unit.Id, Unit = unit, SellingPrice = 25, ConversionFactor = 1, IsDefault = true, IsBaseUnit = true };
            var variant = new ProductVariant { Id = Guid.NewGuid(), TenantId = tenant.Id, ProductId = product.Id, Product = product, SKU = "READY", Price = 25, IsDefault = true, IsActive = true };
            var warehouse = new Warehouse { Id = Guid.NewGuid(), TenantId = tenant.Id, Name = "Readiness origin", IsActive = true };
            var store = new StoreConfig { Id = Guid.NewGuid(), TenantId = tenant.Id, StorefrontStatus = "live" };
            var payment = new TenantPaymentMethod { Id = Guid.NewGuid(), TenantId = tenant.Id, Code = "COD", DisplayName = "COD", IsEnabled = true, SupportsOffline = true };
            var shipping = new ShippingMethod { Id = Guid.NewGuid(), TenantId = tenant.Id, Name = "Manual", IsActive = true, FulfillmentWarehouseId = warehouse.Id, FulfillmentWarehouse = warehouse };
            var category = AffiliateCategory.CreateMainCategory("Readiness", "readiness-" + Guid.NewGuid().ToString("N")); category.Id = Guid.NewGuid();
            var policy = AffiliateProductPolicy.Create(tenant.Id, product.Id, .1m, categoryId: category.Id);
            db.AddRange(tenant, regularCategory, product, unit, pu, variant, warehouse, store, payment, shipping, category, policy);
            await db.SaveChangesAsync();
            var protection = new EphemeralDataProtectionProvider();
            var manualOptions = new ManualPaymentOptions { Enabled = true, Environment = ManualPaymentEnvironment.Sandbox,
                FeatureFlags = new() { Enabled = true, ProofUploadEnabled = true, ReviewExecutionEnabled = true, ApprovalExecutionEnabled = true, RejectionExecutionEnabled = true },
                Capabilities = new() { ProviderAdapterCertified = true, RuntimeBridgeCertified = true, EvidenceAdapterCertified = true, ReviewWorkflowCertified = true, ApprovalWorkflowCertified = true, PersistenceCertified = true },
                Instructions = new() { Configured = true, DefaultInstructions = "ISOLATED ONLY" }, Expiration = new() { Configured = true },
                Channels = [new() { Channel = ManualPaymentChannel.VodafoneCash, Enabled = true, DisplayName = "QA", Instructions = "ISOLATED ONLY" }] };
            var manualSettings = new MutableManualOptions(manualOptions);
            var runtime = new StorefrontPaymentRuntimeReadinessService(db, protection,
                Mock.Of<IStripeTenantConfigurationResolver>(), Mock.Of<IPaymobTenantConfigurationResolver>(), Mock.Of<IFawryTenantConfigurationResolver>(),
                new ManualPaymentTenantConfigurationResolver(db, manualSettings), Options.Create(new StripeOptions()), Options.Create(new PaymobOptions()), Options.Create(new FawryOptions()), NullLogger<StorefrontPaymentRuntimeReadinessService>.Instance);
            var shippingService = new ShippingMethodService(Mock.Of<IShippingMethodReadRepository>(), db, Mock.Of<IShippingMethodCacheService>(),
                new ShippingCalculationService(), new CommerceReliabilityTelemetry(), NullLogger<ShippingMethodService>.Instance);
            return new() { Db = db, Context = context, Tenant = tenant, Product = product, Variant = variant, ProductUnit = pu,
                Store = store, Payment = payment, Shipping = shipping, Warehouse = warehouse, Policy = policy, Protection = protection,
                InMemoryDatabaseName = databaseName, ManualOptions = manualSettings, Service = new AffiliatePromotionReadinessService(db, runtime, shippingService) };
        }

        public async Task Apply(string fault)
        {
            switch (fault)
            {
                case "store-disabled": Store.StorefrontStatus = "disabled"; break;
                case "store-maintenance": Store.StorefrontStatus = "maintenance"; break;
                case "store-missing": Db.StoreConfigs.Remove(Store); break;
                case "tenant-inactive": Tenant.Status = TenantStatus.Suspended; Tenant.IsActive = false; break;
                case "payment-none": Db.TenantPaymentMethods.Remove(Payment); break;
                case "payment-disabled": Payment.IsEnabled = false; break;
                case "payment-deleted": Payment.IsDeleted = true; break;
                case "payment-capability-disabled": Payment.SupportsOffline = false; break;
                case "payment-unsupported": Payment.Code = "FAWRY"; break;
                case "payment-manual-incomplete": Payment.Code = "MANUALWALLET"; break;
                case "payment-manual-runtime-disabled": Payment.Code = "MANUALWALLET"; Payment.ConfigEncrypted = Protection.CreateProtector("PaymentMethodSecrets").Protect("{\"walletNumber\":\"01000000000\"}"); ManualOptions.Value = ManualOptions.Value with { FeatureFlags = ManualOptions.Value.FeatureFlags with { ProofUploadEnabled = false } }; break;
                case "payment-manual-valid": Payment.Code = "MANUALWALLET"; Payment.ConfigEncrypted = Protection.CreateProtector("PaymentMethodSecrets").Protect("{\"walletNumber\":\"01000000000\"}"); break;
                case "payment-multiple": Db.TenantPaymentMethods.Add(new TenantPaymentMethod { Id = Guid.NewGuid(), TenantId = Tenant.Id, Code = "MANUALWALLET", DisplayName = "Incomplete", IsEnabled = true, SupportsOffline = true }); break;
                case "shipping-none": Db.ShippingMethods.Remove(Shipping); break;
                case "shipping-inactive": Shipping.IsActive = false; break;
                case "shipping-deleted": Shipping.IsDeleted = true; break;
                case "shipping-no-warehouse": Shipping.FulfillmentWarehouseId = null; Shipping.FulfillmentWarehouse = null; break;
                case "shipping-inactive-warehouse": Warehouse.IsActive = false; break;
                case "shipping-foreign-warehouse":
                    Shipping.FulfillmentWarehouse = new Warehouse { Id = Guid.NewGuid(), TenantId = Guid.NewGuid(), Name = "Foreign origin", IsActive = true };
                    Shipping.FulfillmentWarehouseId = Shipping.FulfillmentWarehouse.Id; Db.Add(Shipping.FulfillmentWarehouse); break;
                case "shipping-unsupported-carrier": Shipping.CarrierCode = "unsupported"; break;
                case "shipping-multiple": Db.ShippingMethods.Add(new ShippingMethod { Id = Guid.NewGuid(), TenantId = Tenant.Id, Name = "Incomplete", IsActive = true }); break;
                case "product-inactive": Product.IsActive = false; break;
                case "product-draft": Product.PublishStatus = PublishStatus.Draft; break;
                case "product-archived": Product.PublishStatus = PublishStatus.Archived; break;
                case "product-hidden": Product.IsOnlineVisible = false; break;
                case "product-not-searchable": Product.IsSearchable = false; break;
                case "product-price-missing": ProductUnit.SellingPrice = null; Variant.Price = 0; break;
                case "product-unit-missing": ProductUnit.IsDeleted = true; break;
                case "product-variant-inactive": Variant.IsActive = false; break;
                case "product-name-missing": Product.Name = ""; break;
                case "product-currency-invalid": Tenant.Currency = "USD"; break;
                case "product-stock-empty": Product.TrackStock = true; break;
                case "product-backorder": Product.TrackStock = true; Product.AllowBackorder = true; break;
                case "product-service": Product.TrackStock = false; break;
                case "product-variants": Product.HasVariants = true; break;
                case "product-split-price-stock":
                    Product.HasVariants = true; Product.TrackStock = true;
                    var unpriced = new ProductVariant { Id = Guid.NewGuid(), TenantId = Tenant.Id, ProductId = Product.Id, Product = Product, SKU = "UNPRICED", IsActive = true, Price = 0 };
                    Db.Add(unpriced); Db.ProductVariantStocks.Add(new ProductVariantStock { Id = Guid.NewGuid(), TenantId = Tenant.Id, VariantId = unpriced.Id, Variant = unpriced, WarehouseId = Warehouse.Id, Warehouse = Warehouse, Quantity = 5 }); break;
            }
            if (fault is "shipping-no-warehouse" or "shipping-inactive-warehouse" or "shipping-foreign-warehouse" or "shipping-multiple" or "product-currency-invalid")
            {
                // Test-only preexisting/legacy corruption. The normal write authority
                // already rejects these states; seed the shared in-memory store using
                // a plain EF context to prove read-side admission also fails closed.
                Assert.True(Db.Database.IsInMemory());
                await using var legacy = new DbContext(new DbContextOptionsBuilder().UseInMemoryDatabase(InMemoryDatabaseName).UseModel(Db.Model).Options);
                foreach (var entry in Db.ChangeTracker.Entries().Where(e => e.State is EntityState.Added or EntityState.Modified).ToList())
                    legacy.Entry(entry.Entity).State = entry.State;
                await legacy.SaveChangesAsync();
                Db.ChangeTracker.Clear();
            }
            else await Db.SaveChangesAsync();
        }
        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }
}
