using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using PointOfSale.Core.Entities;
using PointOfSale.Core.Entities.Affiliate;
using PointOfSale.Core.Enums;
using PointOfSale.Infrastructure.Data;
using PointOfSale.Infrastructure.MultiTenancy;
using PointOfSale.Infrastructure.Services.Affiliate;
using PointOfSale.Tests.Infrastructure;
using System.Security;
using System.Text.Json;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using PointOfSale.API.Controllers;
using Microsoft.Extensions.Options;
using Moq;
using PointOfSale.Core.Interfaces;
using PointOfSale.Core.Interfaces.Affiliate;
using PointOfSale.Core.Interfaces.Accounting;
using PointOfSale.Core.Interfaces.Treasury;
using Xunit.Abstractions;

namespace PointOfSale.Tests.Affiliate;

/// <summary>Opt-in PRIMARY PostgreSQL only. All new fixtures are retained and labelled.
/// No database harness, test host, provider transport, bus or external collection.</summary>
[Trait("Category", "PrimaryDevelopmentPostgres")]
public sealed class AffiliateWithdrawalCancellationTests(ITestOutputHelper output)
{
    private async Task VerifyPostgresLockWait(Fixture f, string applicationName)
    {
        var deadline = DateTime.UtcNow.AddSeconds(8);
        while (DateTime.UtcNow < deadline)
        {
            await using var command = f.Db.Database.GetDbConnection().CreateCommand();
            command.CommandText = "SELECT pid, wait_event FROM pg_stat_activity WHERE application_name=@application AND wait_event_type='Lock'";
            var parameter = command.CreateParameter(); parameter.ParameterName = "application"; parameter.Value = applicationName;
            command.Parameters.Add(parameter);
            await using var reader = await command.ExecuteReaderAsync();
            if (await reader.ReadAsync())
            {
                output.WriteLine($"PRIMARY PostgreSQL contention observed: database=PosEcommerceDb_Baseline; backend={reader.GetInt32(0)}; wait_event={reader.GetString(1)}; application={applicationName}");
                return;
            }
            await Task.Delay(25);
        }
        Assert.Fail("Competitor did not demonstrably wait on a PostgreSQL lock before the winner was released.");
    }

    private static void Tag(ApplicationDbContext db, string applicationName) => db.Database.SetConnectionString(
        new Npgsql.NpgsqlConnectionStringBuilder(db.Database.GetConnectionString()) { ApplicationName = applicationName }.ConnectionString);

