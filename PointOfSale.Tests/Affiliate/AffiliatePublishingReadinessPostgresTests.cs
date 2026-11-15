using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Npgsql;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PointOfSale.API.Controllers;
using PointOfSale.API.Services;
using PointOfSale.Core.Entities.Affiliate;
using PointOfSale.Core.Interfaces;
using PointOfSale.Core.Interfaces.Affiliate;
using PointOfSale.Infrastructure.Instrumentation;
using PointOfSale.Infrastructure.MultiTenancy;
using PointOfSale.Infrastructure.Services.Affiliate;
using PointOfSale.Tests.Infrastructure;

namespace PointOfSale.Tests.Affiliate;

public sealed class AffiliatePublishingReadinessPostgresTests : IDisposable
{
    private readonly TestDatabaseScope _scope = TestDatabaseScope.Open();
    public void Dispose() => _scope.Dispose();
    private Task<AffiliatePublishingReadinessTests.Fixture> Create()
    {
        var context = new TenantContext();
        return AffiliatePublishingReadinessTests.Fixture.Create(_scope.CreateDbContext(context), context);
    }
    private static AffiliateService PolicyService(AffiliatePublishingReadinessTests.Fixture f) =>
        new(f.Db, f.Context, Mock.Of<IEventPublisher>(), new AffiliateTelemetry(), NullLogger<AffiliateService>.Instance,
            Mock.Of<IAffiliatePayoutService>(), Mock.Of<IAffiliateSettlementOrchestrator>(),
            Options.Create(new AffiliateSettlementOptions()), promotionReadiness: f.Service);

