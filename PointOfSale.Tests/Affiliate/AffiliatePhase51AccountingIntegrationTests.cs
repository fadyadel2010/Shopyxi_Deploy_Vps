using MassTransit;
using MassTransit.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using PointOfSale.Core.Entities.Accounting;
using PointOfSale.Core.Entities.Affiliate;
using PointOfSale.Core.Enums;
using PointOfSale.Core.Events.Affiliate;
using PointOfSale.Core.Interfaces;
using PointOfSale.Core.Interfaces.Accounting;
using PointOfSale.Core.Interfaces.Affiliate;
using PointOfSale.Core.Interfaces.Treasury;
using PointOfSale.Core.Models.Accounting;
using PointOfSale.Infrastructure.Accounting;
using PointOfSale.Infrastructure.Consumers.Affiliate;
using PointOfSale.Infrastructure.Data;
using PointOfSale.Infrastructure.EventBus;
using PointOfSale.Infrastructure.MultiTenancy;
using PointOfSale.Infrastructure.Instrumentation;
using PointOfSale.Infrastructure.Services.Affiliate;
using PointOfSale.Infrastructure.Treasury;
using Xunit.Abstractions;
using BusMessage = MassTransit.EntityFrameworkCoreIntegration.OutboxMessage;

namespace PointOfSale.Tests.Affiliate;

/// <summary>Disposable PostgreSQL only. Real SQL posting/resolvers/EF bus outbox;
/// in-process transport only. No provider, HTTP client, wallet or Treasury transfer service.</summary>
public sealed class AffiliatePhase51AccountingIntegrationTests(ITestOutputHelper output)
{
    private const string Provenance = "P5_SIMULATED_NO_MONEY";

    private sealed class Runtime : IAsyncDisposable
    {
        internal readonly AffiliatePhase5WithdrawalTests.Fixture F = new();
        internal ServiceProvider Provider = null!;
        internal Account Cash = null!, Payable = null!, Expense = null!;
        internal AffiliateCommissionLedger Earned = null!;
        internal Guid Request;