    internal sealed class Fixture : IAsyncDisposable
    {
        internal Guid Store = Guid.NewGuid();
        internal AffiliateAccount Account = null!;
        internal Guid Actor => Account.UserId;
        internal ApplicationDbContext Db = null!;
        internal string Connection = "";
        internal AffiliatePayoutService Service => new(Db, NullLogger<AffiliatePayoutService>.Instance);
        internal ApplicationDbContext Open(params IInterceptor[] interceptors)
        {
            var scope = new TenantContext(); scope.SetTenantId(Store);
            return new(new DbContextOptionsBuilder<ApplicationDbContext>().UseNpgsql(Connection)
                .AddInterceptors(interceptors).Options, scope);
        }
        internal static async Task<Fixture> Create(bool browser = false)
        {
            var config = new ConfigurationBuilder().SetBasePath(Path.Combine(TestConfiguration.GetSolutionRoot(), "PointOfSale.API"))
                .AddJsonFile("appsettings.Development.local.json").Build();
            var cs = new Npgsql.NpgsqlConnectionStringBuilder(config.GetConnectionString("DefaultConnection"));
            Assert.Equal("192.168.1.5", cs.Host); Assert.Equal("PosEcommerceDb_Baseline", cs.Database);
            var f = new Fixture { Connection = cs.ConnectionString };
            if (browser)
            {
                await using var lookup = f.Open();
                f.Account = await lookup.AffiliateAccounts.IgnoreQueryFilters().SingleAsync(a => a.Id == Guid.Parse("01a03e11-e234-7d40-b77e-99f9463c7a40"));
                f.Store = await lookup.Tenants.IgnoreQueryFilters().Where(t => t.Id == Guid.Parse("c9c5c1ae-949e-498e-bc63-07242daeb49d"))
                    .Select(t => t.Id).SingleAsync();
            }
            f.Db = f.Open();
            await f.Db.Database.OpenConnectionAsync();
            await using (var cmd = f.Db.Database.GetDbConnection().CreateCommand())
            {
                cmd.CommandText = "select current_database(), host(inet_server_addr())";
                await using var r = await cmd.ExecuteReaderAsync(); Assert.True(await r.ReadAsync());
                Assert.Equal("PosEcommerceDb_Baseline", r.GetString(0)); Assert.Equal("192.168.1.5", r.GetString(1));
            }
            if (!browser)
            {
                f.Account = AffiliateAccount.Create(Guid.Empty, Guid.NewGuid(), $"B{Guid.NewGuid():N}"[..12], "P53B SIMULATED NO MONEY");
                f.Account.Id = Guid.NewGuid(); f.Account.Activate(); f.Account.UpdatePayoutWallet("01000000000", "VodafoneCash");
                var user = AffiliateUser.Create(Guid.Empty, $"p53b-{f.Account.Id:N}@example.invalid", "NO_LOGIN_TEST_ONLY", f.Account.Id);
                user.Id = f.Actor; user.VerifyEmail();
                f.Db.AddRange(new Tenant { Id = f.Store, Name = "P53B SIMULATED NO MONEY", Slug = $"p53b-{f.Store:N}", Currency = "EGP", IsActive = true, Status = TenantStatus.Active }, f.Account, user);
            }
            if (!browser || !await f.Db.Orders.IgnoreQueryFilters().AnyAsync(o => o.TenantId == f.Store &&
                o.AffiliateAccountId == f.Account.Id && o.OrderNumber.StartsWith("P53B_NO_MONEY_")))
                await f.SeedCommission(600m);
            return f;
        }
        internal async Task SeedCommission(decimal amount)
        {
            var category = new Category { Id = Guid.NewGuid(), TenantId = Store, Name = "P53B NO STOCK NO MONEY" };
            var product = new Product { Id = Guid.NewGuid(), TenantId = Store, CategoryId = category.Id, Name = "P53B cancellation fixture — never ship" };
            var ac = AffiliateCategory.CreateMainCategory("P53B cancellation fixture", $"p53b-{Guid.NewGuid():N}", 1); ac.Id = Guid.NewGuid();
            var policy = AffiliateProductPolicy.Create(Store, product.Id, .1m, null, ac.Id); policy.Id = Guid.NewGuid();
            var order = new Order { Id = Guid.NewGuid(), TenantId = Store, OrderNumber = $"P53B_NO_MONEY_{Guid.NewGuid():N}",
                CustomerNotes = "P53B SIMULATED fixture only: no customer collection, physical delivery or provider execution",
                Currency = "EGP", Total = amount * 10, ShippingAddress = "Test-only fixture, do not ship",
                AffiliateAccountId = Account.Id, AffiliateReferralCode = Account.ReferralCode,
                Status = OrderStatus.Delivered, FulfillmentStatus = FulfillmentStatus.Delivered, PaymentStatus = PaymentStatus.Collected };
            var item = new OrderItem { Id = Guid.NewGuid(), OrderId = order.Id, ProductId = product.Id, Quantity = 1,
                UnitPrice = order.Total, Total = order.Total, PaidAmount = order.Total, AffiliateCommissionEligible = true,
                AffiliateCommissionPolicyIdSnapshot = policy.Id, AffiliateCommissionRateSnapshot = .1m,
                AffiliateCommissionBaseAmountSnapshot = order.Total, AffiliateCommissionAmountSnapshot = amount,
                AffiliateCommissionSnapshotCapturedAtUtc = DateTime.UtcNow };
            order.Items.Add(item);
            var attribution = AffiliateAttribution.Create(Store, order.Id, Account.Id, Account.ReferralCode, AttributionSource.ReferralLink); attribution.Id = Guid.NewGuid();
            var earned = AffiliateCommissionLedger.CreateEarned(Store, attribution.Id, Account.Id, order.Id, item.Id, order.Total, .1m, null, "EGP");
            Db.AddRange(category, product, ac, policy, order, attribution, earned, AffiliateCommissionLedger.CreateApproved(earned));
            await Db.SaveChangesAsync();
        }
        internal Task<AffiliatePayoutRequest> Request(decimal amount = 150) => Service.RequestPayoutAsync(Account.Id, amount, Store);
        internal Task<AffiliatePayoutRequest> Cancel(Guid id) => Service.CancelPayoutAsync(id, Account.Id, Actor, "P53B SIMULATED cancellation", Store);
        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }

