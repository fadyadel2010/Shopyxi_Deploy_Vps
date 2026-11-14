using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using PointOfSale.Core.Entities;
using PointOfSale.Infrastructure.Data;
using PointOfSale.Tests.Infrastructure;

namespace PointOfSale.Tests.Affiliate;

public sealed partial class AffiliateP514MultiOrderTests
{
    [Theory]
    [InlineData("missing")]
    [InlineData("foreign")]
    public async Task CanonicalManualPayoutCannotBorrowAnotherUnitOfWorksTenantAuthority(string scope)
    {
        await using var f = await Create(75m, 125m);
        var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseNpgsql(f.Connection).Options;
        await using var db = scope == "missing" ? new ApplicationDbContext(options) : new ApplicationDbContext(options, new TestTenantContext(Guid.NewGuid()));
        var before = await f.Db.JournalEntries.IgnoreQueryFilters().CountAsync();
        await Assert.ThrowsAsync<System.Security.SecurityException>(() => f.Orchestrator(db).ExecuteAdminManualPayoutAsync(f.Request.Id, f.Store, f.Actor, f.Command));
        Assert.Equal(before, await f.Db.JournalEntries.IgnoreQueryFilters().CountAsync());
        Assert.Empty(await f.Db.AffiliateManualPayouts.IgnoreQueryFilters().ToListAsync());
        Assert.Null(db.Database.CurrentTransaction);
    }

    [Fact]
    public async Task CanonicalManualPayoutRetainsActorOwnershipThroughAuditPostingAndCommit()
    {
        // Explicitly uses the guarded P514 factory, never Fixture.Create or the
        // shared-primary outer driver. All ledger data is fictional and local.
        await using var f = await Create(75m, 125m);
        var pause = new PauseManualAudit();
        await using var db = f.Open(pause);
        var pending = f.Orchestrator(db).ExecuteAdminManualPayoutAsync(f.Request.Id, f.Store, f.Actor, f.Command);
        try
        {
            await pause.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
            await using var competitor = new NpgsqlConnection(f.Connection); await competitor.OpenAsync();
            await using var tx = await competitor.BeginTransactionAsync();
            await using var claim = new NpgsqlCommand("SELECT \"Id\" FROM \"Users\" WHERE \"Id\"=@actor FOR UPDATE NOWAIT", competitor, tx);
            claim.Parameters.AddWithValue("actor", f.Actor);
            var error = await Assert.ThrowsAsync<PostgresException>(() => claim.ExecuteScalarAsync());
            Assert.Equal(PostgresErrorCodes.LockNotAvailable, error.SqlState);
            await tx.RollbackAsync();
        }
        finally
        {
            pause.Release.TrySetResult();
            try { await pending; } catch { /* Drain owned work; retain any original assertion failure. */ }
        }
        Assert.False((await pending).WasIdempotentSkip);
        await AssertPaid(f, 2);
        Assert.Null(db.Database.CurrentTransaction);
    }

    private sealed class PauseManualAudit : SaveChangesInterceptor
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData data,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (data.Context!.ChangeTracker.Entries<AuditLog>().Any(e => e.State == EntityState.Added && e.Entity.ActionType == "AffiliateAdminManualPayout"))
            {
                Assert.NotNull(data.Context.Database.CurrentTransaction); Entered.TrySetResult();
                await Release.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
            }
            return result;
        }
    }
}
