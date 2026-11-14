using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Npgsql;
using PointOfSale.API.Controllers;
using PointOfSale.Core.Entities;
using PointOfSale.Core.Entities.Accounting;
using PointOfSale.Core.Entities.Affiliate;
using PointOfSale.Core.Enums;
using PointOfSale.Core.Events.Affiliate;
using PointOfSale.Core.Interfaces;
using PointOfSale.Core.Interfaces.Accounting;
using PointOfSale.Core.Interfaces.Affiliate;
using PointOfSale.Core.Interfaces.Treasury;
using PointOfSale.Core.Models.Accounting;
using PointOfSale.Infrastructure.Data;
using PointOfSale.Infrastructure.Accounting;
using PointOfSale.Infrastructure.Services.Affiliate;
using PointOfSale.Tests.Infrastructure;
using Xunit.Abstractions;

namespace PointOfSale.Tests.Affiliate;

/// <summary>SIMULATED / NO MONEY. Only TestDatabaseScope connections; no application host,
/// real posting service, Treasury service, bus, HTTP client or provider is constructed.</summary>
public sealed class AffiliatePhase5WithdrawalTests(ITestOutputHelper output)
{
    internal sealed class Fixture : IAsyncDisposable
    {
        internal readonly TestDatabaseScope Scope;
        internal ApplicationDbContext Db { get; }
        internal Guid Store { get; } = Guid.NewGuid();
        internal Guid Admin { get; } = Guid.NewGuid();
        internal AffiliateAccount Account { get; }
        internal string Identity { get; private set; } = "";
        internal AffiliatePayoutService Service => ServiceFor(Db);
        internal static AffiliatePayoutService ServiceFor(ApplicationDbContext db) => new(db, NullLogger<AffiliatePayoutService>.Instance);
        internal Fixture()
        {
            Scope = TestDatabaseScope.Open();
            Db = Scope.CreateDbContext();
            Account = AffiliateAccount.Create(Guid.Empty, Guid.NewGuid(), "P5NOMONEY", "P5 disposable only");
            Account.Id = Guid.NewGuid(); Account.Activate();
            Account.UpdatePayoutWallet("01000000000", "VodafoneCash"); // Fictional fixture, never sent anywhere.
        }
        internal async Task Initialize()
        {
            await Db.Database.OpenConnectionAsync();
            await using var cmd = Db.Database.GetDbConnection().CreateCommand();
            cmd.CommandText = "SELECT current_database(), inet_server_addr()::text, pg_backend_pid()";
            await using (var r = await cmd.ExecuteReaderAsync())
            {
                Assert.True(await r.ReadAsync());
                var name = r.GetString(0);
                Assert.StartsWith("pointofsale_tests_", name);
                Assert.NotEqual("PosEcommerceDb_Baseline", name);
                Identity = $"{r.GetString(1)}/{name}; backend={r.GetInt32(2)}";
            }
            var user = AffiliateUser.Create(Guid.Empty, $"p5-{Account.Id:N}@example.invalid", "not-a-real-password", Account.Id);
            user.Id = Account.UserId; user.VerifyEmail();
            Db.AddRange(new Tenant { Id = Store, Name = "P5 SIMULATED NO MONEY", Slug = $"p5-{Store:N}", Currency = "EGP", IsActive = true, Status = TenantStatus.Active }, Account, user);
            await Db.SaveChangesAsync();
        }
        internal AffiliateCommissionLedger Commission(decimal amount, bool synthetic = false)
        {
            var category = new Category { Id = Guid.NewGuid(), TenantId = Store, Name = "P5 disposable" };
            var product = new Product { Id = Guid.NewGuid(), TenantId = Store, CategoryId = category.Id, Name = "P5 disposable" };
            var affiliateCategory = AffiliateCategory.CreateMainCategory("P5", $"p5-{Guid.NewGuid():N}", 1);
            affiliateCategory.Id = Guid.NewGuid();
            var policy = AffiliateProductPolicy.Create(Store, product.Id, .1m, null, affiliateCategory.Id);
            policy.Id = Guid.NewGuid();
            var order = new Order { Id = Guid.NewGuid(), TenantId = Store, OrderNumber = $"P5_NO_MONEY_{Guid.NewGuid():N}",
                Currency = "EGP", Total = amount * 10, ShippingAddress = "Disposable test fixture, not a delivery",
                AffiliateAccountId = Account.Id, AffiliateReferralCode = Account.ReferralCode,
                Status = OrderStatus.Delivered, FulfillmentStatus = FulfillmentStatus.Delivered,
                PaymentStatus = synthetic ? PaymentStatus.SyntheticQaCollected : PaymentStatus.Collected };
            var item = new OrderItem { Id = Guid.NewGuid(), OrderId = order.Id, ProductId = product.Id,
                Quantity = 1, UnitPrice = order.Total, Total = order.Total, PaidAmount = order.Total,
                AffiliateCommissionEligible = true, AffiliateCommissionPolicyIdSnapshot = policy.Id, AffiliateCommissionRateSnapshot = .1m,
                AffiliateCommissionBaseAmountSnapshot = order.Total, AffiliateCommissionAmountSnapshot = amount,
                AffiliateCommissionSnapshotCapturedAtUtc = DateTime.UtcNow };
            order.Items.Add(item);
            var attribution = AffiliateAttribution.Create(Store, order.Id, Account.Id, Account.ReferralCode, AttributionSource.ReferralLink);
            attribution.Id = Guid.NewGuid();
            var earned = AffiliateCommissionLedger.CreateEarned(Store, attribution.Id, Account.Id, order.Id, item.Id, order.Total, .1m, null, "EGP");
            Db.AddRange(category, product, affiliateCategory, policy, order, attribution, earned, AffiliateCommissionLedger.CreateApproved(earned));
            if (synthetic)
            {
                var customer = new OnlineCustomer { Id = Guid.NewGuid(), TenantId = Store, Email = "p5@example.invalid", CreatedAtUtc = DateTime.UtcNow };
                var link = AffiliatePromotionLink.Create(Store, Account.UserId, product.Id, Guid.NewGuid().ToString("N")[..10], Account.Id);
                link.Id = Guid.NewGuid();
                var run = new SyntheticQaRun { Id = Guid.NewGuid(), TenantId = Store, ProductId = product.Id, CustomerId = customer.Id,
                    PromotionLinkId = link.Id, CreatedAtUtc = DateTime.UtcNow, ExpiresAtUtc = DateTime.UtcNow.AddHours(1) };
                Db.AddRange(customer, link, run, new SyntheticQaOrder { OrderId = order.Id, TenantId = Store, RunId = run.Id, EnrolledAtUtc = DateTime.UtcNow },
                    new SyntheticQaCommissionPosting { Id = Guid.NewGuid(), OrderId = order.Id, Amount = amount, Currency = "EGP", PostedAtUtc = DateTime.UtcNow });
            }
            return earned;
        }
        internal async Task EarnedJournal(AffiliateCommissionLedger earned)
        {
            // Fixture-level prerequisite only, with explicit synthetic provenance; no real posting engine.
            using var authority = LedgerMutationScope.Authorize();
            Db.Add(JournalEntry.Create(Store, $"P5-{Guid.NewGuid():N}", DateTime.UtcNow, "P5_SIMULATED_NO_MONEY", earned.OrderId,
                "fixture-only", "SIMULATED accounting prerequisite, no GL lines or movement", $"AFFILIATE_COMM_EARNED:{Store}:{earned.OrderId}", null));
            await Db.SaveChangesAsync();
        }
        internal AffiliatePayoutRequest Reservation(decimal amount) => AffiliatePayoutRequest.Create(Store, Account.Id, amount, amount,
            "EGP", Account.PayoutWalletNumber!, Account.PayoutWalletType!, Account.PayoutWalletUpdatedAtUtc);
        internal ApplicationDbContext ConcurrentDb() => new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql(Db.Database.GetConnectionString(), p => p.EnableRetryOnFailure()).Options);
        internal async Task NoEffects(int requests = 0, int journals = 0)
        {
            await using var db = Scope.CreateDbContext();
            Assert.Equal(requests, await db.AffiliatePayoutRequests.IgnoreQueryFilters().CountAsync());
            Assert.Empty(await db.AffiliateBalanceLedger.IgnoreQueryFilters().ToListAsync());
            Assert.False(await db.AffiliateCommissionLedger.IgnoreQueryFilters().AnyAsync(e => e.EntryType == CommissionEntryType.Paid));
            Assert.Equal(journals, await db.JournalEntries.IgnoreQueryFilters().CountAsync());
            Assert.Empty(await db.LedgerEntries.IgnoreQueryFilters().ToListAsync());
            Assert.Empty(await db.AccountingOutboxMessages.IgnoreQueryFilters().ToListAsync());
            Assert.Empty(await db.AffiliatePayoutTransferAllocations.IgnoreQueryFilters().ToListAsync());
            Assert.Empty(await db.Set<TreasuryTransaction>().IgnoreQueryFilters().ToListAsync());
            Assert.Empty(await db.Set<CashboxTransaction>().IgnoreQueryFilters().ToListAsync());
            Assert.Empty(await db.LedgerAccounts.IgnoreQueryFilters().ToListAsync());
        }
        public async ValueTask DisposeAsync() { await Db.DisposeAsync(); await Scope.DisposeAsync(); }
    }

    private async Task<Fixture> Create()
    {
        var f = new Fixture(); await f.Initialize();
        output.WriteLine($"DISPOSABLE VERIFIED {f.Identity}; affiliate={f.Account.Id}; store={f.Store}; SIMULATED_NO_MONEY");
        return f;
    }

    /// <summary>Test-assembly-only adapter. Models external confirmation, never sends a payment.
    /// Failure injection at the existing posting boundary also verifies SQL rollback after staged Paid/debit rows.</summary>
    private sealed class Simulator
    {
        private readonly Fixture f;
        internal readonly Mock<IFinancialPostingService> Posting = new(MockBehavior.Strict);
        internal readonly Mock<IEventPublisher> Events = new(MockBehavior.Strict);
        internal int Confirmations;
        internal int ExternalNetworkCalls => 0; // No transport exists in this adapter.
        internal string Outcome = "success";
        internal bool VerifiedEvidence;
        internal Simulator(Fixture fixture)
        {
            f = fixture;
            Posting.Setup(p => p.PostJournalAsync(It.IsAny<PostingRequest>(), It.IsAny<CancellationToken>()))
                .Returns((PostingRequest p, CancellationToken _) =>
                {
                    Assert.Equal(f.Store, p.TenantId);
                    Assert.Equal("AffiliateCommissionPaid", p.SourceType);
                    Assert.Equal(p.Lines.Sum(l => l.DebitAmount), p.Lines.Sum(l => l.CreditAmount));
                    if (Outcome == "transient") throw new TimeoutException("SIMULATED_TRANSIENT_NO_MONEY");
                    if (Outcome == "permanent") throw new InvalidOperationException("SIMULATED_PERMANENT_NO_MONEY");
                    return Task.FromResult(JournalEntry.Create(f.Store, "SIMULATED-NO-MONEY", DateTime.UtcNow,
                        "P5_SIMULATED_NO_MONEY", p.SourceId, null, "Not persisted; no GL movement", p.IdempotencyKey, null));
                });
            Events.Setup(e => e.PublishAsync(It.IsAny<AffiliateCommissionPaidEvent>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask); // Never constructs or starts a bus/outbox delivery service.
        }
        internal async Task Process(Guid requestId, decimal amount, bool realProvider = false)
        {
            if (realProvider) throw new InvalidOperationException("SIMULATOR_REJECTS_REAL_PROVIDER_EXECUTION");
            await using var db = f.Scope.CreateDbContext();
            Assert.Equal(f.Db.Database.GetDbConnection().Database, db.Database.GetDbConnection().Database);
            var reference = $"P5_SIMULATED_NO_MONEY_{requestId:N}";
            // Persist only simulated confirmation evidence in the verified disposable database.
            if (!await db.AffiliatePayoutTransferEvidence.AnyAsync(e => e.PaymentReference == reference))
            {
                db.Add(AffiliatePayoutTransferEvidence.Create(f.Store, "AFFILIATE_PAYOUT", reference, amount, "EGP",
                    DateTime.UtcNow, VerifiedEvidence ? AffiliatePayoutEvidenceService.TrustedVerificationSource : "P5_SIMULATED_NO_MONEY", new string('0', 64)));
                await db.SaveChangesAsync(); Confirmations++;
            }
            var resolver = new Mock<IAccountResolver>(MockBehavior.Strict);
            var payable = new Account { Id = Guid.NewGuid(), TenantId = f.Store };
            var cash = new Account { Id = Guid.NewGuid(), TenantId = f.Store };
            resolver.Setup(r => r.GetByCodesAsync(f.Store, It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new Dictionary<string, Account> { [AccountCodes.AffiliatePayable] = payable });
            resolver.Setup(r => r.GetByCodeAsync(f.Store, AccountCodes.CashboxMain, It.IsAny<CancellationToken>())).ReturnsAsync(cash);
            var cashbox = new Mock<ICashboxLedgerAccountService>(MockBehavior.Strict);
            cashbox.Setup(c => c.ResolveDefaultCashboxAccountAsync(f.Store, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new CashboxLedgerAccountInfo(null, cash.Id, AccountCodes.CashboxMain, "SIMULATED NO MONEY", true, null, null));
            var service = new AffiliateSettlementOrchestrator(db, Fixture.ServiceFor(db), Events.Object, Posting.Object,
                resolver.Object, cashbox.Object, NullLogger<AffiliateSettlementOrchestrator>.Instance,
                Options.Create(new AffiliateSettlementOptions { RequireVerifiedExternalPayoutEvidence = VerifiedEvidence }));
            await service.ExecuteAffiliatePayoutAsync(f.Account.Id, f.Store, amount, SettlementSource.WithdrawalEngine,
                reference, payoutRequestId: requestId, paidByUserId: f.Admin, adminNotes: "P5 SIMULATED NO MONEY");
            if (Outcome == "post-commit-timeout") throw new TimeoutException("SIMULATED_POST_COMMIT_NO_MONEY");
        }
    }

    [Theory]
    [InlineData(-1)] [InlineData(0)] [InlineData(99.99)] [InlineData(100.001)] [InlineData(100.00001)]
    public async Task InvalidAmounts_CreateNoReservation(decimal amount)
    {
        await using var f = await Create(); f.Commission(500); await f.Db.SaveChangesAsync();
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => f.Service.RequestPayoutAsync(f.Account.Id, amount, f.Store));
        await f.NoEffects();
    }

    [Theory]
    [InlineData(100)] [InlineData(150)]
    public async Task MinimumAndAboveMinimum_ReserveExactlyOnce_RejectReleasesOnce(decimal amount)
    {
        await using var f = await Create(); f.Commission(500); f.Commission(900, true); await f.Db.SaveChangesAsync();
        var request = await f.Service.RequestPayoutAsync(f.Account.Id, amount, f.Store);
        output.WriteLine($"request={request.Id}; Pending reservation={amount}");
        Assert.Equal(request.Id, (await f.Service.RequestPayoutAsync(f.Account.Id, amount, f.Store)).Id);
        var b = await f.Service.GetAvailableBalanceAsync(f.Account.Id, f.Store);
        Assert.Equal(500, b.TotalApproved); Assert.Equal(900, b.SyntheticApprovedExcluded);
        Assert.Equal(500 - amount, b.RawAvailable); Assert.Equal(amount, b.TotalPendingRequests);
        await f.Service.ApprovePayoutAsync(request.Id, f.Store, f.Admin);
        Assert.Equal(PayoutRequestStatus.Approved, request.Status);
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Service.ApprovePayoutAsync(request.Id, f.Store, f.Admin));
        await f.Service.RejectPayoutAsync(request.Id, f.Store, f.Admin, "SIMULATED rejection");
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Service.RejectPayoutAsync(request.Id, f.Store, f.Admin));
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Service.ApprovePayoutAsync(request.Id, f.Store, f.Admin));
        Assert.Equal(500, (await f.Service.GetAvailableBalanceAsync(f.Account.Id, f.Store)).RawAvailable);
        await f.NoEffects(1);
    }

    [Theory]
    [InlineData("USD")] [InlineData("ZZZ")] [InlineData("")]
    public async Task ClientCurrency_CannotReplaceServerAuthority(string currency)
    {
        await using var f = await Create(); f.Commission(500); await f.Db.SaveChangesAsync();
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => f.Service.RequestPayoutWithCurrencyAsync(f.Account.Id, 150, f.Store, currency));
        await f.NoEffects();
    }

    [Fact]
    public async Task MixedAndSyntheticOnly_CannotSpendSyntheticOrStaleReservations()
    {
        await using var f = await Create(); f.Commission(900, true); await f.Db.SaveChangesAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Service.RequestPayoutAsync(f.Account.Id, 500, f.Store));
        await f.NoEffects();
        f.Commission(250); await f.Db.SaveChangesAsync();
        var request = await f.Service.RequestPayoutAsync(f.Account.Id, 150, f.Store);
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Service.RequestPayoutAsync(f.Account.Id, 200, f.Store));
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Service.RequestPayoutAsync(f.Account.Id, 251, f.Store));
        await f.Service.RejectPayoutAsync(request.Id, f.Store, f.Admin);
        Assert.Equal(250, (await f.Service.GetAvailableBalanceAsync(f.Account.Id, f.Store)).RawAvailable);
        await f.NoEffects(1);
    }

    [Fact]
    public async Task ParallelCompetingRequestsApprovalsAndRejections_PreserveReservationAuthority()
    {
        await using var f = await Create(); f.Commission(250); await f.Db.SaveChangesAsync();
        async Task<Guid?> Request(decimal amount)
        {
            await using var db = f.ConcurrentDb();
            try { return (await Fixture.ServiceFor(db).RequestPayoutAsync(f.Account.Id, amount, f.Store)).Id; }
            catch (InvalidOperationException e) when (e.Message.Contains("INSUFFICIENT")) { return null; }
        }
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(i => Request(i % 2 == 0 ? 150 : 200)));
        var id = Assert.Single(results.Where(x => x.HasValue).Select(x => x!.Value).Distinct());
        async Task Transition(bool approve)
        {
            await using var db = f.ConcurrentDb();
            try
            {
                if (approve) await Fixture.ServiceFor(db).ApprovePayoutAsync(id, f.Store, f.Admin);
                else await Fixture.ServiceFor(db).RejectPayoutAsync(id, f.Store, f.Admin);
            }
            catch (InvalidOperationException e) when (e.Message.Contains("Only Pending")) { }
        }
        await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => Transition(true)));
        await using var check = f.Scope.CreateDbContext();
        var request = await check.AffiliatePayoutRequests.SingleAsync();
        Assert.Equal(PayoutRequestStatus.Approved, request.Status);
        Assert.Equal(250 - request.RequestedAmount, (await Fixture.ServiceFor(check).GetAvailableBalanceAsync(f.Account.Id, f.Store)).RawAvailable);
        await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => Transition(false)));
        Assert.Equal(250, (await f.Service.GetAvailableBalanceAsync(f.Account.Id, f.Store)).RawAvailable);
        await f.NoEffects(1);
        output.WriteLine($"request={id}; 8 competing requests, 6 approvals, 6 rejections");
    }

    [Fact]
    public async Task Approval_RevalidatesReversalsSyntheticBackingAndStoreScope()
    {
        await using var f = await Create(); var earned = f.Commission(200); f.Commission(900, true); await f.Db.SaveChangesAsync();
        var request = await f.Service.RequestPayoutAsync(f.Account.Id, 200, f.Store);
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Service.ApprovePayoutAsync(request.Id, Guid.NewGuid(), f.Admin));
        f.Db.Add(AffiliateCommissionLedger.CreateReversalWithCustomKey(f.Store, earned.Id, earned.AttributionId,
            f.Account.Id, earned.OrderId, earned.OrderItemId, 1500, 150, .1m, "EGP", $"P5-reversal:{Guid.NewGuid()}"));
        await f.Db.SaveChangesAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Service.ApprovePayoutAsync(request.Id, f.Store, f.Admin));
        await f.Service.RejectPayoutAsync(request.Id, f.Store, f.Admin);
        Assert.Equal(50, (await f.Service.GetAvailableBalanceAsync(f.Account.Id, f.Store)).RawAvailable);
        await f.NoEffects(1);
    }

    [Fact]
    public async Task AuthenticatedController_IgnoresForgedAffiliateAndBalance_StoreCannotConsumeOthers()
    {
        await using var f = await Create(); f.Commission(250); await f.Db.SaveChangesAsync();
        AffiliatePortalController Controller(Guid affiliate) => new(f.Db, null!, null!, f.Service, null!, null!, NullLogger<AffiliatePortalController>.Instance)
        { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(
            new[] { new Claim("affiliate_account_id", affiliate.ToString()) }, "P5-isolated")) } } };
        var body = JsonSerializer.Deserialize<AffiliateRequestPayoutBody>($$"""{"amount":150,"storeTenantId":"{{f.Store}}","affiliateAccountId":"{{Guid.NewGuid()}}","availableForWithdrawal":999999,"totalApproved":999999}""", new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        var ok = Assert.IsType<OkObjectResult>((await Controller(f.Account.Id).RequestPayout(body, default)).Result);
        Assert.Equal(150, Assert.IsType<AffiliatePayoutRequestDto>(ok.Value).RequestedAmount);
        var otherAffiliate = Guid.NewGuid();
        var fail = (await Controller(otherAffiliate).RequestPayout(body, default)).Result;
        Assert.IsNotType<OkObjectResult>(fail);
        body.StoreTenantId = Guid.NewGuid();
        Assert.IsNotType<OkObjectResult>((await Controller(f.Account.Id).RequestPayout(body, default)).Result);
        await f.NoEffects(1);
    }

    [Fact]
    public async Task SimulatedSuccess_MixedSelection_PostCommitTimeoutReplay_DeductsOnlyOnce()
    {
        await using var f = await Create(); f.Commission(900, true);
        var earned = f.Commission(150); await f.EarnedJournal(earned);
        var request = await f.Service.RequestPayoutAsync(f.Account.Id, 150, f.Store);
        await f.Service.ApprovePayoutAsync(request.Id, f.Store, f.Admin);
        var sim = new Simulator(f) { Outcome = "post-commit-timeout" };
        await Assert.ThrowsAsync<InvalidOperationException>(() => sim.Process(request.Id, 150, realProvider: true));
        await Assert.ThrowsAsync<TimeoutException>(() => sim.Process(request.Id, 150));
        sim.Outcome = "success";
        await sim.Process(request.Id, 150); await sim.Process(request.Id, 150);
        await using var db = f.Scope.CreateDbContext();
        Assert.Equal(PayoutRequestStatus.Paid, (await db.AffiliatePayoutRequests.SingleAsync()).Status);
        var paid = await db.AffiliateCommissionLedger.Where(e => e.EntryType == CommissionEntryType.Paid).SingleAsync();
        Assert.Equal(earned.OrderId, paid.OrderId); Assert.Equal(150, paid.CommissionAmount);
        Assert.Equal(-150, (await db.AffiliateBalanceLedger.SingleAsync()).Amount);
        Assert.Single(await db.AffiliatePayoutTransferAllocations.ToListAsync());
        Assert.Equal("P5_SIMULATED_NO_MONEY", (await db.AffiliatePayoutTransferEvidence.SingleAsync()).VerificationSource);
        var b = await Fixture.ServiceFor(db).GetAvailableBalanceAsync(f.Account.Id, f.Store);
        Assert.Equal(0, b.RawAvailable); Assert.Equal(150, b.TotalPaid); Assert.Equal(0, b.TotalPendingRequests);
        Assert.Equal(900, b.SyntheticApprovedExcluded);
        Assert.Single(await db.JournalEntries.IgnoreQueryFilters().ToListAsync()); // fixture prerequisite only, never a Paid journal
        Assert.Empty(await db.LedgerEntries.ToListAsync()); Assert.Empty(await db.AccountingOutboxMessages.ToListAsync());
        Assert.Empty(await db.Set<TreasuryTransaction>().IgnoreQueryFilters().ToListAsync());
        Assert.Empty(await db.Set<CashboxTransaction>().IgnoreQueryFilters().ToListAsync());
        Assert.Empty(await db.LedgerAccounts.IgnoreQueryFilters().ToListAsync());
        Assert.Equal(1, sim.Confirmations); Assert.Equal(0, sim.ExternalNetworkCalls);
        sim.Posting.Verify(p => p.PostJournalAsync(It.IsAny<PostingRequest>(), It.IsAny<CancellationToken>()), Times.Once);
        sim.Events.Verify(e => e.PublishAsync(It.IsAny<AffiliateCommissionPaidEvent>(), It.IsAny<CancellationToken>()), Times.Once);
        output.WriteLine($"request={request.Id}; simulated Pending -> Approved -> Paid; debit=150 once; synthetic=900 excluded; available=0");
    }

    [Theory]
    [InlineData("transient")] [InlineData("permanent")]
    public async Task SimulatedFailure_RollsBackStagedDebitPaidAndAllocation_ThenRetryOrReject(string outcome)
    {
        await using var f = await Create(); var earned = f.Commission(150); await f.EarnedJournal(earned);
        var request = await f.Service.RequestPayoutAsync(f.Account.Id, 150, f.Store);
        await f.Service.ApprovePayoutAsync(request.Id, f.Store, f.Admin);
        var sim = new Simulator(f) { Outcome = outcome };
        var failure = await Assert.ThrowsAnyAsync<Exception>(() => sim.Process(request.Id, 150));
        Assert.Contains(outcome == "transient" ? "SIMULATED_TRANSIENT_NO_MONEY" : "SIMULATED_PERMANENT_NO_MONEY", failure.Message);
        await f.NoEffects(1, 1);
        Assert.Equal(0, (await f.Service.GetAvailableBalanceAsync(f.Account.Id, f.Store)).RawAvailable);
        if (outcome == "transient")
        {
            sim.Outcome = "success"; await sim.Process(request.Id, 150);
            await sim.Process(request.Id, 150);
            await using var check = f.Scope.CreateDbContext();
            Assert.Single(await check.AffiliateBalanceLedger.ToListAsync());
            Assert.Equal(PayoutRequestStatus.Paid, (await check.AffiliatePayoutRequests.SingleAsync()).Status);
        }
        else
        {
            await f.Service.RejectPayoutAsync(request.Id, f.Store, f.Admin, "SIMULATED permanent failure");
            await f.NoEffects(1, 1);
            Assert.Equal(150, (await f.Service.GetAvailableBalanceAsync(f.Account.Id, f.Store)).RawAvailable);
        }
        Assert.Equal(0, sim.ExternalNetworkCalls);
        output.WriteLine($"request={request.Id}; {outcome}; SQL rollback verified; confirmation={sim.Confirmations}");
    }

    [Theory]
    [InlineData("synthetic")] [InlineData("underfunded")] [InlineData("lineage")]
    [InlineData("overreserved")]
    public async Task SimulatedProcessing_RejectsUnbackedRequestsWithoutFinancialEffects(string reason)
    {
        await using var f = await Create(); var earned = f.Commission(reason == "underfunded" ? 100 : 150, reason == "synthetic");
        if (reason == "lineage")
        {
            var other = f.Commission(10);
            var approved = f.Db.ChangeTracker.Entries<AffiliateCommissionLedger>()
                .Single(e => e.Entity.OrderItemId == earned.OrderItemId && e.Entity.EntryType == CommissionEntryType.Approved).Entity;
            typeof(AffiliateCommissionLedger).GetProperty("OriginalEntryId")!.SetValue(approved, other.Id);
        }
        var request = f.Reservation(150); request.Approve(f.Admin); f.Db.Add(request); await f.Db.SaveChangesAsync();
        if (reason == "overreserved") { f.Db.Add(f.Reservation(100)); await f.Db.SaveChangesAsync(); }
        var sim = new Simulator(f);
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => sim.Process(request.Id, 150));
        await f.NoEffects(reason == "overreserved" ? 2 : 1);
        sim.Posting.VerifyNoOtherCalls(); sim.Events.VerifyNoOtherCalls();
        Assert.Equal(0, sim.ExternalNetworkCalls);
    }

    [Fact]
    public async Task VerifiedEvidence_ConsumedOnce_ParallelCommittedReplaysAreIdempotent()
    {
        await using var f = await Create(); var earned = f.Commission(150); await f.EarnedJournal(earned);
        var request = await f.Service.RequestPayoutAsync(f.Account.Id, 150, f.Store);
        await f.Service.ApprovePayoutAsync(request.Id, f.Store, f.Admin);
        var sim = new Simulator(f) { VerifiedEvidence = true };
        await sim.Process(request.Id, 150);
        await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => sim.Process(request.Id, 150)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => sim.Process(request.Id, 151));
        await using var db = f.Scope.CreateDbContext();
        Assert.Single(await db.AffiliateBalanceLedger.ToListAsync());
        Assert.Single(await db.AffiliatePayoutTransferAllocations.ToListAsync());
        var evidence = await db.AffiliatePayoutTransferEvidence.SingleAsync();
        Assert.Equal(150, evidence.AllocatedAmount); Assert.StartsWith("P5_SIMULATED_NO_MONEY_", evidence.PaymentReference);
        sim.Posting.Verify(p => p.PostJournalAsync(It.IsAny<PostingRequest>(), It.IsAny<CancellationToken>()), Times.Once);
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Service.ApprovePayoutAsync(request.Id, f.Store, f.Admin));
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Service.RejectPayoutAsync(request.Id, f.Store, f.Admin));
        Assert.Equal(0, sim.ExternalNetworkCalls);
        output.WriteLine($"request={request.Id}; verified-shape SIMULATED evidence; six concurrent committed retries; one deduction/allocation");
    }
}