    [PrimaryAffiliateFact]
    public async Task Pending_CancelsDurably_ExactlyOnce_WithAuditAndNoMonetaryCredit()
    {
        await using var f = await Fixture.Create();
        var initial = await f.Service.GetAvailableBalanceAsync(f.Account.Id, f.Store);
        var request = await f.Request();
        var before = await f.Service.GetAvailableBalanceAsync(f.Account.Id, f.Store);
        Assert.Equal(600m, initial.RawAvailable); Assert.Equal(450m, before.RawAvailable); Assert.Equal(150m, before.TotalPendingRequests);
        await f.Cancel(request.Id);
        var duplicate = await Assert.ThrowsAsync<InvalidOperationException>(() => f.Cancel(request.Id));
        Assert.Equal("AFFILIATE_PAYOUT_ALREADY_CANCELLED", duplicate.Message);
        await using var fresh = f.Open();
        Assert.Equal(PayoutRequestStatus.Cancelled, (await fresh.AffiliatePayoutRequests.SingleAsync(r => r.Id == request.Id)).Status);
        var after = await new AffiliatePayoutService(fresh, NullLogger<AffiliatePayoutService>.Instance).GetAvailableBalanceAsync(f.Account.Id, f.Store);
        Assert.Equal(600m, after.RawAvailable); Assert.Equal(0m, after.TotalPendingRequests);
        Assert.False(await fresh.AffiliateBalanceLedger.AnyAsync(l => l.AffiliateAccountId == f.Account.Id));
        Assert.False(await fresh.AffiliatePayoutTransferAllocations.AnyAsync(l => l.AffiliateAccountId == f.Account.Id));
        var audit = await fresh.AuditLogs.SingleAsync(a => a.EntityId == request.Id.ToString() && a.ActionType == "AffiliateWithdrawalCancelled");
        Assert.Equal(f.Actor, audit.UserId); Assert.Equal(f.Store, audit.TenantId);
        Assert.Contains("Pending", audit.BeforeDataJson); Assert.Contains("Cancelled", audit.AfterDataJson);
        Assert.Contains("P53B SIMULATED cancellation", audit.AfterDataJson); Assert.True(audit.SuccessFlag);
        audit.Description = "rewrite";
        await Assert.ThrowsAsync<InvalidOperationException>(() => fresh.SaveChangesAsync());
    }

