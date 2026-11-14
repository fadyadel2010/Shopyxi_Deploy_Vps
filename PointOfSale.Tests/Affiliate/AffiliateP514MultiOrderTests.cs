using System.Data;
using System.Data.Common;
using System.Text.Json;
using MassTransit;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Npgsql;
using PointOfSale.API.Controllers;
using PointOfSale.Core.Entities;
using PointOfSale.Core.Entities.Affiliate;
using PointOfSale.Core.Enums;
using PointOfSale.Core.Events.Affiliate;
using PointOfSale.Core.Interfaces;
using PointOfSale.Core.Interfaces.Accounting;
using PointOfSale.Core.Interfaces.Affiliate;
using PointOfSale.Core.Interfaces.Treasury;
using PointOfSale.Infrastructure.Accounting;
using PointOfSale.Infrastructure.Consumers.Affiliate;
using PointOfSale.Infrastructure.Data;
using PointOfSale.Infrastructure.Fulfillment;
using PointOfSale.Infrastructure.Instrumentation;
using PointOfSale.Infrastructure.MultiTenancy;
using PointOfSale.Infrastructure.Services.Affiliate;
using PointOfSale.Infrastructure.Treasury;
using PointOfSale.Tests.Infrastructure;
using Fixture = PointOfSale.Tests.Affiliate.AffiliateP512ManualPayoutTests.Fixture;

namespace PointOfSale.Tests.Affiliate;