        internal async Task Initialize(string reason = "valid")
        {
            await F.Initialize(); // Queries actual open connection before any fixture mutation.
            Cash = Account.Create(F.Store, AccountCodes.CashboxMain, Provenance, AccountType.Asset, preventNegative: true);
            Cash.SetOpeningBalance(1000);
            Payable = Account.Create(F.Store, AccountCodes.AffiliatePayable, Provenance, AccountType.Liability, preventNegative: true);
            Expense = Account.Create(F.Store, AccountCodes.AffiliateMarketingExpense, Provenance, AccountType.Expense);
            F.Db.AddRange(Cash, Payable, Expense);
            Earned = F.Commission(reason == "underfunded" ? 100 : 150, reason == "synthetic");
            if (reason == "valid") F.Commission(900, true);
            if (reason == "lineage")
            {
                var other = F.Commission(10);
                var approved = F.Db.ChangeTracker.Entries<AffiliateCommissionLedger>().Single(e =>
                    e.Entity.OrderItemId == Earned.OrderItemId && e.Entity.EntryType == CommissionEntryType.Approved).Entity;
                typeof(AffiliateCommissionLedger).GetProperty("OriginalEntryId")!.SetValue(approved, other.Id);
            }
            foreach (var order in F.Db.ChangeTracker.Entries<PointOfSale.Core.Entities.Order>())
                order.Entity.OrderNumber = $"{Provenance}_{order.Entity.Id:N}";
            await F.Db.SaveChangesAsync();
            if (reason == "valid")
            {
                // Same canonical Account.Create fixture pattern as AffiliateAccountingBridgeTests,
                // but a real earned journal, not a header-only readiness placeholder.
                await Posting(F.Db).PostJournalAsync(new PostingRequest
                {
                    TenantId = F.Store, JournalDate = DateTime.UtcNow, SourceType = "AffiliateCommissionEarned",
                    SourceId = Earned.OrderId, SourceReference = Provenance, Description = Provenance,
                    IdempotencyKey = $"AFFILIATE_COMM_EARNED:{F.Store}:{Earned.OrderId}",
                    Lines = [PostingLine.Debit(Expense.Id, 150, Provenance), PostingLine.Credit(Payable.Id, 150, Provenance)]
                });
                var request = await F.Service.RequestPayoutAsync(F.Account.Id, 150, F.Store);
                Request = request.Id;
                await F.Service.ApprovePayoutAsync(Request, F.Store, F.Admin, Provenance);
            }
            else
            {
                var request = F.Reservation(150); request.Approve(F.Admin, Provenance);
                F.Db.Add(request); await F.Db.SaveChangesAsync(); Request = request.Id;
            }
            F.Db.Add(AffiliatePayoutTransferEvidence.Create(F.Store, "AFFILIATE_PAYOUT", Reference, 150, "EGP",
                DateTime.UtcNow, Provenance, new string('0', 64)));
            await F.Db.SaveChangesAsync();

            var tenant = new TenantContext(); tenant.SetTenantId(F.Store);
            var services = new ServiceCollection(); services.AddLogging();
            services.AddSingleton<ITenantContext>(tenant);
            services.AddScoped(_ => F.Scope.CreateDbContext());
            services.AddScoped<IFinancialPostingService, FinancialPostingService>();
            services.AddScoped<IAccountResolver, AccountResolver>();
            services.AddScoped<ICashboxLedgerAccountService, CashboxLedgerAccountService>();
            services.AddScoped<IEventPublisher, MassTransitEventPublisher>();
            services.AddSingleton<AffiliateTelemetry>();
            services.AddMassTransitTestHarness(bus =>
            {
                bus.SetTestTimeouts(testTimeout: TimeSpan.FromSeconds(30), testInactivityTimeout: TimeSpan.FromSeconds(10));
                // Only this SQL-only accounting consumer is registered. No projections, providers,
                // external endpoints, notifications, production startup or RabbitMQ connection.
                bus.AddConsumer<AffiliateCommissionPaidAccountingConsumer>();
                bus.AddEntityFrameworkOutbox<ApplicationDbContext>(o =>
                {
                    o.UsePostgres(); o.QueryDelay = TimeSpan.FromMilliseconds(50); o.UseBusOutbox();
                });
                bus.UsingInMemory((context, cfg) => cfg.ConfigureEndpoints(context));
            });
            Provider = services.BuildServiceProvider(validateScopes: true);
        }
        internal string Reference => $"{Provenance}_{Request:N}";
        internal static FinancialPostingService Posting(ApplicationDbContext db) => new(db, NullLogger<FinancialPostingService>.Instance);
        internal async Task Process(bool failBeforeCommit = false, bool postCommitTimeout = false)
        {
            await using var scope = Provider.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            await Verify(db);
            var posting = scope.ServiceProvider.GetRequiredService<IFinancialPostingService>();
            if (failBeforeCommit)
            {
                var actualPosting = posting;
                // Failure AFTER the actual posting SaveChanges, BEFORE outer transaction commit.
                var boundary = new Mock<IFinancialPostingService>(MockBehavior.Strict);
                boundary.Setup(p => p.PostJournalAsync(It.IsAny<PostingRequest>(), It.IsAny<CancellationToken>()))
                    .Returns(async (PostingRequest p, CancellationToken ct) =>
                    { await actualPosting.PostJournalAsync(p, ct); throw new TimeoutException("P5_SIMULATED_NO_MONEY_BEFORE_COMMIT"); });
                posting = boundary.Object;
            }
            var orchestrator = new AffiliateSettlementOrchestrator(db, AffiliatePhase5WithdrawalTests.Fixture.ServiceFor(db),
                scope.ServiceProvider.GetRequiredService<IEventPublisher>(), posting,
                scope.ServiceProvider.GetRequiredService<IAccountResolver>(), scope.ServiceProvider.GetRequiredService<ICashboxLedgerAccountService>(),
                NullLogger<AffiliateSettlementOrchestrator>.Instance,
                Options.Create(new AffiliateSettlementOptions { RequireVerifiedExternalPayoutEvidence = false }));
            await orchestrator.ExecuteAffiliatePayoutAsync(F.Account.Id, F.Store, 150, SettlementSource.WithdrawalEngine,
                Reference, payoutRequestId: Request, paidByUserId: F.Admin, adminNotes: Provenance);
            if (postCommitTimeout) throw new TimeoutException("P5_SIMULATED_NO_MONEY_AFTER_COMMIT");
        }
        internal async Task Verify(ApplicationDbContext db)
        {
            await db.Database.OpenConnectionAsync();
            await using var cmd = db.Database.GetDbConnection().CreateCommand();
            cmd.CommandText = "select current_database(), inet_server_addr()::text, pg_backend_pid()";
            await using var reader = await cmd.ExecuteReaderAsync(); Assert.True(await reader.ReadAsync());
            Assert.Equal(F.Db.Database.GetDbConnection().Database, reader.GetString(0));
            Assert.StartsWith("pointofsale_tests_", reader.GetString(0));
            Assert.NotEqual("PosEcommerceDb_Baseline", reader.GetString(0));
        }
        internal async Task AssertState(bool paid, bool outboxDelivered = false)
        {
            await using var db = F.Scope.CreateDbContext();
            Assert.Equal(paid ? PayoutRequestStatus.Paid : PayoutRequestStatus.Approved, (await db.AffiliatePayoutRequests.SingleAsync()).Status);
            Assert.Equal(paid ? 1 : 0, await db.AffiliateBalanceLedger.CountAsync());
            Assert.Equal(paid ? 1 : 0, await db.AffiliatePayoutTransferAllocations.CountAsync());
            Assert.Equal(paid ? 1 : 0, await db.AffiliateCommissionLedger.CountAsync(e => e.EntryType == CommissionEntryType.Paid));
            Assert.Equal(paid ? 2 : 1, await db.JournalEntries.IgnoreQueryFilters().CountAsync());
            Assert.Equal(paid ? 4 : 2, await db.LedgerEntries.IgnoreQueryFilters().CountAsync());
            Assert.Equal(paid ? 850 : 1000, (await db.LedgerAccounts.IgnoreQueryFilters().SingleAsync(a => a.Id == Cash.Id)).CurrentBalance);
            Assert.Equal(paid ? 0 : 150, (await db.LedgerAccounts.IgnoreQueryFilters().SingleAsync(a => a.Id == Payable.Id)).CurrentBalance);
            Assert.Equal(paid && !outboxDelivered ? 1 : 0, await db.Set<BusMessage>().CountAsync());
            Assert.Empty(await db.AccountingOutboxMessages.ToListAsync()); // not the affiliate event's outbox model
            Assert.Empty(await db.Set<PointOfSale.Core.Entities.TreasuryTransaction>().IgnoreQueryFilters().ToListAsync());
            Assert.Empty(await db.Set<PointOfSale.Core.Entities.CashboxTransaction>().IgnoreQueryFilters().ToListAsync());
            if (paid)
            {
                Assert.Equal(-150, (await db.AffiliateBalanceLedger.SingleAsync()).Amount);
                Assert.Equal(Earned.OrderId, (await db.AffiliateCommissionLedger.SingleAsync(e => e.EntryType == CommissionEntryType.Paid)).OrderId);
                var balance = await AffiliatePhase5WithdrawalTests.Fixture.ServiceFor(db).GetAvailableBalanceAsync(F.Account.Id, F.Store);
                Assert.Equal(0, balance.RawAvailable); Assert.Equal(900, balance.SyntheticApprovedExcluded);
            }
        }
        public async ValueTask DisposeAsync()
        { if (Provider != null) await Provider.DisposeAsync(); await F.DisposeAsync(); }
    }