    [Theory]
    [InlineData("store-disabled", "STOREFRONT_NOT_ENABLED")]
    [InlineData("payment-disabled", "PAYMENT_METHOD_REQUIRED")]
    [InlineData("shipping-inactive", "SHIPPING_METHOD_REQUIRED")]
    [InlineData("product-archived", "PRODUCT_ARCHIVED")]
    public async Task DirectMerchantApi_ReturnsAllCurrentBlockersWithoutEnabling(string fault, string code)
    {
        await using var f = await Create();
        f.Policy.Disable(); await f.Db.SaveChangesAsync();
        await f.Apply(fault);
        var controller = new AdminProductAffiliateController(f.Db, PolicyService(f), Mock.Of<IAffiliateReadinessService>(),
            f.Context, NullLogger<AdminProductAffiliateController>.Instance, f.Service)
            { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() } };
        var response = Assert.IsType<BadRequestObjectResult>((await controller.EnableAffiliateWithCategory(f.Product.Id,
            new EnableAffiliateWithCategoryRequest { CommissionRate = .2m, CategoryId = f.Policy.AffiliateCategoryId!.Value }, default)).Result);
        var payload = System.Text.Json.JsonSerializer.Serialize(response.Value);
        Assert.Contains("AFFILIATE_PROMOTION_NOT_READY", payload); Assert.Contains(code, payload);
        await f.Db.Entry(f.Policy).ReloadAsync();
        Assert.False(f.Policy.IsEnabled); Assert.Equal(.1m, f.Policy.CommissionRate);
    }

    [Theory]
    [InlineData("store-disabled")]
    [InlineData("payment-disabled")]
    [InlineData("shipping-inactive")]
    [InlineData("product-archived")]
    public async Task DirectLinkApi_RechecksStaleOffers_PreservesLinkAndRestoresIdempotently(string fault)
    {
        await using var f = await Create();
        f.Tenant.StorefrontBaseUrl = "https://" + f.Tenant.Slug + ".example.test";
        var userId = Guid.NewGuid();
        var account = AffiliateAccount.Create(Guid.Empty, userId, "ISOLATED", "QA" + Guid.NewGuid().ToString("N")[..8]);
        account.Id = Guid.NewGuid(); account.Activate();
        var user = AffiliateUser.Create(Guid.Empty, "qa-" + userId.ToString("N") + "@example.test", "test-hash", account.Id);
        user.Id = userId; user.VerifyEmail(); f.Db.AddRange(account, user); await f.Db.SaveChangesAsync();
        using var services = new ServiceCollection().BuildServiceProvider();
        var resolver = new StorefrontUrlResolver(new ConfigurationBuilder().Build(), NullLogger<StorefrontUrlResolver>.Instance, services.GetRequiredService<IServiceScopeFactory>());
        var controller = new AffiliatePromotionLinksController(f.Db, resolver, NullLogger<AffiliatePromotionLinksController>.Instance, f.Service)
        { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity([
            new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()), new Claim("affiliate_account_id", account.Id.ToString())], "Test")) } } };
        var request = new CreatePromotionLinkRequest { TenantId = f.Tenant.Id, ProductId = f.Product.Id };
        var created = Assert.IsType<PromotionLinkResponse>(Assert.IsType<CreatedAtActionResult>(await controller.Create(request, default)).Value);
        await f.Apply(fault);
        var rejected = Assert.IsType<BadRequestObjectResult>(await controller.Create(request, default));
        Assert.Contains("AFFILIATE_PROMOTION_NOT_READY", System.Text.Json.JsonSerializer.Serialize(rejected.Value));
        Assert.Empty(Assert.IsType<List<PromotionLinkResponse>>(Assert.IsType<OkObjectResult>(await controller.List(default)).Value));
        Assert.IsType<NotFoundObjectResult>(await controller.GetById(created.Id, default));
        var link = Assert.Single(await f.Db.AffiliatePromotionLinks.IgnoreQueryFilters().ToListAsync());
        Assert.True(link.IsActive); Assert.Equal(created.ReferralCode, link.ReferralCode); Assert.True(f.Policy.IsEnabled);
        f.Store.StorefrontStatus = "live"; f.Payment.IsEnabled = true; f.Shipping.IsActive = true;
        f.Product.PublishStatus = PointOfSale.Core.Enums.PublishStatus.Published; await f.Db.SaveChangesAsync();
        Assert.Equal(created.Id, Assert.IsType<PromotionLinkResponse>(Assert.IsType<OkObjectResult>(await controller.Create(request, default)).Value).Id);
        Assert.IsType<NotFoundObjectResult>(await controller.Create(new() { TenantId = Guid.NewGuid(), ProductId = f.Product.Id }, default));
        Assert.Single(await f.Db.AffiliatePromotionLinks.IgnoreQueryFilters().ToListAsync());
    }

    [Fact]
    public async Task StaleEligibility_IsRechecked_EntryRejectedWithoutPolicyOrProductMutation()
    {
        await using var f = await Create();
        f.Policy.Disable(); await f.Db.SaveChangesAsync();
        Assert.True((await f.Evaluate()).PrerequisitesReady);
        await using (var other = _scope.CreateDbContext(f.Tenant.Id))
        {
            await other.TenantPaymentMethods.Where(p => p.Id == f.Payment.Id).ExecuteUpdateAsync(s => s.SetProperty(p => p.IsEnabled, false));
        }
        var exception = await Assert.ThrowsAsync<AffiliatePromotionNotReadyException>(() => PolicyService(f)
            .EnableAffiliateWithCategoryAsync(f.Product.Id, .2m, f.Policy.AffiliateCategoryId!.Value));
        Assert.Contains("PAYMENT_METHOD_REQUIRED", exception.Readiness.BlockerCodes);
        await f.Db.Entry(f.Policy).ReloadAsync(); await f.Db.Entry(f.Product).ReloadAsync();
        Assert.False(f.Policy.IsEnabled); Assert.Equal(.1m, f.Policy.CommissionRate);
        Assert.True(f.Product.IsActive); Assert.Equal(PointOfSale.Core.Enums.PublishStatus.Published, f.Product.PublishStatus);
    }

    [Theory]
    [InlineData("payment")]
    [InlineData("shipping")]
    [InlineData("storefront")]
    [InlineData("product")]
    public async Task AdmissionLocks_ExcludeConcurrentReadinessChanges_UntilCommit(string kind)
    {
        await using var f = await Create();
        await using var transaction = await f.Db.Database.BeginTransactionAsync();
        Assert.True((await f.Service.EvaluateForMutationAsync(f.Tenant.Id, f.Product.Id)).IsPromotionEligible);
        await using var other = _scope.CreateDbContext(f.Tenant.Id);
        await other.Database.OpenConnectionAsync();
        await other.Database.ExecuteSqlRawAsync("SET lock_timeout='300ms'");
        var sql = kind switch
        {
            "payment" => $"UPDATE \"TenantPaymentMethods\" SET \"IsEnabled\"=false WHERE \"Id\"='{f.Payment.Id}'",
            "shipping" => $"UPDATE \"ShippingMethods\" SET \"IsActive\"=false WHERE \"Id\"='{f.Shipping.Id}'",
            "storefront" => $"UPDATE \"StoreConfigs\" SET \"StorefrontStatus\"='disabled' WHERE \"Id\"='{f.Store.Id}'",
            _ => $"UPDATE \"Products\" SET \"IsActive\"=false WHERE \"Id\"='{f.Product.Id}'"
        };
        var blocked = await Assert.ThrowsAsync<PostgresException>(() => other.Database.ExecuteSqlRawAsync(sql));
        Assert.Equal(PostgresErrorCodes.LockNotAvailable, blocked.SqlState);
        await transaction.CommitAsync();
        Assert.Equal(1, await other.Database.ExecuteSqlRawAsync(sql));
        Assert.False((await f.Evaluate()).IsPromotionEligible);
        Assert.True(f.Policy.IsEnabled);
    }

    [Fact]
    public async Task EnabledPolicyEdits_AreAllowedWhenUnready_ButNeverBecomePromotable()
    {
        await using var f = await Create();
        await f.Apply("payment-disabled");
        var policy = await PolicyService(f).EnableAffiliateWithCategoryAsync(f.Product.Id, .15m, f.Policy.AffiliateCategoryId!.Value);
        Assert.True(policy.Policy.IsEnabled); Assert.Equal(.15m, policy.Policy.CommissionRate);
        Assert.False((await f.Evaluate()).IsPromotionEligible);
        Assert.Empty(await f.Db.Orders.IgnoreQueryFilters().ToListAsync());
        Assert.Empty(await f.Db.AffiliateCommissionLedger.IgnoreQueryFilters().ToListAsync());
    }

    [Fact]
    public async Task RepeatedEntry_IsOnePolicy_AndDoesNotChangeOrdinaryProductStatus()
    {
        await using var f = await Create();
        f.Policy.Disable(); await f.Db.SaveChangesAsync();
        var service = PolicyService(f);
        var first = await service.EnableAffiliateWithCategoryAsync(f.Product.Id, .1m, f.Policy.AffiliateCategoryId!.Value);
        var second = await service.EnableAffiliateWithCategoryAsync(f.Product.Id, .1m, f.Policy.AffiliateCategoryId!.Value);
        Assert.Equal(first.Policy.Id, second.Policy.Id);
        Assert.Single(await f.Db.AffiliateProductPolicies.ToListAsync());
        Assert.True((await f.Evaluate()).IsPromotionEligible);
        await f.Db.Entry(f.Product).ReloadAsync();
        Assert.True(f.Product.IsActive); Assert.Equal(PointOfSale.Core.Enums.PublishStatus.Published, f.Product.PublishStatus);
    }
}
