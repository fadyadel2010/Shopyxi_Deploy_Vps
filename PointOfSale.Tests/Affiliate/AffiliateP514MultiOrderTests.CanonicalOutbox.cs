using System.Data;
using MassTransit;
using MassTransit.EntityFrameworkCoreIntegration;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PointOfSale.Core.Entities.Affiliate;
using PointOfSale.Core.Enums;
using PointOfSale.Core.Interfaces;
using PointOfSale.Infrastructure.Accounting;
using PointOfSale.Infrastructure.Data;
using PointOfSale.Infrastructure.EventBus;
using PointOfSale.Infrastructure.Services.Affiliate;
using PointOfSale.Infrastructure.Treasury;
using PointOfSale.Tests.Infrastructure;
using Fixture = PointOfSale.Tests.Affiliate.AffiliateP512ManualPayoutTests.Fixture;

namespace PointOfSale.Tests.Affiliate;

public sealed partial class AffiliateP514MultiOrderTests
{
    // The real publisher and EF bus outbox are constructed but the bus/host and
    // delivery service are NEVER started. No external payout collaborator exists.
    private static ServiceProvider CanonicalProvider(Fixture f, params IInterceptor[] interceptors)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<ITenantContext>(new TestTenantContext(f.Store));
        services.AddDbContext<ApplicationDbContext>(o => o.UseNpgsql(f.Connection).AddInterceptors(interceptors));
        services.AddMassTransit(x =>
        {
            x.AddEntityFrameworkOutbox<ApplicationDbContext>(o => { o.UsePostgres(); o.UseBusOutbox(); });
            x.UsingInMemory();
        });
        services.AddScoped<IEventPublisher, MassTransitEventPublisher>();
        return services.BuildServiceProvider();
    }

    private static AffiliateSettlementOrchestrator CanonicalOrchestrator(IServiceProvider services)
    {
        var db = services.GetRequiredService<ApplicationDbContext>();
        return new(db, new AffiliatePayoutService(db, NullLogger<AffiliatePayoutService>.Instance),
            services.GetRequiredService<IEventPublisher>(), new FinancialPostingService(db, NullLogger<FinancialPostingService>.Instance),
            new AccountResolver(db, NullLogger<AccountResolver>.Instance),
            new CashboxLedgerAccountService(db, NullLogger<CashboxLedgerAccountService>.Instance),
            NullLogger<AffiliateSettlementOrchestrator>.Instance, Options.Create(new AffiliateSettlementOptions()));
    }

    [Fact]
    public async Task CanonicalPositive_RealPostingAndLocalBusOutboxCommitTogether_ExactReplayDoesNotRepublish()
    {
        await using var f = await Create(45m, 75m);
        var command = f.Command;
        await using var provider = CanonicalProvider(f);
        await using (var scope = provider.CreateAsyncScope())
        {
            var result = await CanonicalOrchestrator(scope.ServiceProvider)
                .ExecuteAdminManualPayoutAsync(f.Request.Id, f.Store, f.Actor, command);
            Assert.False(result.WasIdempotentSkip);
        }
        await AssertPaid(f, 2);
        long[] sequences;
        await using (var observer = f.Open())
        {
            var messages = await observer.Set<OutboxMessage>().AsNoTracking().OrderBy(m => m.SequenceNumber).ToListAsync();
            Assert.Equal(2, messages.Count);
            Assert.All(messages, m =>
            {
                Assert.Contains("AffiliateCommissionPaidEvent", m.MessageType);
                Assert.Contains(f.Request.Id.ToString(), m.Body, StringComparison.OrdinalIgnoreCase);
                Assert.NotNull(m.OutboxId);
            });
            Assert.Single(await observer.Set<OutboxState>().AsNoTracking().ToListAsync());
            sequences = messages.Select(m => m.SequenceNumber).ToArray();
        }
        await using (var scope = provider.CreateAsyncScope())
            Assert.True((await CanonicalOrchestrator(scope.ServiceProvider)
                .ExecuteAdminManualPayoutAsync(f.Request.Id, f.Store, f.Actor, command)).WasIdempotentSkip);
        await using (var observer = f.Open())
            Assert.Equal(sequences, await observer.Set<OutboxMessage>().OrderBy(m => m.SequenceNumber).Select(m => m.SequenceNumber).ToArrayAsync());
        await AssertPaid(f, 2);
        await Evidence(f, "canonical-real-outbox-committed-replayed");
    }

    [Fact]
    public async Task CanonicalOutboxFailure_RollsBackActualPostingAuditAndLocalMessages_FreshScopeCanRetry()
    {
        await using var f = await Create(40m, 60m);
        var command = f.Command;
        await using (var failed = CanonicalProvider(f, new FailCanonicalOutboxSave()))
        await using (var scope = failed.CreateAsyncScope())
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => CanonicalOrchestrator(scope.ServiceProvider)
                .ExecuteAdminManualPayoutAsync(f.Request.Id, f.Store, f.Actor, command));
            Assert.Equal("P514-CANONICAL-OUTBOX-SAVE-FAILURE", error.Message);
        }
        await f.AssertNoPayout();
        await using (var observer = f.Open())
        {
            Assert.Empty(await observer.Set<OutboxMessage>().ToListAsync());
            Assert.Empty(await observer.Set<OutboxState>().ToListAsync());
            Assert.Empty(await observer.AuditLogs.Where(a => a.ActionType == "AffiliateAdminManualPayout").ToListAsync());
            Assert.Equal(PayoutRequestStatus.Approved, (await observer.AffiliatePayoutRequests.SingleAsync(r => r.Id == f.Request.Id)).Status);
        }
        await using var retry = CanonicalProvider(f);
        await using var fresh = retry.CreateAsyncScope();
        await CanonicalOrchestrator(fresh.ServiceProvider).ExecuteAdminManualPayoutAsync(f.Request.Id, f.Store, f.Actor, command);
        await AssertPaid(f, 2);
        await Evidence(f, "canonical-real-outbox-failure-rolled-back-retried");
    }

    [Fact]
    public async Task CanonicalOrchestratorRefusesCallerOwnedTransaction_AndCallerRollbackRemainsAuthoritative()
    {
        await using var f = await Create(100m);
        var command = f.Command;
        await using var provider = CanonicalProvider(f);
        await using (var scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            await using var caller = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => CanonicalOrchestrator(scope.ServiceProvider)
                .ExecuteAdminManualPayoutAsync(f.Request.Id, f.Store, f.Actor, command));
            Assert.StartsWith("AFFILIATE_PAYOUT_AUTHORITY_CONFLICT", error.Message);
            Assert.Same(caller, db.Database.CurrentTransaction);
            await caller.RollbackAsync();
        }
        await f.AssertNoPayout();
        await using var observer = f.Open();
        Assert.Empty(await observer.Set<OutboxMessage>().ToListAsync());
        Assert.Empty(await observer.Set<OutboxState>().ToListAsync());
    }

    private sealed class FailCanonicalOutboxSave : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData data,
            InterceptionResult<int> result, CancellationToken ct = default)
        {
            if (data.Context!.ChangeTracker.Entries<OutboxMessage>().Any(e => e.State == EntityState.Added))
                throw new InvalidOperationException("P514-CANONICAL-OUTBOX-SAVE-FAILURE");
            return ValueTask.FromResult(result);
        }
    }
}