    [Fact]
    public async Task CanonicalPosting_PersistedBusOutbox_PostCommitTimeoutAndParallelReplays_ExactlyOnce()
    {
        await using var r = new Runtime(); await r.Initialize();
        output.WriteLine($"{r.F.Identity}; apiProcess={Environment.ProcessId}; request={r.Request}; affiliate={r.F.Account.Id}; store={r.F.Store}; {Provenance}");
        await Assert.ThrowsAsync<TimeoutException>(() => r.Process(postCommitTimeout: true));
        await r.AssertState(true);
        await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => r.Process()));
        await r.AssertState(true);
        await using var db = r.F.Scope.CreateDbContext();
        var message = await db.Set<BusMessage>().SingleAsync();
        Assert.Contains(Provenance, message.Body);
        output.WriteLine($"Persisted MassTransit outbox sequence={message.SequenceNumber}; message={message.MessageId}; body={message.Body}");

        // Start only this isolated provider's hosted services: EF normal dispatcher + in-memory bus.
        var hosted = r.Provider.GetServices<IHostedService>().ToList();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        try
        {
            var harness = r.Provider.GetRequiredService<ITestHarness>();
            await harness.Start();
            output.WriteLine($"Isolated hosted services: {string.Join(",", hosted.Select(h => h.GetType().FullName))}");
            // Bind the in-memory receive endpoint BEFORE the persisted outbox can dispatch.
            foreach (var service in hosted) await service.StartAsync(timeout.Token);
            Assert.True(await harness.Consumed.Any<AffiliateCommissionPaidEvent>(timeout.Token));
            var limit = DateTime.UtcNow.AddSeconds(10);
            while (await db.Set<BusMessage>().AnyAsync() && DateTime.UtcNow < limit) await Task.Delay(50, timeout.Token);
            await r.AssertState(true, true);
            // Real consumer replay (legacy accounting fallback is normally disabled in production).
            // The normal consumer's posting request must match the synchronous canonical journal.
            var evt = new AffiliateCommissionPaidEvent { TenantId = r.F.Store, StoreTenantId = r.F.Store,
                AffiliateAccountId = r.F.Account.Id, OrderId = r.Earned.OrderId, SettlementReferenceId = r.Request,
                TotalPaid = 150, Currency = "EGP", OrderNumber = Provenance, CorrelationId = Provenance };
            await Task.WhenAll(Enumerable.Range(0, 6).Select(async _ =>
            {
                await using var scope = r.Provider.CreateAsyncScope();
                var consumer = ActivatorUtilities.CreateInstance<AffiliateCommissionPaidAccountingConsumer>(scope.ServiceProvider);
                var context = new Mock<ConsumeContext<AffiliateCommissionPaidEvent>>(); context.SetupGet(c => c.Message).Returns(evt);
                await consumer.Consume(context.Object);
            }));
            await r.AssertState(true, true);
            output.WriteLine("Normal EF outbox dispatcher -> in-memory transport -> actual SQL accounting consumer; six concurrent consumer replays; 2 journals/4 lines total, cash=850 payable=0, one debit/Paid/allocation.");
        }
        finally
        { foreach (var service in hosted.AsEnumerable().Reverse()) await service.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task CanonicalPosting_PreCommitFailure_RollsBackActualJournalLinesBalancesAndLifecycle_RetryOnce()
    {
        await using var r = new Runtime(); await r.Initialize();
        output.WriteLine($"{r.F.Identity}; process={Environment.ProcessId}; request={r.Request}; {Provenance}");
        await Assert.ThrowsAsync<TimeoutException>(() => r.Process(failBeforeCommit: true));
        await r.AssertState(false);
        await r.Process(); await r.Process(); await r.AssertState(true);
    }

    [Fact]
    public async Task FirstExecutionConcurrency_RealPostingAndOutbox_OneFinancialEffect()
    {
        await using var r = new Runtime(); await r.Initialize();
        output.WriteLine($"{r.F.Identity}; process={Environment.ProcessId}; request={r.Request}; {Provenance}");
        var outcomes = await Task.WhenAll(Enumerable.Range(0, 2).Select(async _ =>
        {
            try { await r.Process(); return "committed-or-idempotent"; }
            catch (Exception e) when (ContainsSerializationFailure(e)) { return "SQL40001-rolled-back"; }
        }));
        Assert.Contains("committed-or-idempotent", outcomes);
        output.WriteLine($"First-execution outcomes: {string.Join(",", outcomes)}; caller retries with a fresh scope after SQL40001.");
        await r.AssertState(true);
        await r.Process(); // Explicit caller replay; do not claim transparent built-in retry success.
        await r.AssertState(true);
    }

    private static bool ContainsSerializationFailure(Exception e) =>
        e is Npgsql.PostgresException { SqlState: "40001" } || e.InnerException != null && ContainsSerializationFailure(e.InnerException);

    [Theory]
    [InlineData("synthetic")] [InlineData("lineage")] [InlineData("underfunded")]
    public async Task IneligibleProcessing_StopsBeforeRealPostingAndOutbox(string reason)
    {
        await using var r = new Runtime(); await r.Initialize(reason);
        output.WriteLine($"{r.F.Identity}; process={Environment.ProcessId}; request={r.Request}; reason={reason}; {Provenance}");
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => r.Process());
        await using var db = r.F.Scope.CreateDbContext();
        Assert.Empty(await db.JournalEntries.IgnoreQueryFilters().ToListAsync());
        Assert.Empty(await db.LedgerEntries.IgnoreQueryFilters().ToListAsync());
        Assert.Empty(await db.Set<BusMessage>().ToListAsync());
        Assert.Empty(await db.AccountingOutboxMessages.ToListAsync());
        Assert.Empty(await db.AffiliateBalanceLedger.ToListAsync());
        Assert.Empty(await db.AffiliatePayoutTransferAllocations.ToListAsync());
        Assert.False(await db.AffiliateCommissionLedger.AnyAsync(e => e.EntryType == CommissionEntryType.Paid));
        Assert.Equal(PayoutRequestStatus.Approved, (await db.AffiliatePayoutRequests.SingleAsync()).Status);
    }
}
