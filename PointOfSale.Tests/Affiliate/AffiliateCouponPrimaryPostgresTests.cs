using System.Data;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Npgsql;
using PointOfSale.Core.Discounts;
using PointOfSale.Core.Entities;
using PointOfSale.Core.Entities.Affiliate;
using PointOfSale.Core.Enums;
using PointOfSale.Core.Interfaces;
using PointOfSale.Core.Interfaces.Affiliate;
using PointOfSale.Infrastructure.Data;
using PointOfSale.Infrastructure.Discounts;
using PointOfSale.Infrastructure.Instrumentation;
using PointOfSale.Infrastructure.MultiTenancy;
using PointOfSale.Infrastructure.Services.Affiliate;
using PointOfSale.Tests.Infrastructure;
using Xunit.Abstractions;

namespace PointOfSale.Tests.Affiliate;

/// <summary>Primary development only, retained P53C fixtures. No host, migration,
/// database reset, collection, financial driver or external operation.</summary>
[Trait("Category", "PrimaryDevelopmentPostgres")]
public sealed class AffiliateCouponPrimaryPostgresTests(ITestOutputHelper output)
{
    private static readonly Guid Store = Guid.Parse("51510000-2026-4022-a000-000000000001");

    private static async Task<ApplicationDbContext> Open(string? application = null)
    {
        var config = new ConfigurationBuilder().SetBasePath(Path.Combine(TestConfiguration.GetSolutionRoot(), "PointOfSale.API"))
            .AddJsonFile("appsettings.Development.local.json").Build();
        var cs = new NpgsqlConnectionStringBuilder(config.GetConnectionString("DefaultConnection"));
        Assert.Equal("192.168.1.5", cs.Host); Assert.Equal("PosEcommerceDb_Baseline", cs.Database);
        cs.ApplicationName = application ?? "P53C-coupon";
        var tenant = new TenantContext(); tenant.SetTenantId(Store);
        var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseNpgsql(cs.ConnectionString).Options, tenant);
        await db.Database.OpenConnectionAsync();
        await using var cmd = db.Database.GetDbConnection().CreateCommand();
        cmd.CommandText = "select current_database(), host(inet_server_addr())";
        await using var r = await cmd.ExecuteReaderAsync(); Assert.True(await r.ReadAsync());
        Assert.Equal("PosEcommerceDb_Baseline", r.GetString(0)); Assert.Equal("192.168.1.5", r.GetString(1));
        return db;
    }

    private static CouponValidationService Service(ApplicationDbContext db, IEventPublisher? events = null)
    {
        var publisher = new Mock<IEventPublisher>(); publisher.SetReturnsDefault(Task.CompletedTask);
        return new(db, events ?? publisher.Object, NullLogger<CouponValidationService>.Instance);
    }

    private static async Task<(Coupon Coupon, OnlineCustomer Customer, Order Order)> Seed(ApplicationDbContext db, int? max = null)
    {
        Assert.True(await db.Tenants.IgnoreQueryFilters().AnyAsync(t => t.Id == Store && t.Name == "P5.1_TEST_NO_REAL_MONEY"));
        var coupon = new Coupon { Id = Guid.NewGuid(), TenantId = Store, Description = "P53C retained coupon authority test; no real collection",
            DiscountType = DiscountType.Percentage, DiscountValue = 10, MaxUsage = max };
        coupon.SetCode($"P53C{Guid.NewGuid():N}");
        var customer = Customer();
        var order = Order(customer.Id, coupon);
        db.AddRange(coupon, customer, order); await db.SaveChangesAsync();
        return (coupon, customer, order);
    }

    private static OnlineCustomer Customer()
    {
        var id = Guid.NewGuid(); var email = $"p53c-{id:N}@example.invalid";
        return new() { Id = id, TenantId = Store, Email = email, NormalizedEmail = email, DisplayName = "P53C no login fixture",
            IsActive = true, EmailVerified = true, CreatedAtUtc = DateTime.UtcNow };
    }

    private static Order Order(Guid customer, Coupon coupon) => new() { Id = Guid.NewGuid(), TenantId = Store,
        OrderNumber = $"P53C_NO_MONEY_{Guid.NewGuid():N}", OnlineCustomerId = customer, Currency = "EGP",
        CustomerNotes = "P53C factory fixture, no collection, no provider or carrier; retain history", ShippingAddress = "TEST DO NOT SHIP",
        Subtotal = 100, DiscountAmount = 10, TotalDiscountAmount = 10, ShippingCost = 7, TaxAmount = 9, Total = 106,
        CouponId = coupon.Id, CouponCode = coupon.Code, PaymentMethodCode = "COD", PaymentStatus = PaymentStatus.Pending };

    private static CouponValidationSnapshot Snapshot(decimal subtotal = 100, decimal discount = 10,
        Guid product = default, Guid category = default) => new() { OrderSubtotal = subtotal, AppliedDiscountAmount = discount,
            CartItems = [new(product, category)] };

    [PrimaryAffiliateFact]
    public async Task FullLengthV2Referral_CommitsOrderCouponReservationAndAttributionAtomically()
    {
        var store = Guid.Parse("c9c5c1ae-949e-498e-bc63-07242daeb49d");
        await using var db = await Open();
        var link = await db.AffiliatePromotionLinks.IgnoreQueryFilters().SingleAsync(l => l.TenantId == store && l.ReferralCode == "P53CLINK20261023");
        var coupon = await db.Coupons.IgnoreQueryFilters().SingleAsync(c => c.TenantId == store && c.Code == "P53C20261023TEN");
        var customer = Customer(); customer.TenantId = store;
        var order = Order(customer.Id, coupon); order.TenantId = store; order.AffiliateAccountId = link.AffiliateAccountId;
        order.AffiliateReferralCode = link.ReferralCode; order.PromotionLinkId = link.Id;
        var attribution = AffiliateAttribution.CreateV2(store, order.Id, link.AffiliateAccountId!.Value, link.ReferralCode, AttributionSource.ReferralLink, link.Id); attribution.Id = Guid.NewGuid();
        await using (var tx = await db.Database.BeginTransactionAsync())
        {
            db.AddRange(customer, order, attribution); await db.SaveChangesAsync();
            // Open a store-bound context on the SAME transaction/connection for canonical reservation.
            var tenant = new TenantContext(); tenant.SetTenantId(store);
            await using var scoped = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseNpgsql(db.Database.GetDbConnection()).Options, tenant);
            await scoped.Database.UseTransactionAsync(tx.GetDbTransaction());
            await Service(scoped).ReserveCouponAsync(store, coupon.Id, coupon.Code, order.Id, customer.Id,
                Snapshot(product: link.ProductId), "P53C-long-v2");
            await tx.CommitAsync();
        }
        await using var fresh = await Open();
        var persisted = await fresh.AffiliateAttributions.IgnoreQueryFilters().SingleAsync(a => a.OrderId == order.Id);
        Assert.Equal(link.ReferralCode, persisted.ReferralCode); Assert.Equal(link.Id, persisted.PromotionLinkId);
        Assert.Equal(link.ReferralCode, (await fresh.Orders.IgnoreQueryFilters().SingleAsync(o => o.Id == order.Id)).AffiliateReferralCode);
        Assert.Single(await fresh.CouponReservations.IgnoreQueryFilters().Where(r => r.OrderId == order.Id).ToListAsync());
        Assert.False(await fresh.CouponUsages.IgnoreQueryFilters().AnyAsync(u => u.OrderId == order.Id));
        Assert.False(await fresh.AffiliateCommissionLedger.IgnoreQueryFilters().AnyAsync(c => c.OrderId == order.Id));
        output.WriteLine($"P53C long-code transaction fixture order={order.Id}; attribution={attribution.Id}; unpaid, no lifecycle operation.");
    }

    [PrimaryAffiliateFact]
    public async Task BrowserFixtures_ExplicitOneShot_OnlyTaskOwnedCouponAndReferral()
    {
        if (Environment.GetEnvironmentVariable("P53C_BROWSER_SEED") != "1") return;
        var store = Guid.Parse("c9c5c1ae-949e-498e-bc63-07242daeb49d");
        var product = Guid.Parse("15e044bc-eaa1-c905-31a4-658aac4ad14b");
        await using var db = await Open();
        Assert.Equal("AhramStore", (await db.Tenants.IgnoreQueryFilters().SingleAsync(t => t.Id == store)).Name);
        Assert.True(await db.AffiliateProductPolicies.IgnoreQueryFilters().AnyAsync(p => p.ProductId == product && p.TenantId == store && p.IsEnabled && !p.IsDeleted));
        if (await db.Coupons.IgnoreQueryFilters().AnyAsync(c => c.TenantId == store && c.NormalizedCode == "P53C20261023TEN")) return;
        var actor = Guid.NewGuid();
        var account = AffiliateAccount.Create(Guid.Empty, actor, "P53CCPNTEST", "P53C coupon referral — no payout");
        account.Id = Guid.NewGuid(); account.Activate();
        var user = AffiliateUser.Create(Guid.Empty, "p53c-referral@example.invalid", "NO_LOGIN_FIXTURE", account.Id);
        user.Id = actor; user.VerifyEmail();
        var link = AffiliatePromotionLink.Create(store, actor, product, "P53CLINK20261023", account.Id); link.Id = Guid.NewGuid();
        var valid = new Coupon { Id = Guid.NewGuid(), TenantId = store, DiscountType = DiscountType.Percentage, DiscountValue = 10,
            Description = "P53C normal unpaid COD browser checkout; retained test coupon", MinOrderAmount = 1, MaxUsage = 20,
            MaxUsagePerCustomer = 2, ApplicableProductIds = JsonSerializer.Serialize(new[] { product }) };
        valid.SetCode("P53C20261023TEN");
        var expired = new Coupon { Id = Guid.NewGuid(), TenantId = store, DiscountType = DiscountType.Percentage, DiscountValue = 10,
            Description = "P53C expired UI negative test", EndDate = DateTime.UtcNow.AddDays(-1) };
        expired.SetCode("P53C20261023EXPIRED");
        db.AddRange(account, user, link, valid, expired); await db.SaveChangesAsync();
        var evidence = new { store, product, affiliate = account.Id, affiliateUser = actor, promotionLink = link.Id,
            referralCode = link.ReferralCode, coupon = valid.Id, couponCode = valid.Code, expiredCoupon = expired.Id,
            expiredCode = expired.Code, provenance = "P53C factory fixtures only; no historical values, no self-referral override, no login or payout capability" };
        var json = JsonSerializer.Serialize(evidence, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(Path.Combine(TestConfiguration.GetSolutionRoot(), ".ai/evidence/affiliate-p53c-20261023/browser-seed.json"), json);
        output.WriteLine(json);
    }

    [PrimaryAffiliateFact]
    public async Task RepeatedPreview_ReserveConfirmReplay_PersistsOneUsage_AndNoCommission()
    {
        await using var db = await Open(); var (coupon, customer, order) = await Seed(db, 1); var service = Service(db);
        for (var i = 0; i < 3; i++)
            Assert.Equal(10, (await service.ValidateCouponByCodeAsync(Store, "  " + coupon.Code.ToLowerInvariant() + " ", customer.Id, Snapshot(), "P53C-preview")).CalculatedDiscountAmount);
        Assert.False(await db.CouponReservations.AnyAsync(r => r.OrderId == order.Id));
        Assert.False(await db.CouponUsages.AnyAsync(r => r.OrderId == order.Id));
        await using (var tx = await db.Database.BeginTransactionAsync())
        {
            var first = await service.ReserveCouponAsync(Store, coupon.Id, coupon.Code, order.Id, customer.Id, Snapshot(), "P53C-reserve");
            var retry = await service.ReserveCouponAsync(Store, coupon.Id, coupon.Code, order.Id, customer.Id, Snapshot(), "P53C-retry");
            Assert.Equal(first.ReservationId, retry.ReservationId); Assert.True(retry.WasIdempotent);
            await tx.CommitAsync();
        }
        await using (var tx = await db.Database.BeginTransactionAsync())
        {
            var confirmed = await service.ConfirmCouponUsageAsync(Store, order.Id, "P53C-SIMULATED-confirm-no-collection");
            Assert.True(confirmed.WasConfirmed);
            Assert.True((await service.ConfirmCouponUsageAsync(Store, order.Id, "P53C-confirm-retry")).WasAlreadyConfirmed);
            await tx.CommitAsync();
        }
        await using var fresh = await Open();
        var usage = Assert.Single(await fresh.CouponUsages.Where(u => u.OrderId == order.Id).ToListAsync());
        Assert.Equal(10, usage.DiscountAmount); Assert.Equal(100, usage.OrderSubtotal);
        Assert.Equal(1, (await fresh.Coupons.AsNoTracking().SingleAsync(c => c.Id == coupon.Id)).CurrentUsage);
        Assert.Equal(CouponReservationStatus.Confirmed, (await fresh.CouponReservations.SingleAsync(r => r.OrderId == order.Id)).Status);
        Assert.False(await fresh.AffiliateCommissionLedger.IgnoreQueryFilters().AnyAsync(c => c.OrderId == order.Id));
        Assert.False((await Service(fresh).ValidateCouponByIdAsync(Store, coupon.Id, coupon.Code, customer.Id, Snapshot(), "P53C-exhausted")).IsValid);
        output.WriteLine($"P53C retained simulated confirmation: order={order.Id}; coupon={coupon.Id}; usage={usage.Id}. This calls coupon authority only, NOT collection.");
    }

    [PrimaryAffiliateTheory]
    [InlineData("invalid", CouponErrorCodes.Invalid)]
    [InlineData("expired", CouponErrorCodes.Expired)]
    [InlineData("inactive", CouponErrorCodes.Inactive)]
    [InlineData("future", CouponErrorCodes.NotYetValid)]
    [InlineData("minimum", CouponErrorCodes.MinimumNotMet)]
    [InlineData("product", CouponErrorCodes.NotApplicable)]
    [InlineData("category", CouponErrorCodes.NotApplicable)]
    [InlineData("excluded", CouponErrorCodes.NotApplicable)]
    [InlineData("auth", CouponErrorCodes.AuthenticationRequired)]
    [InlineData("tenant", CouponErrorCodes.Invalid)]
    [InlineData("mismatch", CouponErrorCodes.DiscountMismatch)]
    public async Task IneligiblePreview_IsClearAndDoesNotWriteRedemption(string rule, string code)
    {
        await using var db = await Open(); var (coupon, customer, order) = await Seed(db);
        var product = Guid.NewGuid(); var category = Guid.NewGuid();
        switch (rule)
        {
            case "expired": coupon.EndDate = DateTime.UtcNow.AddDays(-1); break;
            case "inactive": coupon.IsActive = false; break;
            case "future": coupon.StartDate = DateTime.UtcNow.AddDays(1); break;
            case "minimum": coupon.MinOrderAmount = 101; break;
            case "product": coupon.ApplicableProductIds = JsonSerializer.Serialize(new[] { Guid.NewGuid() }); break;
            case "category": coupon.ApplicableCategoryIds = JsonSerializer.Serialize(new[] { Guid.NewGuid() }); break;
            case "excluded": coupon.ExcludedProductIds = JsonSerializer.Serialize(new[] { product }); break;
        }
        await db.SaveChangesAsync();
        var result = await Service(db).ValidateCouponByCodeAsync(rule == "tenant" ? Guid.NewGuid() : Store,
            rule == "invalid" ? "P53C-NOT-REAL" : coupon.Code, rule == "auth" ? null : customer.Id,
            Snapshot(discount: rule == "mismatch" ? 20 : 10, product: product, category: category), "P53C-negative");
        Assert.False(result.IsValid); Assert.Equal(code, result.ErrorCode); Assert.False(string.IsNullOrWhiteSpace(result.ErrorMessage));
        Assert.Null(result.CalculatedDiscountAmount);
        Assert.False(await db.CouponUsages.AnyAsync(r => r.CouponId == coupon.Id));
        Assert.False(await db.CouponReservations.AnyAsync(r => r.CouponId == coupon.Id));
        Assert.False(await db.AffiliateCommissionLedger.IgnoreQueryFilters().AnyAsync(r => r.OrderId == order.Id));
    }

    [PrimaryAffiliateFact]
    public async Task ScopeIsCartQualification_NotEligibleLineOnlyDiscount_AndCapIsAuthoritative()
    {
        await using var db = await Open(); var (coupon, customer, _) = await Seed(db);
        var product = Guid.NewGuid(); var category = Guid.NewGuid();
        coupon.ApplicableProductIds = JsonSerializer.Serialize(new[] { product });
        coupon.ApplicableCategoryIds = JsonSerializer.Serialize(new[] { category }); coupon.MaxDiscountAmount = 7.25m;
        await db.SaveChangesAsync();
        var result = await Service(db).ValidateCouponByCodeAsync(Store, coupon.Code, customer.Id,
            new CouponValidationSnapshot { OrderSubtotal = 300, CartItems = [new(product, category), new(Guid.NewGuid(), Guid.NewGuid())] }, "P53C-scope");
        Assert.True(result.IsValid); Assert.Equal(7.25m, result.CalculatedDiscountAmount);
    }

    [PrimaryAffiliateFact]
    public async Task PerCustomerAndFirstOrderRestrictions_UsePersistedAuthority()
    {
        await using var db = await Open(); var (coupon, customer, order) = await Seed(db);
        coupon.MaxUsagePerCustomer = 1; await db.SaveChangesAsync();
        await using (var tx = await db.Database.BeginTransactionAsync())
        {
            await Service(db).ReserveCouponAsync(Store, coupon.Id, coupon.Code, order.Id, customer.Id, Snapshot(), "P53C-customer");
            await tx.CommitAsync();
        }
        Assert.Equal(CouponErrorCodes.CustomerMaxExceeded, (await Service(db).ValidateCouponByCodeAsync(Store, coupon.Code, customer.Id, Snapshot(), "P53C-customer-limit")).ErrorCode);
        coupon.FirstOrderOnly = true; coupon.MaxUsagePerCustomer = null;
        // Simulated collected fact, never call collection/payment/fulfillment services.
        order.PaymentStatus = PaymentStatus.Collected; await db.SaveChangesAsync();
        Assert.Equal(CouponErrorCodes.FirstOrderOnly, (await Service(db).ValidateCouponByCodeAsync(Store, coupon.Code, customer.Id, Snapshot(), "P53C-first", order.Id)).ErrorCode);
    }

    [PrimaryAffiliateFact]
    public async Task FailureAfterSqlUsageSave_RollsBackUsageReservationAndCounter()
    {
        await using var db = await Open(); var (coupon, customer, order) = await Seed(db);
        var publisher = new Mock<IEventPublisher>();
        publisher.Setup(p => p.PublishAsync(It.IsAny<PointOfSale.Core.Events.CouponConsumedEvent>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("P53C failure after SQL usage save"));
        await using (var tx = await db.Database.BeginTransactionAsync())
        {
            await Service(db).ReserveCouponAsync(Store, coupon.Id, coupon.Code, order.Id, customer.Id, Snapshot(), "P53C-rollback");
            await Assert.ThrowsAsync<InvalidOperationException>(() => Service(db, publisher.Object).ConfirmCouponUsageAsync(Store, order.Id, "P53C-rollback"));
            await tx.RollbackAsync();
        }
        await using var fresh = await Open();
        Assert.False(await fresh.CouponUsages.AnyAsync(r => r.OrderId == order.Id));
        Assert.False(await fresh.CouponReservations.AnyAsync(r => r.OrderId == order.Id));
        Assert.Equal(0, (await fresh.Coupons.SingleAsync(c => c.Id == coupon.Id)).CurrentUsage);
    }

    [PrimaryAffiliateFact]
    public async Task LastCapacity_ObservedPostgresLock_OnlyOneOrderWins()
    {
        await using var db = await Open(); var (coupon, customer, order) = await Seed(db, 1);
        var secondCustomer = Customer(); var secondOrder = Order(secondCustomer.Id, coupon);
        db.AddRange(secondCustomer, secondOrder); await db.SaveChangesAsync();
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted);
        await Service(db).ReserveCouponAsync(Store, coupon.Id, coupon.Code, order.Id, customer.Id, Snapshot(), "P53C-winner");
        var application = $"P53C-lock-{Guid.NewGuid():N}";
        var loser = Task.Run(async () =>
        {
            await using var competitor = await Open(application);
            await using var competingTx = await competitor.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted);
            var error = await Assert.ThrowsAsync<CouponAuthorityException>(() => Service(competitor).ReserveCouponAsync(Store,
                coupon.Id, coupon.Code, secondOrder.Id, secondCustomer.Id, Snapshot(), "P53C-loser"));
            Assert.Equal(CouponErrorCodes.MaxUsageExceeded, error.ErrorCode); await competingTx.RollbackAsync();
        });
        var observed = false;
        await using var observer = await Open();
        try
        {
            for (var i = 0; i < 160; i++)
            {
                await using var cmd = observer.Database.GetDbConnection().CreateCommand();
                cmd.CommandText = "select pid,wait_event from pg_stat_activity where application_name=@app and wait_event_type='Lock'";
                var p = cmd.CreateParameter(); p.ParameterName = "app"; p.Value = application; cmd.Parameters.Add(p);
                await using var r = await cmd.ExecuteReaderAsync();
                if (await r.ReadAsync()) { output.WriteLine($"P53C actual primary PostgreSQL lock: pid={r.GetInt32(0)}, wait={r.GetString(1)}"); observed = true; break; }
                await Task.Delay(25);
            }
        }
        finally { await tx.CommitAsync(); }
        await loser.WaitAsync(TimeSpan.FromSeconds(20)); Assert.True(observed);
        await using var fresh = await Open();
        Assert.Single(await fresh.CouponReservations.Where(r => r.CouponId == coupon.Id).ToListAsync());
        Assert.False(await fresh.CouponUsages.AnyAsync(r => r.CouponId == coupon.Id));
    }

    [PrimaryAffiliateFact]
    public async Task DiscountedCommission_RealSqlPersistenceAndReplay_UsesHistoricalNetTaxInclusiveBase()
    {
        await using var db = await Open();
        var coupon = new Coupon { Id = Guid.NewGuid(), TenantId = Store, DiscountType = DiscountType.Percentage,
            DiscountValue = 20, Description = "P53C SIMULATED discounted commission fixture" }; coupon.SetCode($"P53C{Guid.NewGuid():N}");
        var customer = Customer(); var order = Order(customer.Id, coupon);
        var category = new Category { Id = Guid.NewGuid(), TenantId = Store, Name = "P53C financial-state fixture" };
        var product = new Product { Id = Guid.NewGuid(), TenantId = Store, CategoryId = category.Id, Name = "P53C no stock no delivery" };
        var affiliateCategory = AffiliateCategory.CreateMainCategory("P53C fixture", $"p53c-{Guid.NewGuid():N}", 1); affiliateCategory.Id = Guid.NewGuid();
        var policy = AffiliateProductPolicy.Create(Store, product.Id, .1m, 4m, affiliateCategory.Id); policy.Id = Guid.NewGuid();
        var actor = Guid.NewGuid(); var account = AffiliateAccount.Create(Guid.Empty, actor, $"C{Guid.NewGuid():N}"[..12], "P53C SIMULATED financial state"); account.Id = Guid.NewGuid(); account.Activate();
        var user = AffiliateUser.Create(Guid.Empty, $"p53c-{actor:N}@example.invalid", "NO_LOGIN_FIXTURE", account.Id); user.Id = actor; user.VerifyEmail();
        // Factory-seeded recognized payment fact, NOT a collection or payment command.
        order.PaymentStatus = PaymentStatus.Collected; order.FinancialPostingStatus = FinancialPostingStatus.NotRequired;
        order.FulfillmentMode = FulfillmentMode.Carrier; order.Subtotal = 100; order.DiscountAmount = 20; order.TotalDiscountAmount = 20;
        order.TaxAmount = 10; order.Total = 97; order.AffiliateAccountId = account.Id; order.AffiliateReferralCode = account.ReferralCode;
        var item = new OrderItem { Id = Guid.NewGuid(), OrderId = order.Id, ProductId = product.Id, Quantity = 1, UnitPrice = 100,
            DiscountAmount = 20, TaxAmount = 10, Total = 90, PaidAmount = 90, AffiliateCommissionEligible = true,
            AffiliateCommissionPolicyIdSnapshot = policy.Id, AffiliateCommissionRateSnapshot = .1m, AffiliateMaxCommissionPerItemSnapshot = 4,
            AffiliateCommissionBaseAmountSnapshot = 90, AffiliateCommissionAmountSnapshot = 4, AffiliateCommissionSnapshotCapturedAtUtc = DateTime.UtcNow };
        order.Items.Add(item);
        var attribution = AffiliateAttribution.Create(Store, order.Id, account.Id, account.ReferralCode, AttributionSource.ReferralLink); attribution.Id = Guid.NewGuid();
        db.AddRange(coupon, customer, order, category, product, affiliateCategory, policy, account, user, attribution); await db.SaveChangesAsync();
        AffiliateService CommissionService(ApplicationDbContext context)
        {
            var tenant = new TenantContext(); tenant.SetTenantId(Store);
            var publisher = new Mock<IEventPublisher>(); publisher.SetReturnsDefault(Task.CompletedTask);
            return new(context, tenant, publisher.Object, new AffiliateTelemetry(), NullLogger<AffiliateService>.Instance,
                Mock.Of<IAffiliatePayoutService>(MockBehavior.Strict), Mock.Of<IAffiliateSettlementOrchestrator>(MockBehavior.Strict), Options.Create(new AffiliateSettlementOptions()));
        }
        await CommissionService(db).RecordCommissionForOrderAsync(order.Id);
        await CommissionService(db).RecordCommissionForOrderAsync(order.Id);
        await using (var fresh = await Open())
        {
            var earned = Assert.Single(await fresh.AffiliateCommissionLedger.IgnoreQueryFilters().Where(e => e.OrderId == order.Id).ToListAsync());
            Assert.Equal(90, earned.BaseAmount); Assert.Equal(4, earned.CommissionAmount); Assert.Equal(attribution.Id, earned.AttributionId);
            var persisted = await fresh.Orders.Include(o => o.Items).SingleAsync(o => o.Id == order.Id);
            Assert.Equal(persisted.Total, persisted.Items.Sum(i => i.Total) + persisted.ShippingCost + persisted.PaymentFee);
            persisted.Items.Single().RefundedAmount = 45; await fresh.SaveChangesAsync();
            await CommissionService(fresh).RecordCommissionForOrderAsync(order.Id);
            await CommissionService(fresh).RecordCommissionForOrderAsync(order.Id);
        }
        await using (var fresh = await Open())
        {
            var rows = await fresh.AffiliateCommissionLedger.IgnoreQueryFilters().Where(e => e.OrderId == order.Id).ToListAsync();
            var earned = Assert.Single(rows.Where(e => e.EntryType == CommissionEntryType.Earned));
            var reversal = Assert.Single(rows.Where(e => e.EntryType == CommissionEntryType.Reversed));
            Assert.Equal(earned.Id, reversal.OriginalEntryId); Assert.Equal(45, reversal.BaseAmount); Assert.Equal(2, reversal.CommissionAmount);
            var persistedItem = await fresh.OrderItems.SingleAsync(i => i.Id == item.Id); persistedItem.RefundedAmount = 90; await fresh.SaveChangesAsync();
            await CommissionService(fresh).RecordCommissionForOrderAsync(order.Id); await CommissionService(fresh).RecordCommissionForOrderAsync(order.Id);
        }
        await using var final = await Open();
        var reversed = await final.AffiliateCommissionLedger.IgnoreQueryFilters().Where(e => e.OrderId == order.Id && e.EntryType == CommissionEntryType.Reversed).ToListAsync();
        Assert.Equal(90, reversed.Sum(e => e.BaseAmount)); Assert.Equal(4, reversed.Sum(e => e.CommissionAmount));
        Assert.False(await final.AffiliateBalanceLedger.IgnoreQueryFilters().AnyAsync(e => e.AffiliateAccountId == account.Id));
        output.WriteLine($"P53C SIMULATED discounted commission/refund SQL fixture order={order.Id}; item={item.Id}; attribution={attribution.Id}; no collection, delivery, approval, transfer or payout executed.");
    }
}
