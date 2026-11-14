using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MongoDB.Driver;
using PointOfSale.Core.Entities;
using PointOfSale.Core.Entities.Affiliate;
using PointOfSale.Core.Events;
using PointOfSale.Core.Events.Affiliate;
using PointOfSale.Core.Interfaces;
using PointOfSale.Core.Interfaces.Accounting;
using PointOfSale.Core.Interfaces.Affiliate;
using PointOfSale.Core.Interfaces.Treasury;
using PointOfSale.Infrastructure.Accounting;
using PointOfSale.Infrastructure.Data;
using PointOfSale.Infrastructure.EventBus;
using PointOfSale.Infrastructure.MongoDB.Configuration;
using PointOfSale.Infrastructure.MongoDB.Documents.Affiliate;
using PointOfSale.Infrastructure.MongoDB.Services;
using PointOfSale.Infrastructure.MultiTenancy;
using PointOfSale.Infrastructure.Services.Affiliate;
using PointOfSale.Infrastructure.Treasury;
using Xunit.Abstractions;
using BusMessage = MassTransit.EntityFrameworkCoreIntegration.OutboxMessage;
using static PointOfSale.Tests.Affiliate.AffiliateP53ePrimarySupport;

namespace PointOfSale.Tests.Affiliate;

/// <summary>Existing primary only; no application host, database lifecycle, provider,
/// carrier, transfer client or general P5 driver. Retain labelled records.</summary>
public sealed class AffiliateP53ePayoutAtomicityTests(ITestOutputHelper output)
{
    private const string Source = "DEV_P53E_TEST_ONLY_NO_TRANSFER";
    private static readonly Guid Cash = Guid.Parse("01a0828d-734b-7385-9659-ef041fa3e836");
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private sealed record Case(string Name, Guid Affiliate, Guid Request, Guid Order)
    {
        internal string Reference => $"P53E-SIMULATED-NO-TRANSFER:{Request:D}";
        internal string Fingerprint => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{Source}\n{Store:D}\n{Affiliate:D}\n{Request:D}\n{Order:D}\n100\nEGP")));
    }
    private static Case Load(string name)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(EvidenceDirectory, "lineage.json")));
        var c = document.RootElement.GetProperty("cases").GetProperty(name);
        return new(name, c.GetProperty("identity").GetProperty("affiliateId").GetGuid(),
            c.GetProperty("withdrawal").GetProperty("id").GetGuid(), c.GetProperty("checkout").GetProperty("orderId").GetGuid());
    }
    private static Task Save(string name, object value) => File.WriteAllTextAsync(Path.Combine(EvidenceDirectory, name), JsonSerializer.Serialize(value, Json));

    private static MongoDbContext Mongo() => new(Options.Create(Config.GetSection("MongoDB").Get<MongoDbSettings>()!), NullLogger<MongoDbContext>.Instance);

    // Test assembly only. Normal HTTP requires trusted evidence and cannot resolve this verifier.
    private sealed class SimulatedFact(ApplicationDbContext db, Case task) : IAffiliatePayoutEvidenceVerifier
    {
        public async Task<bool> VerifyAsync(AffiliatePayoutTransferEvidence e, Guid affiliateAccountId, Guid storeTenantId, decimal amount, CancellationToken ct)
        {
            if (affiliateAccountId != task.Affiliate || storeTenantId != Store || amount != 100 ||
                e.IsDeleted || e.VerificationSource != Source || e.PaymentReference != task.Reference ||
                e.VerificationFingerprint != task.Fingerprint || e.ExpectedAmount != 100 || e.Currency != "EGP" ||
                e.RailNamespace != "AFFILIATE_PAYOUT" || e.StoreTenantId != Store || e.VerifiedAtUtc == default) return false;
            return await db.AffiliatePayoutRequests.IgnoreQueryFilters().AsNoTracking().AnyAsync(r =>
                r.Id == task.Request && r.AffiliateAccountId == task.Affiliate && r.StoreTenantId == Store &&
                r.Status == PayoutRequestStatus.Approved && r.ApprovedAmount == 100 && r.Currency == "EGP" &&
                r.PayoutWalletTypeSnapshot == "Other" && r.PayoutWalletNumberSnapshot == $"P53E{task.Name.ToUpperInvariant()}NOTRANSFER", ct);
        }
    }

    private static async Task AdmitFact(Case task)
    {
        await using var db = await Open();
        var account = await db.AffiliateAccounts.IgnoreQueryFilters().SingleAsync(a => a.Id == task.Affiliate);
        Assert.StartsWith($"P53E {task.Name} SIMULATED", account.DisplayName);
        Assert.True(await db.AffiliateUsers.IgnoreQueryFilters().AnyAsync(u => u.AffiliateAccountId == account.Id && u.EmailVerified && !u.IsDeleted));
        var request = await db.AffiliatePayoutRequests.IgnoreQueryFilters().SingleAsync(r => r.Id == task.Request);
        Assert.Equal(PayoutRequestStatus.Approved, request.Status);
        var earned = Assert.Single(await db.AffiliateCommissionLedger.IgnoreQueryFilters().Where(e => e.OrderId == task.Order && e.EntryType == CommissionEntryType.Earned).ToListAsync());
        var approved = Assert.Single(await db.AffiliateCommissionLedger.IgnoreQueryFilters().Where(e => e.OrderId == task.Order && e.EntryType == CommissionEntryType.Approved).ToListAsync());
        Assert.Equal(earned.Id, approved.OriginalEntryId); Assert.Equal(100, approved.CommissionAmount);
        Assert.Equal(task.Affiliate, approved.AffiliateAccountId);
        var earnedJournal = await db.JournalEntries.Include(j => j.LedgerEntries).SingleAsync(j => j.SourceType == "AffiliateCommissionEarned" && j.SourceId == task.Order);
        Assert.Equal(100, earnedJournal.TotalDebits); Assert.Equal(100, earnedJournal.TotalCredits);
        Assert.False(await db.SyntheticQaOrders.AnyAsync(o => o.OrderId == task.Order));
        if (await db.AffiliatePayoutTransferEvidence.IgnoreQueryFilters().AnyAsync(e => e.PaymentReference == task.Reference)) return;
        var evidence = AffiliatePayoutTransferEvidence.Create(Store, "AFFILIATE_PAYOUT", task.Reference, 100, "EGP", DateTime.UtcNow, Source, task.Fingerprint);
        Assert.True(await new SimulatedFact(db, task).VerifyAsync(evidence, task.Affiliate, Store, 100, default));
        db.Add(evidence);
        db.AuditLogs.Add(new AuditLog { TenantId = Store, UserId = Actor, ActionType = "P53E_SIMULATED_PAYOUT_FACT",
            EntityType = nameof(AffiliatePayoutTransferEvidence), EntityId = evidence.Id.ToString(), Source = Source, SuccessFlag = true,
            Description = $"TEST ASSEMBLY ONLY. NO TRANSFER. Exact request {task.Request}, source order {task.Order}. Canonical eligibility, wallet, posting and evidence allocation still required." });
        await db.SaveChangesAsync();
    }

    private sealed class PaidOnlyPublisher(MassTransitEventPublisher actual) : IEventPublisher
    {
        internal List<AffiliateCommissionPaidEvent> Events { get; } = [];
        public async Task PublishAsync<T>(T e, CancellationToken ct = default) where T : IntegrationEvent
        {
            var paid = Assert.IsType<AffiliateCommissionPaidEvent>(e);
            Assert.Equal(Store, paid.StoreTenantId);
            Events.Add(paid); await actual.PublishAsync(e, ct);
        }
        public Task PublishBatchAsync<T>(IEnumerable<T> e, CancellationToken ct = default) where T : IntegrationEvent => throw new NotSupportedException();
        public Task PublishSensitiveAsync<T>(T e, CancellationToken ct = default) where T : IntegrationEvent, ISensitiveIntegrationEvent => throw new NotSupportedException();
    }

    private static ServiceProvider Provider(Case task, string application, CommitBoundary? boundary = null)
    {
        var services = new ServiceCollection(); services.AddLogging();
        services.AddScoped<ITenantContext>(_ => { var t = new TenantContext(); t.SetTenantId(Store); return t; });
        services.AddScoped(_ => Open(application, boundary == null ? [] : [boundary]).GetAwaiter().GetResult());
        services.AddScoped<IAffiliatePayoutService, AffiliatePayoutService>();
        services.AddScoped<IFinancialPostingService, FinancialPostingService>();
        services.AddScoped<IAccountResolver, AccountResolver>();
        services.AddScoped<ICashboxLedgerAccountService, CashboxLedgerAccountService>();
        services.AddScoped<MassTransitEventPublisher>(); services.AddScoped<PaidOnlyPublisher>();
        services.AddScoped<IEventPublisher>(sp => sp.GetRequiredService<PaidOnlyPublisher>());
        services.AddScoped<IAffiliatePayoutEvidenceVerifier>(sp => new SimulatedFact(sp.GetRequiredService<ApplicationDbContext>(), task));
        services.AddSingleton<IOptions<AffiliateSettlementOptions>>(Options.Create(new AffiliateSettlementOptions()));
        services.AddScoped<AffiliateSettlementOrchestrator>();
        services.AddMassTransit(bus =>
        {
            bus.AddEntityFrameworkOutbox<ApplicationDbContext>(o => { o.UsePostgres(); o.UseBusOutbox(); o.IsolationLevel = System.Data.IsolationLevel.ReadCommitted; });
            // Only construct the normal transactional publisher/outbox. Do NOT start
            // another application/consumer host. The existing API dispatches it.
            bus.UsingRabbitMq((context, cfg) =>
            {
                var rabbit = Config.GetSection("RabbitMQ");
                cfg.Host(rabbit["Host"] ?? "localhost", rabbit["VirtualHost"] ?? "/", h =>
                { h.Username(rabbit["Username"] ?? "guest"); h.Password(rabbit["Password"] ?? "guest"); });
            });
        });
        return services.BuildServiceProvider(validateScopes: true);
    }

    private static async Task<(PayoutExecutionResult Result, AffiliateCommissionPaidEvent[] Events)> Execute(ServiceProvider provider, Case task,
        string? reference = null, Guid? affiliate = null)
    {
        await using var scope = provider.CreateAsyncScope();
        var result = await scope.ServiceProvider.GetRequiredService<AffiliateSettlementOrchestrator>().ExecuteAffiliatePayoutAsync(
            affiliate ?? task.Affiliate, Store, 100, SettlementSource.WithdrawalEngine, reference ?? task.Reference,
            payoutRequestId: task.Request, paidByUserId: Actor, adminNotes: Source);
        return (result, scope.ServiceProvider.GetRequiredService<PaidOnlyPublisher>().Events.ToArray());
    }

    private sealed class ControlledAbort : Exception { internal ControlledAbort() : base("P53E_TEST_ONLY_AFTER_ALL_SQL_AND_OUTBOX_BEFORE_COMMIT") { } }

    // Actual EF/Npgsql commit interception, not an orchestrator mock or HTTP switch.
    private sealed class CommitBoundary(Case task, bool fail) : DbTransactionInterceptor
    {
        internal TaskCompletionSource Staged { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Hits;
        public override async ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction,
            TransactionEventData eventData, InterceptionResult result, CancellationToken ct = default)
        {
            var db = (ApplicationDbContext)eventData.Context!;
            if (!db.ChangeTracker.Entries<AffiliatePayoutRequest>().Any(e => e.Entity.Id == task.Request && e.Entity.Status == PayoutRequestStatus.Paid)) return result;
            if (Interlocked.Increment(ref Hits) != 1) return result;
            Assert.Equal(System.Data.IsolationLevel.Serializable, transaction.IsolationLevel);
            Assert.Single(await db.AffiliateBalanceLedger.Where(e => e.ReferenceId == task.Request).ToListAsync(ct));
            var journal = Assert.Single(await db.JournalEntries.Include(j => j.LedgerEntries).Where(j => j.SourceType == "AffiliateCommissionPaid" && j.SourceId == task.Request).ToListAsync(ct));
            Assert.Equal(100, journal.TotalDebits); Assert.Equal(100, journal.TotalCredits);
            Assert.Single(journal.LedgerEntries.Where(l => l.AccountId == Cash && l.CreditAmount == 100));
            var message = Assert.Single(await db.Set<BusMessage>().Where(m => m.Body.Contains(task.Request.ToString())).ToListAsync(ct));
            await File.WriteAllTextAsync(Path.Combine(EvidenceDirectory, $"{task.Name}-staged.json"), await SqlSnapshot(db, task), ct);
            await Save($"{task.Name}-staged-outbox.json", new { message.MessageId, message.OutboxId, message.SequenceNumber, message.Body, message.DestinationAddress });
            Staged.TrySetResult();
            if (fail) throw new ControlledAbort();
            await Release.Task.WaitAsync(TimeSpan.FromSeconds(45), ct);
            return result;
        }
    }

    private static async Task<string> SqlSnapshot(ApplicationDbContext db, Case task)
    {
        await using var cmd = db.Database.GetDbConnection().CreateCommand();
        cmd.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
        cmd.CommandText = """
            SELECT jsonb_build_object(
              'observedAt',clock_timestamp(),'database',current_database(),'server',host(inet_server_addr()),'pid',pg_backend_pid(),
              'request',(SELECT to_jsonb(r) FROM "AffiliatePayoutRequests" r WHERE "Id"=@request),
              'commission',(SELECT jsonb_agg(to_jsonb(c) ORDER BY "RecordedAtUtc","Id") FROM "AffiliateCommissionLedger" c WHERE "OrderId"=@order),
              'debits',(SELECT jsonb_agg(to_jsonb(d)) FROM "AffiliateBalanceLedger" d WHERE "ReferenceId"=@request),
              'evidence',(SELECT jsonb_agg(to_jsonb(e)) FROM "AffiliatePayoutTransferEvidence" e WHERE "PaymentReference"=@reference),
              'allocations',(SELECT jsonb_agg(to_jsonb(a)) FROM "AffiliatePayoutTransferAllocations" a WHERE "ReferenceId"=@request),
              'journals',(SELECT jsonb_agg(to_jsonb(j)) FROM "LedgerJournalEntries" j WHERE "SourceType"='AffiliateCommissionPaid' AND "SourceId"=@request),
              'journalLines',(SELECT jsonb_agg(to_jsonb(l)) FROM "LedgerLines" l JOIN "LedgerJournalEntries" j ON j."Id"=l."JournalEntryId" WHERE j."SourceType"='AffiliateCommissionPaid' AND j."SourceId"=@request),
              'cash',(SELECT to_jsonb(a) FROM "LedgerAccounts" a WHERE "Id"=@cash),
              'busOutbox',(SELECT jsonb_agg(to_jsonb(m)) FROM "OutboxMessage" m WHERE "Body" LIKE '%'||@request::text||'%')
            )::text
            """;
        foreach (var (key, value) in new (string, object)[] { ("request", task.Request), ("order", task.Order), ("reference", task.Reference), ("cash", Cash) })
        { var p = cmd.CreateParameter(); p.ParameterName = key; p.Value = value; cmd.Parameters.Add(p); }
        return (string)(await cmd.ExecuteScalarAsync())!;
    }

    private static async Task AssertState(Case task, decimal opening, bool paid)
    {
        await using var db = await Open();
        var request = await db.AffiliatePayoutRequests.IgnoreQueryFilters().AsNoTracking().SingleAsync(r => r.Id == task.Request);
        Assert.Equal(paid ? PayoutRequestStatus.Paid : PayoutRequestStatus.Approved, request.Status);
        Assert.Equal(paid ? 0 : 100, request.RemainingAmount);
        Assert.Equal(paid ? task.Reference : null, request.PaymentReference);
        Assert.Equal(opening - (paid ? 100 : 0), await db.LedgerAccounts.Where(a => a.Id == Cash).Select(a => a.CurrentBalance).SingleAsync());
        var commissions = await db.AffiliateCommissionLedger.IgnoreQueryFilters().Where(c => c.OrderId == task.Order).ToListAsync();
        var earned = Assert.Single(commissions.Where(c => c.EntryType == CommissionEntryType.Earned));
        var approved = Assert.Single(commissions.Where(c => c.EntryType == CommissionEntryType.Approved));
        Assert.Equal(100, earned.CommissionAmount); Assert.Equal(100, approved.CommissionAmount); Assert.Equal(earned.Id, approved.OriginalEntryId);
        Assert.Equal(paid ? 1 : 0, commissions.Count(c => c.EntryType == CommissionEntryType.Paid));
        Assert.Equal(paid ? 1 : 0, await db.AffiliateBalanceLedger.CountAsync(d => d.ReferenceId == task.Request));
        Assert.Equal(paid ? 1 : 0, await db.AffiliatePayoutTransferAllocations.CountAsync(a => a.ReferenceId == task.Request));
        Assert.Equal(paid ? 100 : 0, await db.AffiliatePayoutTransferEvidence.Where(e => e.PaymentReference == task.Reference).Select(e => e.AllocatedAmount).SingleAsync());
        var journals = await db.JournalEntries.Include(j => j.LedgerEntries).Where(j => j.SourceType == "AffiliateCommissionPaid" && j.SourceId == task.Request).ToListAsync();
        Assert.Equal(paid ? 1 : 0, journals.Count);
        if (paid)
        {
            var p = Assert.Single(commissions.Where(c => c.EntryType == CommissionEntryType.Paid));
            // Exactly one eligible FIFO source for each independent task account.
            Assert.Equal(approved.Id, p.OriginalEntryId); Assert.Equal(100, p.CommissionAmount);
            var j = Assert.Single(journals); Assert.Equal(100, j.TotalDebits); Assert.Equal(100, j.TotalCredits);
            Assert.Single(j.LedgerEntries.Where(l => l.AccountId == Cash && l.CreditAmount == 100));
            var debit = await db.AffiliateBalanceLedger.SingleAsync(d => d.ReferenceId == task.Request); Assert.Equal(-100, debit.Amount);
        }
        else Assert.False(await db.Set<BusMessage>().AnyAsync(m => m.Body.Contains(task.Request.ToString())));
        await File.WriteAllTextAsync(Path.Combine(EvidenceDirectory, $"{task.Name}-{(paid ? "committed" : "rollback")}-sql.json"), await SqlSnapshot(db, task));
    }

    private static async Task AssertProjection(Case task, bool paid, Guid? eventId = null)
    {
        var mongo = Mongo(); AffiliateOrderDocument? doc = null;
        for (var i = 0; i < (paid ? 120 : 1); i++)
        {
            doc = await mongo.AffiliateOrders.Find(d => d.OrderId == task.Order && d.TenantId == Store).SingleOrDefaultAsync();
            if (doc != null && (!paid || doc.TotalPaid == 100)) break;
            await Task.Delay(500);
        }
        Assert.NotNull(doc); Assert.Equal(paid ? 100 : 0, doc.TotalPaid);
        if (eventId.HasValue) Assert.Contains(eventId.Value.ToString(), doc.ProcessedEventIds);
        await Save($"{task.Name}-{(paid ? "paid" : "not-paid")}-projection.json", new { doc.Id, doc.OrderId, doc.Status, doc.TotalApproved, doc.TotalPaid, doc.ProcessedEventIds, doc.Items });
    }

    [PrimaryAffiliateFact]
    public async Task FirstPayout_AfterAllFinancialAndOutboxStaging_AbortRollsBack_ThenSameRequestCommitsOnce()
    {
        var task = Load("rollback"); await AdmitFact(task);
        await using var before = await Open();
        var opening = await before.LedgerAccounts.Where(a => a.Id == Cash).Select(a => a.CurrentBalance).SingleAsync();
        await File.WriteAllTextAsync(Path.Combine(EvidenceDirectory, "rollback-opening-sql.json"), await SqlSnapshot(before, task));
        var boundary = new CommitBoundary(task, fail: true);
        await using (var provider = Provider(task, "P53E-rollback", boundary))
            await Assert.ThrowsAsync<ControlledAbort>(() => Execute(provider, task));
        Assert.Equal(1, boundary.Hits);
        await AssertState(task, opening, false); await AssertProjection(task, false);
        // Required authoritative HTTP read occurs before retry, using normal login.
        await ApiReadback(task, paid: false, "rollback-after-abort-api.json");
        await using var retryProvider = Provider(task, "P53E-retry");
        var retry = await Execute(retryProvider, task); Assert.False(retry.Result.WasIdempotentSkip);
        var fact = Assert.Single(retry.Events);
        await Save("rollback-retry-event.json", fact);
        await AssertState(task, opening, true); await AssertProjection(task, true, fact.EventId);
        await ApiReadback(task, true, "rollback-after-retry-api.json");
        output.WriteLine($"FIRST payout abort after journal + Paid + actual EF bus outbox, then retry COMMITTED: request={task.Request}; order={task.Order}; openingCash={opening}; finalCash={opening - 100}; event={fact.EventId}");
    }

    [PrimaryAffiliateFact]
    public async Task FirstPayout_TwoCanonicalExecutions_ObservedPrimaryAdvisoryLock_ExactlyOneCommit_ConflictsReadOnly()
    {
        var task = Load("race"); await AdmitFact(task);
        await using var observer = await Open("P53E-lock-observer");
        var opening = await observer.LedgerAccounts.Where(a => a.Id == Cash).Select(a => a.CurrentBalance).SingleAsync();
        await File.WriteAllTextAsync(Path.Combine(EvidenceDirectory, "race-opening-sql.json"), await SqlSnapshot(observer, task));
        var boundary = new CommitBoundary(task, fail: false);
        await using var firstProvider = Provider(task, "P53E-race-holder", boundary);
        await using var secondProvider = Provider(task, "P53E-race-contender");
        var first = Execute(firstProvider, task);
        await boundary.Staged.Task.WaitAsync(TimeSpan.FromSeconds(30));
        // At this instant the first request is still Approved on every independent connection.
        await AssertState(task, opening, false); await AssertProjection(task, false);
        var second = Execute(secondProvider, task);
        string? contention = null;
        try
        {
            for (var i = 0; i < 160; i++)
            {
                await using var cmd = observer.Database.GetDbConnection().CreateCommand();
                cmd.CommandText = """
                    SELECT jsonb_build_object('observedAt',clock_timestamp(),'pid',a.pid,'application',a.application_name,
                      'state',a.state,'waitType',a.wait_event_type,'wait',a.wait_event,'query',a.query,
                      'blockingPids',pg_blocking_pids(a.pid), 'holder',
                      (SELECT jsonb_agg(jsonb_build_object('pid',b.pid,'application',b.application_name,'state',b.state,'xactStart',b.xact_start))
                       FROM pg_stat_activity b WHERE b.pid=ANY(pg_blocking_pids(a.pid))))::text
                    FROM pg_stat_activity a WHERE a.application_name='P53E-race-contender' AND a.wait_event_type='Lock'
                      AND EXISTS(SELECT 1 FROM pg_stat_activity b WHERE b.pid=ANY(pg_blocking_pids(a.pid)) AND b.application_name='P53E-race-holder')
                    """;
                contention = await cmd.ExecuteScalarAsync() as string;
                if (contention != null) break;
                await Task.Delay(25);
            }
            Assert.NotNull(contention);
            await File.WriteAllTextAsync(Path.Combine(EvidenceDirectory, "race-pg-contention.json"), contention!);
            output.WriteLine(contention);
        }
        finally { boundary.Release.TrySetResult(); }
        var outcomes = await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(45));
        Assert.Single(outcomes.Where(o => !o.Result.WasIdempotentSkip));
        Assert.Single(outcomes.Where(o => o.Result.WasIdempotentSkip));
        var fact = Assert.Single(outcomes.SelectMany(o => o.Events));
        await Save("race-outcomes.json", new { opening, finalCash = opening - 100, committed = 1, idempotent = 1, fact });
        await AssertState(task, opening, true); await AssertProjection(task, true, fact.EventId);
        var referenceConflict = await Assert.ThrowsAsync<InvalidOperationException>(() => Execute(secondProvider, task, "P53E-CONFLICT-NO-TRANSFER"));
        Assert.Contains("IDEMPOTENCY_CONFLICT", referenceConflict.Message);
        var identityConflict = await Assert.ThrowsAsync<InvalidOperationException>(() => Execute(secondProvider, task, affiliate: Load("rollback").Affiliate));
        Assert.Contains("REQUEST_NOT_FOUND", identityConflict.Message);
        await Save("race-controlled-conflicts.json", new { reference = referenceConflict.Message, identity = identityConflict.Message });
        await AssertState(task, opening, true); await ApiReadback(task, true, "race-final-api.json");
    }

    private static async Task ApiReadback(Case task, bool paid, string filename)
    {
        using var handler = new HttpClientHandler { CookieContainer = new System.Net.CookieContainer() };
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:5010"), Timeout = TimeSpan.FromSeconds(30) };
        using var login = await client.PostAsync("/api/affiliate/auth/login", new StringContent(JsonSerializer.Serialize(new
        { email = $"affiliate.p53e.{task.Name}.20261023@example.test", password = "P53eNoTransfer!2026" }), Encoding.UTF8, "application/json"));
        login.EnsureSuccessStatusCode();
        using var read = await client.GetAsync($"/api/affiliate/payouts?storeTenantId={Store:D}"); read.EnsureSuccessStatusCode();
        var body = await read.Content.ReadAsStringAsync();
        using var json = JsonDocument.Parse(body);
        var request = json.RootElement.GetProperty("history").EnumerateArray().Single(r => r.GetProperty("id").GetGuid() == task.Request);
        Assert.Equal(paid ? "Paid" : "Approved", request.GetProperty("status").GetString());
        Assert.Equal(paid ? 100 : 0, json.RootElement.GetProperty("totalPaid").GetDecimal());
        await File.WriteAllTextAsync(Path.Combine(EvidenceDirectory, filename), body);
    }
}
