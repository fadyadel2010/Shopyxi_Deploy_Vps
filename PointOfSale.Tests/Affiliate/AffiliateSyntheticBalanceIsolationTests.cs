using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using PointOfSale.API.Controllers;
using PointOfSale.Core.Entities;
using PointOfSale.Core.Entities.Affiliate;
using PointOfSale.Core.Enums;
using PointOfSale.Core.Exceptions.Affiliate;
using PointOfSale.Core.Interfaces;
using PointOfSale.Core.Interfaces.Affiliate;
using PointOfSale.Infrastructure.Data;
using PointOfSale.Infrastructure.Instrumentation;
using PointOfSale.Infrastructure.MultiTenancy;
using PointOfSale.Infrastructure.Services.Affiliate;
using PointOfSale.Tests.Infrastructure;

namespace PointOfSale.Tests.Affiliate;

public sealed class AffiliateSyntheticBalanceIsolationTests
{
    private sealed class Fixture : IAsyncDisposable
    {
        public ApplicationDbContext Db { get; }
        public Guid Store { get; } = Guid.NewGuid();
        public AffiliateAccount Account { get; }
        public AffiliatePayoutService Service => new(Db, NullLogger<AffiliatePayoutService>.Instance);
        public Fixture(ApplicationDbContext? db = null)
        {
            Db = db ?? new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning)).Options);
            if (db == null) Db.Currencies.Add(new Currency { Code = "EGP", Name = "Egyptian Pound", Symbol = "EGP", DecimalPlaces = 2 });
            Account = AffiliateAccount.Create(Guid.Empty, Guid.NewGuid(), "ISOLATIONQA", "Isolated balance");
            Account.Id = Guid.NewGuid(); Account.Activate();
            Account.UpdatePayoutWallet("01000000000", "VodafoneCash");
            var user = AffiliateUser.Create(Guid.Empty, "isolated@example.invalid", "not-a-password", Account.Id);
            user.Id = Account.UserId;
            Db.AddRange(new Tenant { Id = Store, Name = "Isolated balance", Slug = $"isolation-{Store:N}", Currency = "EGP", IsActive = true, Status = TenantStatus.Active }, Account, user);
        }
        public AffiliateCommissionLedger AddCommission(decimal commission, bool synthetic = false, string artifact = "order")
        {
            var category = new Category { Id = Guid.NewGuid(), TenantId = Store, Name = "Isolated" };
            var product = new Product { Id = Guid.NewGuid(), TenantId = Store, CategoryId = category.Id, Name = "Isolated" };
            var order = new Order { Id = Guid.NewGuid(), TenantId = Store, OrderNumber = $"ISOLATION_{Guid.NewGuid():N}",
                Currency = "EGP", Total = commission * 10, ShippingAddress = "Isolated fixture",
                AffiliateAccountId = Account.Id, AffiliateReferralCode = Account.ReferralCode,
                PaymentStatus = synthetic ? PaymentStatus.SyntheticQaCollected : PaymentStatus.Collected };
            var item = new OrderItem { Id = Guid.NewGuid(), OrderId = order.Id, ProductId = product.Id, Quantity = 1, UnitPrice = order.Total, Total = order.Total };
            order.Items.Add(item);
            var attribution = AffiliateAttribution.Create(Store, order.Id, Account.Id, Account.ReferralCode, AttributionSource.ReferralLink);
            attribution.Id = Guid.NewGuid();
            var earned = AffiliateCommissionLedger.CreateEarned(Store, attribution.Id, Account.Id, order.Id, item.Id, order.Total, .1m, null, "EGP");
            Db.AddRange(category, product, order, attribution, earned, AffiliateCommissionLedger.CreateApproved(earned));
            if (synthetic)
            {
                var customer = new OnlineCustomer { Id = Guid.NewGuid(), TenantId = Store, Email = "fixture@example.invalid", CreatedAtUtc = DateTime.UtcNow };
                var link = AffiliatePromotionLink.Create(Store, Account.UserId, product.Id, Guid.NewGuid().ToString("N")[..10], Account.Id);
                link.Id = Guid.NewGuid();
                var run = new SyntheticQaRun { Id = Guid.NewGuid(), TenantId = Store, ProductId = product.Id, CustomerId = customer.Id,
                    PromotionLinkId = link.Id, CreatedAtUtc = DateTime.UtcNow, ExpiresAtUtc = DateTime.UtcNow.AddHours(1) };
                Db.AddRange(customer, link, run);
                if (artifact == "order") Db.Add(new SyntheticQaOrder { OrderId = order.Id, TenantId = Store, RunId = run.Id, EnrolledAtUtc = DateTime.UtcNow });
                if (artifact == "evidence") Db.Add(new SyntheticQaEvidence { Id = Guid.NewGuid(), OrderId = order.Id, TenantId = Store, RunId = run.Id, Kind = "Collection", Nonce = Guid.NewGuid().ToString(), IssuedAtUtc = DateTime.UtcNow });
                if (artifact == "posting") Db.Add(new SyntheticQaCommissionPosting { Id = Guid.NewGuid(), OrderId = order.Id, Amount = commission, Currency = "EGP", PostedAtUtc = DateTime.UtcNow });
            }
            return earned;
        }
        public void Reverse(AffiliateCommissionLedger earned, decimal amount) => Db.Add(AffiliateCommissionLedger.CreateReversalWithCustomKey(
            Store, earned.Id, earned.AttributionId, Account.Id, earned.OrderId, earned.OrderItemId, amount * 10, amount, .1m, "EGP", $"reverse:{Guid.NewGuid()}"));
        public AffiliatePayoutRequest Reservation(decimal amount) => AffiliatePayoutRequest.Create(Store, Account.Id, amount, 999999m, "EGP",
            Account.PayoutWalletNumber!, Account.PayoutWalletType!, Account.PayoutWalletUpdatedAtUtc);
        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }

    private static async Task AssertNoMoney(ApplicationDbContext db)
    {
        Assert.Empty(await db.AffiliateBalanceLedger.IgnoreQueryFilters().ToListAsync());
        Assert.False(await db.AffiliateCommissionLedger.IgnoreQueryFilters().AnyAsync(e => e.EntryType == CommissionEntryType.Paid));
        Assert.Empty(await db.JournalEntries.IgnoreQueryFilters().ToListAsync());
        Assert.Empty(await db.LedgerEntries.IgnoreQueryFilters().ToListAsync());
        Assert.Empty(await db.AffiliatePayoutTransferEvidence.IgnoreQueryFilters().ToListAsync());
        Assert.Empty(await db.AffiliatePayoutTransferAllocations.IgnoreQueryFilters().ToListAsync());
        Assert.Empty(await db.AccountingOutboxMessages.IgnoreQueryFilters().ToListAsync());
    }

    [Fact]
    public async Task PortalSummaryAndWithdrawalEndpoint_IgnoreFabricatedBalanceInBody()
    {
        await using var f = new Fixture(); f.AddCommission(500m, true); await f.Db.SaveChangesAsync();
        var controller = new AffiliatePortalController(f.Db, null!, null!, f.Service, null!, null!, NullLogger<AffiliatePortalController>.Instance)
        { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(
            new ClaimsIdentity(new[] { new Claim("affiliate_account_id", f.Account.Id.ToString()) }, "isolated")) } } };
        var dashboard = Assert.IsType<AffiliateDashboardResponse>(Assert.IsType<OkObjectResult>((await controller.GetDashboard(default)).Result).Value);
        Assert.Equal(0m, dashboard.TotalApproved); Assert.Equal(0m, dashboard.TotalEarned);
        Assert.Equal(500m, Assert.Single(dashboard.EarningsByCurrency).SyntheticApprovedExcluded);
        var payouts = Assert.IsType<AffiliatePayoutBalanceResponse>(Assert.IsType<OkObjectResult>((await controller.GetPayouts(ct: default)).Result).Value);
        Assert.Equal(0m, payouts.AvailableForWithdrawal); Assert.Equal(500m, payouts.SyntheticApprovedExcluded);
        var body = JsonSerializer.Deserialize<AffiliateRequestPayoutBody>(
            $$"""{"amount":500,"storeTenantId":"{{f.Store}}","availableForWithdrawal":999999,"totalApproved":999999}""",
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        var rejection = Assert.IsType<ObjectResult>((await controller.RequestPayout(body, default)).Result);
        Assert.InRange(rejection.StatusCode!.Value, 400, 499);
        Assert.Empty(await f.Db.AffiliatePayoutRequests.IgnoreQueryFilters().ToListAsync()); await AssertNoMoney(f.Db);
    }

    [Theory]
    [InlineData("order")]
    [InlineData("evidence")]
    [InlineData("posting")]
    public async Task SyntheticOnlyAboveMinimum_ReplaysRejectWithoutAnyFinancialEffects(string artifact)
    {
        await using var f = new Fixture();
        f.AddCommission(500m, true, artifact); await f.Db.SaveChangesAsync();
        var balance = await f.Service.GetAvailableBalanceAsync(f.Account.Id);
        Assert.Equal(0m, balance.TotalApproved); Assert.Equal(0m, balance.AvailableForWithdrawal);
        Assert.Equal(500m, balance.SyntheticApprovedExcluded); Assert.False(balance.CanWithdraw);
        for (var i = 0; i < 3; i++)
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => f.Service.RequestPayoutAsync(f.Account.Id, 500m, f.Store));
            Assert.Contains("exceeds available balance", error.Message); // Not a minimum-threshold rejection.
        }
        Assert.Empty(await f.Db.AffiliatePayoutRequests.IgnoreQueryFilters().ToListAsync());
        await AssertNoMoney(f.Db);
        Assert.DoesNotContain(typeof(AffiliateRequestPayoutBody).GetProperties(), p => p.Name.Contains("Balance"));
    }

    [Fact]
    public async Task Mixed_RealPartialReversalPaidAndReservations_UnchangedBySyntheticDerivedEntries()
    {
        await using var f = new Fixture();
        var real = f.AddCommission(500m); f.Reverse(real, 50m);
        var qa = f.AddCommission(900m, true); f.Reverse(qa, 400m);
        var realApproved = f.Db.ChangeTracker.Entries<AffiliateCommissionLedger>().Single(e => e.Entity.OrderItemId == real.OrderItemId && e.Entity.EntryType == CommissionEntryType.Approved).Entity;
        var qaApproved = f.Db.ChangeTracker.Entries<AffiliateCommissionLedger>().Single(e => e.Entity.OrderItemId == qa.OrderItemId && e.Entity.EntryType == CommissionEntryType.Approved).Entity;
        f.Db.AddRange(AffiliateCommissionLedger.CreatePaid(realApproved, null), AffiliateCommissionLedger.CreatePaid(qaApproved, null));
        f.Db.Add(AffiliateBalanceLedger.CreateWithdrawal(f.Store, f.Account.Id, Guid.NewGuid(), 100m, "EGP", "isolated only"));
        f.Db.Add(f.Reservation(100m)); await f.Db.SaveChangesAsync();
        var balance = await f.Service.GetAvailableBalanceAsync(f.Account.Id, f.Store);
        Assert.Equal(450m, balance.TotalApproved); Assert.Equal(100m, balance.TotalPaid);
        Assert.Equal(100m, balance.TotalPendingRequests); Assert.Equal(250m, balance.RawAvailable);
        Assert.Equal(500m, balance.SyntheticApprovedExcluded);
        var eligibility = await AffiliateCommissionEligibility.LoadAsync(f.Db, f.Account.Id, f.Store);
        Assert.Equal(4, eligibility.RealEntries.Count); Assert.Equal(4, eligibility.SyntheticEntries.Count);
        Assert.Equal(0m, (await f.Service.GetAvailableBalanceAsync(Guid.NewGuid(), f.Store)).RawAvailable);
        var otherStore = Guid.NewGuid();
        f.Db.Add(new Tenant { Id = otherStore, Name = "Other isolated store", Slug = $"other-{otherStore:N}", Currency = "EGP", IsActive = true, Status = TenantStatus.Active });
        await f.Db.SaveChangesAsync();
        Assert.Equal(0m, (await f.Service.GetAvailableBalanceAsync(f.Account.Id, otherStore)).TotalApproved);
    }

    [Theory]
    [InlineData("missing-parent")]
    [InlineData("wrong-item")]
    [InlineData("wrong-order")]
    [InlineData("cycle")]
    [InlineData("missing-provenance")]
    public async Task AmbiguousLineage_FailsClosedAndNamesClassificationProblem(string problem)
    {
        await using var f = new Fixture();
        var earned = f.AddCommission(500m, problem == "missing-provenance", "none");
        var approved = f.Db.ChangeTracker.Entries<AffiliateCommissionLedger>().Single(e => e.Entity.EntryType == CommissionEntryType.Approved).Entity;
        void Set(string name, object value) => typeof(AffiliateCommissionLedger).GetProperty(name)!.SetValue(approved, value);
        if (problem == "missing-parent") Set("OriginalEntryId", Guid.NewGuid());
        if (problem == "wrong-item") Set("OrderItemId", Guid.NewGuid());
        if (problem == "wrong-order") Set("OrderId", Guid.NewGuid());
        if (problem == "cycle") Set("OriginalEntryId", approved.Id);
        await f.Db.SaveChangesAsync();
        var error = await Assert.ThrowsAsync<AffiliatePayoutAuthorityException>(() => f.Service.GetAvailableBalanceAsync(f.Account.Id));
        Assert.Contains("LINEAGE_UNCLASSIFIED", error.Message);
        await Assert.ThrowsAsync<AffiliatePayoutAuthorityException>(() => f.Service.RequestPayoutAsync(f.Account.Id, 500m, f.Store));
        Assert.Empty(await f.Db.AffiliatePayoutRequests.IgnoreQueryFilters().ToListAsync());
        await AssertNoMoney(f.Db);
    }

    [Fact]
    public async Task StaleClientCannotSpendSyntheticOrReservedRealFunds_AndMinimumStillApplies()
    {
        await using var f = new Fixture(); f.AddCommission(200m); f.AddCommission(1000m, true); await f.Db.SaveChangesAsync();
        var request = await f.Service.RequestPayoutAsync(f.Account.Id, 150m, f.Store);
        Assert.Equal(request.Id, (await f.Service.RequestPayoutAsync(f.Account.Id, 150m, f.Store)).Id);
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Service.RequestPayoutAsync(f.Account.Id, 200m, f.Store));
        var below = await Assert.ThrowsAsync<InvalidOperationException>(() => f.Service.RequestPayoutAsync(f.Account.Id, 50m, f.Store));
        Assert.Contains("minimum threshold", below.Message);
        Assert.Single(await f.Db.AffiliatePayoutRequests.IgnoreQueryFilters().ToListAsync());
        Assert.Equal(50m, (await f.Service.GetAvailableBalanceAsync(f.Account.Id)).RawAvailable);
        await AssertNoMoney(f.Db);
    }

    [Fact]
    public async Task PreviouslySyntheticFundedReservation_CannotBeApprovedOrReplayed()
    {
        await using var f = new Fixture(); f.AddCommission(500m, true);
        var request = f.Reservation(500m); f.Db.Add(request); await f.Db.SaveChangesAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Service.ApprovePayoutAsync(request.Id, f.Store, Guid.NewGuid()));
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Service.RequestPayoutAsync(f.Account.Id, 500m, f.Store));
        Assert.Equal(PayoutRequestStatus.Pending, request.Status);
        Assert.Null(request.ApprovedAtUtc); await AssertNoMoney(f.Db);
    }

    [Fact]
    public async Task LegacySyntheticOrderCannotConsumeRealCommissionsOrReachSettlement()
    {
        await using var f = new Fixture(); f.AddCommission(500m);
        var qa = f.AddCommission(500m, true); await f.Db.SaveChangesAsync();
        var orchestrator = new Mock<IAffiliateSettlementOrchestrator>(MockBehavior.Strict);
        var service = new AffiliateService(f.Db, new TenantContext(), Mock.Of<IEventPublisher>(MockBehavior.Strict),
            new AffiliateTelemetry(), NullLogger<AffiliateService>.Instance, f.Service, orchestrator.Object,
            Options.Create(new AffiliateSettlementOptions()));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => service.RecordCommissionPaidAsync(qa.OrderId, "ISOLATED-NOT-A-TRANSFER"));
        Assert.Contains("SYNTHETIC_QA_EXCLUDED", error.Message);
        orchestrator.VerifyNoOtherCalls(); await AssertNoMoney(f.Db);
        Assert.Equal(500m, (await f.Service.GetAvailableBalanceAsync(f.Account.Id)).RawAvailable);
    }

    [Fact]
    public async Task Postgres_ConcurrentSyntheticRejectionsAndLegitimateReservations_AreAtomic()
    {
        await using var database = TestDatabaseScope.Open();
        await using var f = new Fixture(database.CreateDbContext());
        f.AddCommission(1000m, true); await f.Db.SaveChangesAsync();
        async Task Attempt(bool reject)
        {
            await using var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseNpgsql(f.Db.Database.GetConnectionString(), options => options.EnableRetryOnFailure()).Options);
            var service = new AffiliatePayoutService(db, NullLogger<AffiliatePayoutService>.Instance);
            if (reject) await Assert.ThrowsAsync<InvalidOperationException>(() => service.RequestPayoutAsync(f.Account.Id, 200m, f.Store));
            else await service.RequestPayoutAsync(f.Account.Id, 200m, f.Store);
        }
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Attempt(true)));
        await Attempt(true); // Replay after all concurrent rejections.
        Assert.Empty(await f.Db.AffiliatePayoutRequests.IgnoreQueryFilters().ToListAsync());
        await AssertNoMoney(f.Db);
        f.AddCommission(500m); await f.Db.SaveChangesAsync();
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Attempt(false)));
        Assert.Single(await f.Db.AffiliatePayoutRequests.IgnoreQueryFilters().ToListAsync());
        Assert.Equal(300m, (await f.Service.GetAvailableBalanceAsync(f.Account.Id)).RawAvailable);
        await AssertNoMoney(f.Db);
    }
}