    [PrimaryAffiliateFact]
    public async Task Approved_Paid_Rejected_AndPartiallyReconciled_AreNotCancellable()
    {
        foreach (var state in new[] { "Approved", "Paid", "Rejected", "Partial" })
        {
            await using var f = await Fixture.Create(); var r = await f.Request();
            if (state is "Approved" or "Paid") r.Approve(Guid.NewGuid(), "P53B simulated fixture");
            if (state == "Paid") r.MarkPaid(Guid.NewGuid(), "P53B_SIMULATED_NO_TRANSFER");
            if (state == "Rejected") r.Reject(Guid.NewGuid(), "P53B simulated fixture");
            if (state == "Partial") r.Reconcile(25m, "P53B_SIMULATED_PARTIAL");
            await f.Db.SaveChangesAsync();
            Assert.Empty(await f.Service.GetCancellablePayoutIdsAsync(f.Account.Id, new[] { r }));
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => f.Cancel(r.Id));
            Assert.Equal("AFFILIATE_PAYOUT_CANCELLATION_UNSAFE", error.Message);
            Assert.False(await f.Db.AuditLogs.AnyAsync(a => a.EntityId == r.Id.ToString()));
        }
    }

    [PrimaryAffiliateFact]
    public async Task Ownership_ActorBinding_AndTrustedStore_AreEnforced()
    {
        await using var f = await Fixture.Create(); var r = await f.Request();
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Service.CancelPayoutAsync(r.Id, Guid.NewGuid(), f.Actor, null));
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Service.CancelPayoutAsync(r.Id, f.Account.Id, f.Actor, null, Guid.NewGuid()));
        await Assert.ThrowsAsync<SecurityException>(() => f.Service.CancelPayoutAsync(r.Id, f.Account.Id, Guid.NewGuid(), null, f.Store));
        await using var fresh = f.Open(); Assert.Equal(PayoutRequestStatus.Pending, (await fresh.AffiliatePayoutRequests.SingleAsync(x => x.Id == r.Id)).Status);
    }

    [PrimaryAffiliateFact]
    public async Task ConcurrentCancellations_ProduceOneReleaseAndOneAudit()
    {
        await using var f = await Fixture.Create(); var r = await f.Request();
        async Task<string> Cancel()
        {
            await using var db = f.Open();
            try { await new AffiliatePayoutService(db, NullLogger<AffiliatePayoutService>.Instance).CancelPayoutAsync(r.Id, f.Account.Id, f.Actor, null, f.Store); return "cancelled"; }
            catch (InvalidOperationException ex) { return ex.Message; }
        }
        var outcomes = await Task.WhenAll(Cancel(), Cancel());
        Assert.Single(outcomes, x => x == "cancelled"); Assert.Single(outcomes, x => x == "AFFILIATE_PAYOUT_ALREADY_CANCELLED");
        Assert.Equal(1, await f.Db.AuditLogs.CountAsync(a => a.EntityId == r.Id.ToString()));
        Assert.Equal(600m, (await f.Service.GetAvailableBalanceAsync(f.Account.Id, f.Store)).RawAvailable);
    }

    [PrimaryAffiliateFact]
    public async Task CancellationVersusApproval_OnlyOneTransitionCommits()
    {
        await using var f = await Fixture.Create(); var r = await f.Request();
        async Task<bool> Run(bool cancel)
        {
            await using var db = f.Open(); var service = new AffiliatePayoutService(db, NullLogger<AffiliatePayoutService>.Instance);
            try
            {
                if (cancel) await service.CancelPayoutAsync(r.Id, f.Account.Id, f.Actor, null, f.Store);
                else await service.ApprovePayoutAsync(r.Id, f.Store, Guid.NewGuid());
                return true;
            }
            catch (InvalidOperationException) { return false; }
        }
        var outcomes = await Task.WhenAll(Run(true), Run(false)); Assert.Single(outcomes, x => x);
        await using var fresh = f.Open(); var result = await fresh.AffiliatePayoutRequests.SingleAsync(x => x.Id == r.Id);
        Assert.Contains(result.Status, new[] { PayoutRequestStatus.Cancelled, PayoutRequestStatus.Approved });
        Assert.Equal(result.Status == PayoutRequestStatus.Cancelled ? 1 : 0, await fresh.AuditLogs.CountAsync(a => a.EntityId == r.Id.ToString()));
    }

    private sealed class FailAfterSave : SaveChangesInterceptor
    {
        public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData data, int result, CancellationToken ct = default) =>
            throw new InvalidOperationException("P53B_INJECTED_AFTER_SQL_SAVE");
    }

    [PrimaryAffiliateFact]
    public async Task PersistenceFailure_RollsBackBothStateAndAudit_AfterSqlSave()
    {
        await using var f = await Fixture.Create(); var r = await f.Request();
        await using (var failing = f.Open(new FailAfterSave()))
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => new AffiliatePayoutService(failing, NullLogger<AffiliatePayoutService>.Instance)
                .CancelPayoutAsync(r.Id, f.Account.Id, f.Actor, null, f.Store));
            Assert.Equal("P53B_INJECTED_AFTER_SQL_SAVE", error.Message);
        }
        await using var fresh = f.Open(); Assert.Equal(PayoutRequestStatus.Pending, (await fresh.AffiliatePayoutRequests.SingleAsync(x => x.Id == r.Id)).Status);
        Assert.False(await fresh.AuditLogs.AnyAsync(a => a.EntityId == r.Id.ToString()));
        Assert.Equal(450m, (await f.Service.GetAvailableBalanceAsync(f.Account.Id, f.Store)).RawAvailable);
        await f.Cancel(r.Id);
    }

    [PrimaryAffiliateFact]
    public async Task ExistingDisbursementReference_BlocksEvenAnApparentlyPendingRequest()
    {
        await using var f = await Fixture.Create(); var r = await f.Request();
        f.Db.Add(AffiliateBalanceLedger.CreateWithdrawal(f.Store, f.Account.Id, r.Id, 150m, "EGP", "P53B SIMULATED debit fixture — no transfer"));
        await f.Db.SaveChangesAsync();
        Assert.Empty(await f.Service.GetCancellablePayoutIdsAsync(f.Account.Id, new[] { r }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Cancel(r.Id));
    }

    [PrimaryAffiliateFact]
    public async Task Controller_UsesAuthenticatedActor_AndDuplicateSubmissionsReturn409()
    {
        await using var f = await Fixture.Create(); var r = await f.Request();
        var tenant = new TenantContext(); tenant.SetTenantId(f.Store);
        var controller = new AffiliatePortalController(f.Db, null!, null!, f.Service, tenant, null!, NullLogger<AffiliatePortalController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(new[] {
                new Claim("affiliate_account_id", f.Account.Id.ToString()), new Claim(ClaimTypes.NameIdentifier, f.Actor.ToString()) }, "test-only-controller")) } }
        };
        Assert.IsType<OkObjectResult>(await controller.CancelPayout(r.Id, new() { Reason = "P53B HTTP boundary test" }, default));
        var duplicate = Assert.IsType<ObjectResult>(await controller.CancelPayout(r.Id, new(), default));
        Assert.Equal(409, duplicate.StatusCode); Assert.Contains("AFFILIATE_PAYOUT_ALREADY_CANCELLED", JsonSerializer.Serialize(duplicate.Value));
        controller.HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity(new[] {
            new Claim("affiliate_account_id", f.Account.Id.ToString()), new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()) }, "invalid-actor"));
        Assert.Equal(403, Assert.IsType<ObjectResult>(await controller.CancelPayout(r.Id, new(), default)).StatusCode);
    }

    private sealed class PauseSave : SaveChangesInterceptor
    {
        internal TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData data,
            InterceptionResult<int> result, CancellationToken ct = default)
        {
            Entered.TrySetResult(); await Release.Task.WaitAsync(TimeSpan.FromSeconds(15), ct); return result;
        }
    }

    [PrimaryAffiliateTheory]
    [InlineData(true, 150)] [InlineData(false, 150)] [InlineData(false, 25)]
    public async Task CancellationVersusCanonicalReconciliation_SerializesAndNeverReleasesDisbursedFunds(bool cancellationFirst, decimal amount)
    {
        await using var f = await Fixture.Create(); var request = await f.Request();
        var pause = new PauseSave();
        var competitor = $"P53B_RACE_{Guid.NewGuid():N}";
        async Task<bool> Cancel(bool held)
        {
            await using var db = held ? f.Open(pause) : f.Open();
            if (!held) Tag(db, competitor);
            try { await new AffiliatePayoutService(db, NullLogger<AffiliatePayoutService>.Instance)
                .CancelPayoutAsync(request.Id, f.Account.Id, f.Actor, null, f.Store); return true; }
            catch (InvalidOperationException ex) when (ex.Message == "AFFILIATE_PAYOUT_CANCELLATION_UNSAFE") { return false; }
        }
        async Task<bool> Reconcile(bool held)
        {
            // Same atomic debit + canonical FIFO request reconciliation as legacy
            // settlement; controlled development disbursement, no external transfer.
            // Retry only fully rolled-back PostgreSQL transactions, with fresh contexts.
            return await AffiliateFinancialRetry.ExecuteAsync(async _ =>
            {
                await using var db = held ? f.Open(pause) : f.Open();
                if (!held) Tag(db, competitor);
                await using var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);
                db.Add(AffiliateBalanceLedger.CreateWithdrawal(f.Store, f.Account.Id, Guid.NewGuid(), amount, "EGP", "P53B SIMULATED legacy reconciliation — no transfer"));
                await new AffiliatePayoutService(db, NullLogger<AffiliatePayoutService>.Instance)
                    .AutoReconcileWithdrawalRequestsAsync(f.Account.Id, f.Store, amount, "P53B_SIMULATED_NO_TRANSFER");
                // A cancelled request may leave no active FIFO rows; still save the
                // independently authorized simulated legacy debit exactly once.
                await db.SaveChangesAsync(); await tx.CommitAsync(); return true;
            });
        }
        var first = cancellationFirst ? Cancel(true) : Reconcile(true);
        await pause.Entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
        var second = cancellationFirst ? Reconcile(false) : Cancel(false);
        Assert.False(second.IsCompleted);
        await VerifyPostgresLockWait(f, competitor);
        pause.Release.TrySetResult();
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(30));
        await using var fresh = f.Open(); var r = await fresh.AffiliatePayoutRequests.SingleAsync(x => x.Id == request.Id);
        var balance = await new AffiliatePayoutService(fresh, NullLogger<AffiliatePayoutService>.Instance).GetAvailableBalanceAsync(f.Account.Id, f.Store);
        if (cancellationFirst)
        {
            Assert.True(await first); Assert.Equal(PayoutRequestStatus.Cancelled, r.Status);
            Assert.Equal(600m - amount, balance.RawAvailable); Assert.Equal(0m, balance.TotalPendingRequests);
        }
        else
        {
            Assert.False(await second);
            Assert.Equal(amount == 150m ? PayoutRequestStatus.Paid : PayoutRequestStatus.Pending, r.Status);
            Assert.Equal(150m - amount, r.RemainingAmount);
            Assert.Equal(amount == 150m ? 450m : 425m, balance.RawAvailable);
        }
        Assert.Equal(1, await fresh.AffiliateBalanceLedger.CountAsync(l => l.AffiliateAccountId == f.Account.Id));
        Assert.Equal(cancellationFirst ? 1 : 0, await fresh.AuditLogs.CountAsync(a => a.EntityId == r.Id.ToString()));
    }

    [PrimaryAffiliateFact]
    public async Task ActualPayoutExecution_RacingCancellation_CannotConsumeCancelledRequest_AndEvidenceRemainsRequired()
    {
        await using var f = await Fixture.Create(); var request = await f.Request();
        var pause = new PauseSave();
        await using var cancelling = f.Open(pause);
        var cancel = new AffiliatePayoutService(cancelling, NullLogger<AffiliatePayoutService>.Instance)
            .CancelPayoutAsync(request.Id, f.Account.Id, f.Actor, null, f.Store);
        await pause.Entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
        await using var paying = f.Open();
        var competitor = $"P53B_PAYOUT_RACE_{Guid.NewGuid():N}";
        Tag(paying, competitor);
        var events = new Mock<IEventPublisher>(MockBehavior.Strict);
        var posting = new Mock<IFinancialPostingService>(MockBehavior.Strict);
        AffiliateSettlementOrchestrator Orchestrator(ApplicationDbContext db) => new(db,
            new AffiliatePayoutService(db, NullLogger<AffiliatePayoutService>.Instance), events.Object, posting.Object,
            Mock.Of<IAccountResolver>(MockBehavior.Strict), Mock.Of<ICashboxLedgerAccountService>(MockBehavior.Strict),
            NullLogger<AffiliateSettlementOrchestrator>.Instance, Options.Create(new AffiliateSettlementOptions()));
        var payout = Orchestrator(paying).ExecuteAffiliatePayoutAsync(f.Account.Id, f.Store, 150m,
            SettlementSource.WithdrawalEngine, "P53B_NO_EXTERNAL_TRANSFER", payoutRequestId: request.Id, paidByUserId: Guid.NewGuid());
        Assert.False(payout.IsCompleted);
        await VerifyPostgresLockWait(f, competitor);
        pause.Release.TrySetResult(); await cancel;
        var invalid = await Assert.ThrowsAsync<InvalidOperationException>(() => payout);
        Assert.Contains("AFFILIATE_PAYOUT_REQUEST_STATUS_INVALID", invalid.Message);
        await using var fresh = f.Open();
        Assert.Equal(PayoutRequestStatus.Cancelled, (await fresh.AffiliatePayoutRequests.SingleAsync(r => r.Id == request.Id)).Status);
        Assert.False(await fresh.AffiliateBalanceLedger.AnyAsync(l => l.AffiliateAccountId == f.Account.Id));
        // Approval alone never bypasses verified external evidence in ordinary payout.
        var approved = await f.Request(160m); await f.Service.ApprovePayoutAsync(approved.Id, f.Store, Guid.NewGuid());
        await using var ordinary = f.Open();
        var evidenceError = await Assert.ThrowsAsync<InvalidOperationException>(() => Orchestrator(ordinary)
            .ExecuteAffiliatePayoutAsync(f.Account.Id, f.Store, 160m, SettlementSource.WithdrawalEngine,
                "P53B_NO_EXTERNAL_TRANSFER", payoutRequestId: approved.Id, paidByUserId: Guid.NewGuid()));
        Assert.Contains("AFFILIATE_PAYOUT_VERIFIED_EXTERNAL_EVIDENCE_REQUIRED", evidenceError.Message);
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Cancel(approved.Id));
        posting.VerifyNoOtherCalls(); events.VerifyNoOtherCalls();
    }

    [PrimaryAffiliateFact]
    public async Task AllocatedPayoutEvidence_PreventsCancellation_WithoutDeletingAllocation()
    {
        await using var f = await Fixture.Create(); var r = await f.Request();
        var evidence = AffiliatePayoutTransferEvidence.Create(f.Store, "AFFILIATE_PAYOUT", $"P53B_SIMULATED_{r.Id:N}",
            150m, "EGP", DateTime.UtcNow, "P53B_SIMULATED_NO_MONEY", new string('0', 64));
        var allocation = AffiliatePayoutTransferAllocation.Create(evidence.Id, f.Store, f.Account.Id, r.Id,
            SettlementSource.WithdrawalEngine, 150m, "EGP");
        f.Db.AddRange(evidence, allocation); await f.Db.SaveChangesAsync();
        Assert.Empty(await f.Service.GetCancellablePayoutIdsAsync(f.Account.Id, new[] { r }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Cancel(r.Id));
        await using var fresh = f.Open(); Assert.True(await fresh.AffiliatePayoutTransferAllocations.IgnoreQueryFilters().AnyAsync(a => a.Id == allocation.Id));
    }

    [PrimaryAffiliateFact]
    public async Task PersistenceGuard_RequiresAudit_AndCancelledRowsCannotBeResurrected()
    {
        await using var f = await Fixture.Create(); var r = await f.Request();
        r.Cancel();
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Db.SaveChangesAsync());
        f.Db.ChangeTracker.Clear(); await f.Cancel(r.Id);
        var cancelled = await f.Db.AffiliatePayoutRequests.SingleAsync(x => x.Id == r.Id);
        f.Db.Entry(cancelled).Property(x => x.Status).CurrentValue = PayoutRequestStatus.Pending;
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Db.SaveChangesAsync());
        await using var fresh = f.Open(); Assert.Equal(PayoutRequestStatus.Cancelled, (await fresh.AffiliatePayoutRequests.SingleAsync(x => x.Id == r.Id)).Status);
    }

    [PrimaryAffiliateFact]
    public async Task AmountOnlyRequest_ResolvesRealCommissionStore_NotGlobalAccountTenant()
    {
        await using var f = await Fixture.Create();
        Assert.Equal(Guid.Empty, f.Account.TenantId);
        var request = await f.Service.RequestPayoutAsync(f.Account.Id, 120m);
        Assert.Equal(f.Store, request.StoreTenantId);
        await f.Cancel(request.Id);
    }

    [PrimaryAffiliateFact]
    public async Task SeedBrowserTaskOwnedRequests()
    {
        // Dedicated opt-in in addition to the primary suite opt-in: run exactly once.
        if (Environment.GetEnvironmentVariable("AFFILIATE_P53B_BROWSER_SEED") != "1") return;
        var directory = Path.Combine(TestConfiguration.GetSolutionRoot(), ".ai/evidence/affiliate-p53b-20261023");
        Assert.False(File.Exists(Path.Combine(directory, "browser-seed.json")), "Browser fixtures already retained; do not reseed.");
        await using var f = await Fixture.Create(browser: true);
        // Controlled historical-state fixture, not a successful normal wallet/request
        // command: the supplied account has no wallet, which cancellation must tolerate.
        // Do not alter its profile or weaken the normal wallet guard for the smoke test.
        var before = await f.Service.GetAvailableBalanceAsync(f.Account.Id, f.Store);
        var pending = AffiliatePayoutRequest.Create(f.Store, f.Account.Id, 140m, before.RawAvailable,
            "EGP", "01000000000", "VodafoneCash", null);
        var approved = AffiliatePayoutRequest.Create(f.Store, f.Account.Id, 110m, before.RawAvailable - 140m,
            "EGP", "01000000000", "VodafoneCash", null);
        approved.Approve(Guid.NewGuid(), "P53B SIMULATED STATE FIXTURE — no approval command or transfer performed");
        f.Db.AddRange(pending, approved); await f.Db.SaveChangesAsync();
        var balance = await f.Service.GetAvailableBalanceAsync(f.Account.Id, f.Store);
        await File.WriteAllTextAsync(Path.Combine(directory, "browser-seed.json"), JsonSerializer.Serialize(new { f.Store, AffiliateId = f.Account.Id,
            ActorId = f.Actor, PendingId = pending.Id, ApprovedId = approved.Id, Before = before, Balance = balance,
            FixtureOnly = "No wallet/profile mutation; factory-seeded operational state only, no external transfer or live approval command" }, new JsonSerializerOptions { WriteIndented = true }));
    }
}

public sealed class PrimaryAffiliateTheoryAttribute : TheoryAttribute
{
    public PrimaryAffiliateTheoryAttribute()
    {
        if (Environment.GetEnvironmentVariable("AFFILIATE_PRIMARY_DEVELOPMENT_TESTS") != "1")
            Skip = "Explicit primary-development database opt-in required";
    }
}