/// <summary>Required guarded PostgreSQL18 fixture, fictional local ledger data only. No primary DB, broker/provider/carrier/notification calls.</summary>
public sealed partial class AffiliateP514MultiOrderTests
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private static async Task<Fixture> Create(params decimal[] amounts)
    {
        var f = await Fixture.CreateGuardedP514Infrastructure();
        try
        {
        // Verify actual write connection, not just the connection-string configuration.
        await f.Db.Database.OpenConnectionAsync();
        await using (var cmd = f.Db.Database.GetDbConnection().CreateCommand())
        {
            cmd.CommandText = "SELECT current_database(),host(inet_server_addr()),current_setting('transaction_read_only'),current_user";
            await using var r = await cmd.ExecuteReaderAsync(); Assert.True(await r.ReadAsync());
            Assert.Equal("app17c_fixture", r.GetString(0)); Assert.False(string.IsNullOrWhiteSpace(r.GetString(1)));
            Assert.Equal("off", r.GetString(2)); Assert.Equal("app17c_fixture", r.GetString(3));
        }
        var tenant = new TenantContext(); tenant.SetTenantId(f.Store);
        // An isolated in-process event sink: application services and accounting consumer are real.
        var events = new Mock<IEventPublisher>(); events.SetReturnsDefault(Task.CompletedTask);
        var earnedEvents = new List<AffiliateCommissionEarnedEvent>();
        events.Setup(e => e.PublishAsync(It.IsAny<AffiliateCommissionEarnedEvent>(), It.IsAny<CancellationToken>()))
            .Callback<AffiliateCommissionEarnedEvent, CancellationToken>((e, _) => earnedEvents.Add(e)).Returns(Task.CompletedTask);
        var affiliate = new AffiliateService(f.Db, tenant, events.Object, new AffiliateTelemetry(), NullLogger<AffiliateService>.Instance,
            new AffiliatePayoutService(f.Db, NullLogger<AffiliatePayoutService>.Instance), Mock.Of<IAffiliateSettlementOrchestrator>(),
            Options.Create(new AffiliateSettlementOptions()));
        foreach (var amount in amounts)
        {
            var category = new Category { Id = Guid.NewGuid(), TenantId = f.Store, Name = f.Label };
            var product = new Product { Id = Guid.NewGuid(), TenantId = f.Store, CategoryId = category.Id, Name = f.Label };
            var pc = AffiliateCategory.CreateMainCategory(f.Label, $"p514-{Guid.NewGuid():N}", 1); pc.Id = Guid.NewGuid();
            var policy = AffiliateProductPolicy.Create(f.Store, product.Id, .1m, null, pc.Id); policy.Id = Guid.NewGuid();
            // Captured-payment operational boundary, not fabricated financial history or an eligibility override.
            // Delivery/completion and Earned/Approved records below are produced by canonical services.
            var order = new Order { Id = Guid.NewGuid(), TenantId = f.Store, OrderNumber = $"P514-{Guid.NewGuid():N}",
                CustomerNotes = f.Label, ShippingAddress = "INTERNAL TEST ONLY - DO NOT SHIP", Currency = "EGP", Total = amount * 10,
                AffiliateAccountId = f.Affiliate.Id, AffiliateReferralCode = f.Affiliate.ReferralCode,
                Status = OrderStatus.Shipped, FulfillmentStatus = FulfillmentStatus.OutForDelivery, FulfillmentMode = FulfillmentMode.Manual,
                PaymentStatus = PaymentStatus.Collected, FinancialPostingStatus = FinancialPostingStatus.NotRequired };
            order.Items.Add(new OrderItem { Id = Guid.NewGuid(), OrderId = order.Id, ProductId = product.Id, Quantity = 1,
                UnitPrice = order.Total, Total = order.Total, PaidAmount = order.Total, AffiliateCommissionEligible = true,
                AffiliateCommissionPolicyIdSnapshot = policy.Id, AffiliateCommissionRateSnapshot = .1m,
                AffiliateCommissionBaseAmountSnapshot = order.Total, AffiliateCommissionAmountSnapshot = amount,
                AffiliateCommissionSnapshotCapturedAtUtc = DateTime.UtcNow });
            var attribution = AffiliateAttribution.Create(f.Store, order.Id, f.Affiliate.Id, f.Affiliate.ReferralCode, AttributionSource.ReferralLink);
            attribution.Id = Guid.NewGuid(); f.Db.AddRange(category, product, pc, policy, order, attribution); await f.Db.SaveChangesAsync();
            f.Db.ChangeTracker.Clear();
            await affiliate.RecordCommissionForOrderAsync(order.Id);
            var fulfillment = new OrderFulfillmentService(f.Db, events.Object, NullLogger<OrderFulfillmentService>.Instance);
            var delivered = await fulfillment.MarkDeliveredAsync(order.Id, FulfillmentRequestSource.Manual, f.Label, "https://example.invalid/p514-no-shipment");
            Assert.True(delivered.Success, delivered.ErrorMessage);
            var completed = await fulfillment.MarkCompletedAsync(order.Id, FulfillmentRequestSource.Manual, f.Actor.ToString(), $"P514-COMPLETE:{order.Id}");
            Assert.True(completed.Success, completed.ErrorMessage); f.Db.ChangeTracker.Clear();
            await affiliate.ApproveCommissionForDeliveredOrderAsync(order.Id, f.Store, DateTime.UtcNow, "P514-ISOLATED-INTERNAL-TEST", 1,
                deliveryReference: f.Label, proofOfDeliveryUrl: "https://example.invalid/p514-no-shipment");
            var evt = earnedEvents.Single(e => e.OrderId == order.Id);
            await new AffiliateCommissionEarnedAccountingConsumer(new AccountResolver(f.Db, NullLogger<AccountResolver>.Instance),
                Fixture.Posting(f.Db), new AffiliateTelemetry(), NullLogger<AffiliateCommissionEarnedAccountingConsumer>.Instance, f.Db)
                .Consume(Mock.Of<ConsumeContext<AffiliateCommissionEarnedEvent>>(c => c.Message == evt && c.CancellationToken == CancellationToken.None));
        }
        var payout = new AffiliatePayoutService(f.Db, NullLogger<AffiliatePayoutService>.Instance);
        f.Request = await payout.RequestPayoutAsync(f.Affiliate.Id, amounts.Sum(), f.Store);
        await payout.ApprovePayoutAsync(f.Request.Id, f.Store, f.Actor, f.Label); f.Db.ChangeTracker.Clear();
        await Evidence(f, "approved");
        return f;
        }
        catch { await f.DisposeAsync(); throw; }
    }

    private static async Task Evidence(Fixture f, string stage)
    {
        await using var db = f.Open();
        var request = await db.AffiliatePayoutRequests.AsNoTracking().SingleAsync(r => r.Id == f.Request.Id);
        var source = await db.LedgerAccounts.IgnoreQueryFilters().AsNoTracking().SingleAsync(a => a.Id == f.SourceAccountId);
        var journals = await db.JournalEntries.IgnoreQueryFilters().AsNoTracking().Include(j => j.LedgerEntries)
            .Where(j => j.SourceId == request.Id).ToListAsync();
        var commission = await db.AffiliateCommissionLedger.IgnoreQueryFilters().AsNoTracking().Where(e => e.AffiliateAccountId == f.Affiliate.Id).ToListAsync();
        var movements = await new TreasuryReadService(db, NullLogger<TreasuryReadService>.Instance).GetMovementsAsync(f.Store,
            source.AccountCode, DateTime.UtcNow.AddDays(-1), DateTime.UtcNow.AddDays(1));
        var balance = await new AffiliatePayoutService(db, NullLogger<AffiliatePayoutService>.Instance).GetAvailableBalanceAsync(f.Affiliate.Id, f.Store);
        var ledger = await db.LedgerEntries.IgnoreQueryFilters().Where(l => l.AccountId == source.Id &&
            (l.JournalEntry.Status == JournalStatus.Posted || l.JournalEntry.Status == JournalStatus.Reversed)).SumAsync(l => l.DebitAmount - l.CreditAmount);
        Assert.Equal(source.CurrentBalance, ledger);
        var audits = await db.AffiliateManualPayouts.AsNoTracking().Where(m => m.RequestId == request.Id).ToListAsync();
        var result = new { stage, f.Label, f.Store, f.Actor, affiliateId = f.Affiliate.Id, requestId = request.Id, f.CashboxId, f.SourceAccountId,
            request.Status, request.ApprovedAmount, request.RemainingAmount, balance, source.CurrentBalance, ledger,
            commission = commission.Select(e => new { e.Id, e.OrderId, e.OrderItemId, e.OriginalEntryId, e.EntryType, e.CommissionAmount, e.StoreTenantId, e.AffiliateAccountId }),
            journals = journals.Select(j => new { j.Id, j.IdempotencyKey, j.SourceReference, j.Status,
                lines = j.LedgerEntries.Select(l => new { l.AccountId, l.DebitAmount, l.CreditAmount, l.BalanceAfter }) }),
            movements, audits = audits.Select(a => new { a.Id, a.RequestId, a.AllocationsJson, a.AdministratorId, a.SourceAccountId, a.CommandFingerprint }) };
        await File.WriteAllTextAsync(EvidencePath($"p514-{f.Store}-{stage}.json"), JsonSerializer.Serialize(result, Json));
    }

    private static string EvidencePath(string name) => Path.Combine(
        Environment.GetEnvironmentVariable("APP17D_EVIDENCE_DIR") ?? throw new InvalidOperationException("Fixture evidence directory required"), name);

    private static async Task AssertPaid(Fixture f, int orders)
    {
        await using var db = f.Open(); var amount = f.Request.ApprovedAmount!.Value;
        var audit = Assert.Single(await db.AffiliateManualPayouts.Where(m => m.RequestId == f.Request.Id).ToListAsync());
        using var allocations = JsonDocument.Parse(audit.AllocationsJson);
        Assert.Equal(orders, allocations.RootElement.GetArrayLength());
        Assert.Equal(amount, allocations.RootElement.EnumerateArray().Sum(a => a.GetProperty("Amount").GetDecimal()));
        var journal = Assert.Single(await db.JournalEntries.IgnoreQueryFilters().Include(j => j.LedgerEntries).Where(j => j.SourceId == f.Request.Id).ToListAsync());
        Assert.Equal($"AFFILIATE_MANUAL_PAYOUT:v1:{f.Store}:{f.Request.Id}", journal.IdempotencyKey);
        Assert.Equal(2, journal.LedgerEntries.Count); Assert.Equal(amount, journal.TotalDebits); Assert.Equal(amount, journal.TotalCredits);
        Assert.Equal(amount, Assert.Single(journal.LedgerEntries.Where(l => l.AccountId == f.SourceAccountId)).CreditAmount);
        var paid = await db.AffiliateCommissionLedger.IgnoreQueryFilters().Where(e => e.AffiliateAccountId == f.Affiliate.Id && e.EntryType == CommissionEntryType.Paid).ToListAsync();
        Assert.Equal(orders, paid.Select(e => e.OrderId).Distinct().Count()); Assert.Equal(amount, paid.Sum(e => e.CommissionAmount));
        foreach (var p in paid) Assert.True(await db.AffiliateCommissionLedger.IgnoreQueryFilters().AnyAsync(a => a.Id == p.OriginalEntryId &&
            a.EntryType == CommissionEntryType.Approved && a.OrderId == p.OrderId && a.OrderItemId == p.OrderItemId && a.StoreTenantId == f.Store && a.AffiliateAccountId == f.Affiliate.Id));
        var request = await db.AffiliatePayoutRequests.SingleAsync(r => r.Id == f.Request.Id);
        Assert.Equal(PayoutRequestStatus.Paid, request.Status); Assert.Equal(0, request.RemainingAmount);
        Assert.Equal(f.Actor, audit.AdministratorId); Assert.Equal(f.Actor, request.PaidBy); Assert.Equal(f.Actor, journal.CreatedByUserId);
        Assert.Single(await db.AffiliateBalanceLedger.IgnoreQueryFilters().Where(l => l.ReferenceId == request.Id && l.Amount == -amount).ToListAsync());
        Assert.Single(await db.AuditLogs.IgnoreQueryFilters().Where(a => a.EntityId == audit.Id.ToString() && a.ActionType == "AffiliateAdminManualPayout").ToListAsync());
        Assert.False(await db.AffiliatePayoutTransferEvidence.IgnoreQueryFilters().AnyAsync(e => e.StoreTenantId == f.Store));
        Assert.False(await db.AffiliatePayoutTransferAllocations.IgnoreQueryFilters().AnyAsync(e => e.ReferenceId == request.Id));
        var source = await db.LedgerAccounts.IgnoreQueryFilters().SingleAsync(a => a.Id == f.SourceAccountId);
        Assert.Equal(500 - amount, source.CurrentBalance);
        var movements = await new TreasuryReadService(db, NullLogger<TreasuryReadService>.Instance).GetMovementsAsync(f.Store, source.AccountCode, DateTime.UtcNow.AddDays(-1), DateTime.UtcNow.AddDays(1));
        Assert.Equal(amount, Assert.Single(movements.Where(m => m.ReferenceId == request.Id)).Credit);
        var balance = await new AffiliatePayoutService(db, NullLogger<AffiliatePayoutService>.Instance).GetAvailableBalanceAsync(f.Affiliate.Id, f.Store);
        Assert.Equal(0, balance.RawAvailable); Assert.Equal(0, balance.TotalPendingRequests);
        var tenant = new TenantContext(); tenant.SetTenantId(f.Store);
        Assert.IsType<OkObjectResult>(await new AffiliateManualPayoutController(db, tenant, Mock.Of<IAffiliateSettlementOrchestrator>()).Review(request.Id, default));
    }

    [Fact]
    public async Task MultiOrder_75Plus125_OnePhysicalCredit_ReplayUnchanged()
    {
        await using var f = await Create(75m, 125m); var command = f.Command;
        var result = await f.Orchestrator(f.Db).ExecuteAdminManualPayoutAsync(f.Request.Id, f.Store, f.Actor, command);
        Assert.False(result.WasIdempotentSkip); Assert.Equal(2, result.PaidOrders.Count); await AssertPaid(f, 2); await Evidence(f, "paid");
        // Even the inactive legacy Paid accounting consumer must not create another
        // per-order outflow if an old subscription or replay invokes it.
        foreach (var order in result.PaidOrders)
        {
            await using var consumerDb = f.Open();
            var evt = new AffiliateCommissionPaidEvent { TenantId = f.Store, StoreTenantId = f.Store, OrderId = order.OrderId,
                AffiliateAccountId = f.Affiliate.Id, SettlementReferenceId = f.Request.Id, TotalPaid = order.TotalPaid, Currency = "EGP", ItemCount = order.ItemCount };
            var consumer = new AffiliateCommissionPaidAccountingConsumer(new AccountResolver(consumerDb, NullLogger<AccountResolver>.Instance),
                Fixture.Posting(consumerDb), Mock.Of<ICashboxLedgerAccountService>(MockBehavior.Strict), new AffiliateTelemetry(),
                NullLogger<AffiliateCommissionPaidAccountingConsumer>.Instance, consumerDb);
            var context = Mock.Of<ConsumeContext<AffiliateCommissionPaidEvent>>(c => c.Message == evt && c.CancellationToken == CancellationToken.None);
            await consumer.Consume(context); await consumer.Consume(context);
        }
        await AssertPaid(f, 2);
        await using var replay = f.Open();
        var second = await f.Orchestrator(replay).ExecuteAdminManualPayoutAsync(f.Request.Id, f.Store, f.Actor, command);
        Assert.True(second.WasIdempotentSkip); Assert.Equal(result.ManualPayoutId, second.ManualPayoutId); Assert.Equal(result.ExecutedAtUtc, second.ExecutedAtUtc);
        await AssertPaid(f, 2); await Evidence(f, "replayed");
    }

    [Fact]
    public async Task OneOrder_ExactReplay_StillOneCredit()
    {
        await using var f = await Create(100m); var command = f.Command;
        await f.Orchestrator(f.Db).ExecuteAdminManualPayoutAsync(f.Request.Id, f.Store, f.Actor, command); await AssertPaid(f, 1);
        await using var db = f.Open(); Assert.True((await f.Orchestrator(db).ExecuteAdminManualPayoutAsync(f.Request.Id, f.Store, f.Actor, command)).WasIdempotentSkip);
        await AssertPaid(f, 1); await Evidence(f, "one-order-replayed");
    }

    [Fact]
    public async Task ConcurrentDuplicates_OneCommittedResult_CompetitorFreshReplay()
    {
        await using var f = await Create(35m, 65m); var command = f.Command;
        async Task<string> Attempt()
        {
            await using var db = f.Open();
            try { var r = await f.Orchestrator(db).ExecuteAdminManualPayoutAsync(f.Request.Id, f.Store, f.Actor, command); return r.WasIdempotentSkip ? "replayed" : "committed"; }
            catch (InvalidOperationException e) when (e.Message.StartsWith("AFFILIATE_CONCURRENCY_CONFLICT")) { return "retry-required"; }
        }
        var outcomes = await Task.WhenAll(Attempt(), Attempt()); Assert.Equal(1, outcomes.Count(o => o == "committed"));
        await using var db = f.Open(); Assert.True((await f.Orchestrator(db).ExecuteAdminManualPayoutAsync(f.Request.Id, f.Store, f.Actor, command)).WasIdempotentSkip);
        await AssertPaid(f, 2); await Evidence(f, "concurrent");
        await File.WriteAllTextAsync(EvidencePath($"p514-{f.Store}-concurrency.json"), JsonSerializer.Serialize(outcomes));
    }

    [Fact]
    public async Task FailedPostingAndOutbox_RollBackAllFinancialRows_ThenRetry()
    {
        await using var f = await Create(40m, 80m); var command = f.Command;
        await using (var failed = f.Open(new FailJournalSave()))
            await Assert.ThrowsAsync<InvalidOperationException>(() => f.Orchestrator(failed).ExecuteAdminManualPayoutAsync(f.Request.Id, f.Store, f.Actor, command));
        await f.AssertNoPayout(); await Evidence(f, "posting-rolled-back");
        await using (var failed = f.Open())
            await Assert.ThrowsAsync<InvalidOperationException>(() => f.Orchestrator(failed, true).ExecuteAdminManualPayoutAsync(f.Request.Id, f.Store, f.Actor, command));
        await f.AssertNoPayout(); await Evidence(f, "outbox-rolled-back");
        await using var db = f.Open(); await f.Orchestrator(db).ExecuteAdminManualPayoutAsync(f.Request.Id, f.Store, f.Actor, command);
        await AssertPaid(f, 2); await Evidence(f, "failure-retried");
    }

    private sealed class FailJournalSave : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData data, InterceptionResult<int> result, CancellationToken ct = default)
        {
            if (data.Context!.ChangeTracker.Entries<PointOfSale.Core.Entities.Accounting.JournalEntry>().Any(e => e.State == EntityState.Added && e.Entity.SourceType == "AffiliateCommissionPaid"))
                throw new InvalidOperationException("P514-INJECTED-JOURNAL-SAVE-FAILURE");
            return ValueTask.FromResult(result);
        }
    }

    [Fact]
    public async Task InvalidPersistedSourcesAndFingerprints_LeaveBalancesAndAuthorityUntouched()
    {
        await using var f = await Create(55m, 65m); var command = f.Command;
        async Task Reject(AdminManualPayoutCommand c, string code)
        {
            await using var db = f.Open();
            var e = await Assert.ThrowsAsync<InvalidOperationException>(() => f.Orchestrator(db).ExecuteAdminManualPayoutAsync(f.Request.Id, f.Store, f.Actor, c));
            Assert.StartsWith(code, e.Message); await f.AssertNoPayout();
        }
        await Reject(command with { SourceFingerprint = null }, "AFFILIATE_MANUAL_REQUEST_CHANGED");
        await Reject(command with { SourceFingerprint = new string('0', 64) }, "AFFILIATE_MANUAL_REQUEST_CHANGED");
        var empty = await AffiliateManualCashSource.ResolveAsync(f.Db, f.Store, f.EmptyCashboxId);
        await Reject(command with { CashboxId = f.EmptyCashboxId, SourceFingerprint = AffiliateManualCashSource.Fingerprint(f.EmptyCashboxId, empty!) }, "AFFILIATE_MANUAL_SOURCE_UNDERFUNDED");
        await Reject(command with { CashboxId = Guid.NewGuid() }, "AFFILIATE_MANUAL_SOURCE_INVALID");
        foreach (var scenario in new[] { "inactive-cashbox", "disabled-mapping", "unmapped", "inactive-ledger", "cross-store", "changed-parent" })
        {
            await using var metadata = f.Open();
            var cashbox = await metadata.Cashboxes.SingleAsync(c => c.Id == f.CashboxId);
            var mapping = await metadata.TreasuryAccounts.IgnoreQueryFilters().SingleAsync(t => t.AccountId == f.SourceAccountId);
            var account = await metadata.LedgerAccounts.IgnoreQueryFilters().SingleAsync(a => a.Id == f.SourceAccountId);
            var originalAccount = mapping.AccountId; var originalParent = account.ParentAccountId;
            switch (scenario)
            {
                case "inactive-cashbox": cashbox.IsActive = false; break;
                case "disabled-mapping": mapping.IsActive = false; break;
                case "unmapped": mapping.ReferenceCode = null; break;
                case "inactive-ledger": account.IsActive = false; break;
                case "cross-store": cashbox.TenantId = AffiliateP53ePrimarySupport.Store; break;
                case "changed-parent": account.ParentAccountId = null; break;
            }
            await metadata.SaveChangesAsync();
            try { await Reject(command, "AFFILIATE_MANUAL_SOURCE_INVALID"); }
            finally
            {
                cashbox.IsActive = true; cashbox.TenantId = f.Store; mapping.IsActive = true;
                mapping.ReferenceCode = f.CashboxId.ToString("N"); mapping.AccountId = originalAccount;
                account.IsActive = true; account.ParentAccountId = originalParent;
                await metadata.SaveChangesAsync();
            }
        }
        await using (var metadata = f.Open())
        {
            var parent = await metadata.LedgerAccounts.IgnoreQueryFilters().SingleAsync(a => a.TenantId == f.Store && a.AccountCode == "1010");
            var generic = new Cashbox { Id = Guid.NewGuid(), TenantId = f.Store, Name = "P514 INVALID GENERIC SOURCE", IsActive = true, OpenedByUserId = f.Actor, OpenedAt = DateTime.UtcNow };
            var bad = await metadata.TreasuryAccounts.IgnoreQueryFilters().SingleAsync(t => t.AccountId == parent.Id);
            var reference = bad.ReferenceCode;
            bad.ReferenceCode = generic.Id.ToString("N"); metadata.Add(generic); await metadata.SaveChangesAsync();
            try { await Reject(command with { CashboxId = generic.Id }, "AFFILIATE_MANUAL_SOURCE_INVALID"); }
            finally { bad.ReferenceCode = reference; await metadata.SaveChangesAsync(); }
        }
        await using (var metadata = f.Open())
        {
            metadata.Add(PointOfSale.Core.Entities.Accounting.TreasuryAccount.CreateBank(f.Store, f.SourceAccountId, "P514 rejected shared mapping"));
            var error = await Assert.ThrowsAsync<DbUpdateException>(() => metadata.SaveChangesAsync());
            Assert.Equal(PostgresErrorCodes.UniqueViolation, Assert.IsType<PostgresException>(error.InnerException).SqlState);
            // Do not bypass the real PostgreSQL exclusivity constraint to construct a bad source.
            metadata.ChangeTracker.Clear(); await f.AssertNoPayout();
        }
        await Evidence(f, "invalid-sources-no-effects");
    }

    [Fact]
    public async Task PayoutExceedingCurrentCashFunds_IsRejectedWithoutPartialPosting()
    {
        await using var f = await Create(300m, 300m);
        var e = await Assert.ThrowsAsync<InvalidOperationException>(() => f.Orchestrator(f.Db).ExecuteAdminManualPayoutAsync(f.Request.Id, f.Store, f.Actor, f.Command));
        Assert.StartsWith("AFFILIATE_MANUAL_SOURCE_UNDERFUNDED", e.Message); await f.AssertNoPayout(); await Evidence(f, "underfunded-no-effects");
    }

    [Fact]
    public async Task SourceShareLocks_BlockMappingAndCashboxWriters_UntilRollback()
    {
        await using var f = await Create(100m); var command = f.Command;
        await using var holder = f.Open();
        await using var tx = await holder.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        var load = typeof(AffiliateSettlementOrchestrator).GetMethod("LoadManualFundingAsync", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        await (Task)load.Invoke(f.Orchestrator(holder), [f.Store, f.CashboxId, command.Amount, command.SourceFingerprint, CancellationToken.None])!;
        foreach (var table in new[] { "Cashboxes", "TreasuryAccounts" })
        {
            await using var contender = f.Open(); await using var competing = await contender.Database.BeginTransactionAsync();
            await contender.Database.ExecuteSqlRawAsync("SET LOCAL lock_timeout='300ms'");
            var id = table == "Cashboxes" ? f.CashboxId : (await holder.TreasuryAccounts.SingleAsync(t => t.AccountId == f.SourceAccountId)).Id;
            var sql = table switch
            {
                "Cashboxes" => "SELECT 1 FROM \"Cashboxes\" WHERE \"Id\" = {0} FOR UPDATE",
                "TreasuryAccounts" => "SELECT 1 FROM \"TreasuryAccounts\" WHERE \"Id\" = {0} FOR UPDATE",
                _ => throw new InvalidOperationException("Unknown fixture lock target")
            };
            var error = await Assert.ThrowsAsync<PostgresException>(() => contender.Database.ExecuteSqlRawAsync(sql, id));
            Assert.Equal(PostgresErrorCodes.LockNotAvailable, error.SqlState); await competing.RollbackAsync();
        }
        await tx.RollbackAsync(); await f.AssertNoPayout(); await Evidence(f, "locks-rolled-back");
    }

    [Fact]
    public async Task RealSerializationAbort_AutomaticallyRetriesInFreshScope_OneCredit()
    {
        await using var f = await Create(45m, 75m); var command = f.Command;
        var services = new ServiceCollection(); var scopes = 0;
        services.AddScoped(_ => { Interlocked.Increment(ref scopes); return f.Open(); });
        services.AddScoped<IAffiliatePayoutService>(s => new AffiliatePayoutService(s.GetRequiredService<ApplicationDbContext>(), NullLogger<AffiliatePayoutService>.Instance));
        services.AddScoped<IFinancialPostingService>(s => Fixture.Posting(s.GetRequiredService<ApplicationDbContext>()));
        services.AddScoped<IAccountResolver>(s => new AccountResolver(s.GetRequiredService<ApplicationDbContext>(), NullLogger<AccountResolver>.Instance));
        services.AddScoped<ICashboxLedgerAccountService>(s => new CashboxLedgerAccountService(s.GetRequiredService<ApplicationDbContext>(), NullLogger<CashboxLedgerAccountService>.Instance));
        var events = new Mock<IEventPublisher>(); events.SetReturnsDefault(Task.CompletedTask); services.AddScoped(_ => events.Object);
        await using var provider = services.BuildServiceProvider();
        await using var first = f.Open(new AbortOnce());
        var orchestrator = new AffiliateSettlementOrchestrator(first, new AffiliatePayoutService(first, NullLogger<AffiliatePayoutService>.Instance),
            events.Object, Fixture.Posting(first), new AccountResolver(first, NullLogger<AccountResolver>.Instance),
            new CashboxLedgerAccountService(first, NullLogger<CashboxLedgerAccountService>.Instance), NullLogger<AffiliateSettlementOrchestrator>.Instance,
            Options.Create(new AffiliateSettlementOptions()), provider.GetRequiredService<IServiceScopeFactory>());
        var result = await orchestrator.ExecuteAdminManualPayoutAsync(f.Request.Id, f.Store, f.Actor, command);
        Assert.False(result.WasIdempotentSkip); Assert.Equal(1, scopes); await AssertPaid(f, 2); await Evidence(f, "serialization-retried");
    }

    private sealed class AbortOnce : SaveChangesInterceptor
    {
        private bool aborted;
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData data, InterceptionResult<int> result, CancellationToken ct = default)
        {
            if (!aborted && data.Context!.ChangeTracker.Entries<AffiliateManualPayout>().Any(e => e.State == EntityState.Added))
            {
                aborted = true;
                await data.Context.Database.ExecuteSqlRawAsync("DO $$ BEGIN RAISE EXCEPTION 'P514 controlled serialization abort' USING ERRCODE='40001'; END $$", ct);
            }
            return result;
        }
    }
}
