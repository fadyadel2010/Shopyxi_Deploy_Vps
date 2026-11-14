#if DEBUG
using System.Data.Common;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using PointOfSale.API.Development;
using PointOfSale.Core.Entities.Accounting;
using PointOfSale.Core.Enums;
using PointOfSale.Core.Interfaces.Accounting;
using PointOfSale.Core.Models.Accounting;
using PointOfSale.Infrastructure.Accounting;
using PointOfSale.Infrastructure.Data;
using PointOfSale.Tests.Integration;

namespace PointOfSale.Tests.Affiliate;

// No driver/host construction. Only its exact private posting decorator is
// supplied an already-guarded disposable PostgreSQL model connection. All money
// is fictional, all SQL belongs to the per-case schema, no external operations.
public sealed class AffiliateDevelopmentPostingRollbackProviderTests
{
    [Theory]
    [InlineData("before-commit")]
    [InlineData("serialization")]
    public async Task ActualDebugDecoratorAbortsAfterCanonicalPostingAndCallerRollbackPreservesEveryFinancialTable(string outcome)
    {
        await using var f = await ProductionCoordinationAuthorityTests.Fixture.CreateAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(40)); var ct = deadline.Token;
        await new TenantAccountSeeder(f.Db, NullLogger<TenantAccountSeeder>.Instance).SeedAsync(f.VendorId);
        Assert.Empty(await f.Db.JournalEntries.AsNoTracking().ToListAsync(ct));
        var witness = new PostedWitness();
        await using var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql(f.ConnectionString).AddInterceptors(witness).Options,
            new PointOfSale.Tests.Infrastructure.TestTenantContext(f.VendorId));
        var before = await db.LedgerAccounts.AsNoTracking().OrderBy(a => a.Id)
            .Select(a => new { a.Id, a.CurrentBalance }).ToListAsync(ct);
        Assert.NotEmpty(before);
        var accounts = await db.LedgerAccounts.ToDictionaryAsync(a => a.AccountCode, ct);
        var source = Guid.NewGuid();
        var request = new PostingRequest
        {
            TenantId = f.VendorId, SourceId = source, SourceType = "APP2DebugRollbackOnly",
            IdempotencyKey = "APP2-DEBUG-ROLLBACK:" + source.ToString("N"),
            Description = "Fictional isolated posting rollback only; no real receipt or transfer", JournalDate = DateTime.UtcNow,
            Lines = [PostingLine.Debit(accounts["1410"].Id, 10m), PostingLine.Credit(accounts["4020"].Id, 10m)]
        };
        var actual = new FinancialPostingService(db, NullLogger<FinancialPostingService>.Instance);
        var decoratorType = typeof(AffiliatePhase52LocalDriver).GetNestedType("PostingBoundary", BindingFlags.NonPublic);
        Assert.NotNull(decoratorType);
        var decorator = Assert.IsAssignableFrom<IFinancialPostingService>(Activator.CreateInstance(decoratorType,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, binder: null, args: [actual, db, outcome], culture: null));
        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            var error = await Record.ExceptionAsync(() => decorator.PostJournalAsync(request, ct));
            Assert.True(witness.PostedObserved, "Actual canonical Posted journal and two SQL ledger lines must precede injection.");
            if (outcome == "serialization")
            {
                var postgres = Assert.IsType<PostgresException>(error);
                Assert.Equal(PostgresErrorCodes.SerializationFailure, postgres.SqlState);
                Assert.Equal("P5.2_TEST_NO_REAL_MONEY controlled serialization abort", postgres.MessageText);
                var aborted = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync("SELECT 1", ct));
                Assert.Equal(PostgresErrorCodes.InFailedSqlTransaction, aborted.SqlState);
            }
            else
            {
                Assert.Equal("P5.2_TEST_NO_REAL_MONEY simulated posting timeout BEFORE COMMIT", Assert.IsType<TimeoutException>(error).Message);
                Assert.Equal(1, await db.JournalEntries.CountAsync(ct));
                Assert.Equal(2, await db.LedgerEntries.CountAsync(ct));
            }
            // Even before rollback, independently committed visibility excludes
            // the fictional journal. Do not mistake tracked entities for commit.
            await using var observer = f.ScopedDb(f.VendorId);
            Assert.Empty(await observer.JournalEntries.AsNoTracking().ToListAsync(ct));
            Assert.Empty(await observer.LedgerEntries.AsNoTracking().ToListAsync(ct));
            await tx.RollbackAsync(ct);
        }
        await using var fresh = f.ScopedDb(f.VendorId);
        Assert.Empty(await fresh.JournalEntries.ToListAsync(ct)); Assert.Empty(await fresh.LedgerEntries.ToListAsync(ct));
        Assert.Empty(await fresh.AccountingOutboxMessages.ToListAsync(ct)); Assert.Empty(await fresh.OutboxMessages.ToListAsync(ct));
        Assert.Empty(await fresh.PaymentSettlementBatches.ToListAsync(ct)); Assert.Empty(await fresh.CarrierRemittances.ToListAsync(ct));
        Assert.Empty(await fresh.AffiliateBalanceLedger.ToListAsync(ct)); Assert.Empty(await fresh.AffiliatePayoutRequests.ToListAsync(ct));
        Assert.Equal(before, await fresh.LedgerAccounts.OrderBy(a => a.Id).Select(a => new { a.Id, a.CurrentBalance }).ToListAsync(ct));
        Assert.Null(db.Database.CurrentTransaction);
    }

    private sealed class PostedWitness : SaveChangesInterceptor
    {
        internal bool PostedObserved { get; private set; }
        public override async ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData data, int result, CancellationToken ct = default)
        {
            var db = Assert.IsType<ApplicationDbContext>(data.Context);
            if (db.ChangeTracker.Entries<JournalEntry>().Any(e => e.Entity.Status == JournalStatus.Posted))
            {
                Assert.NotNull(db.Database.CurrentTransaction);
                Assert.Equal(1, await db.JournalEntries.CountAsync(ct));
                Assert.Equal(2, await db.LedgerEntries.CountAsync(ct));
                PostedObserved = true;
            }
            return result;
        }
    }
}
#endif
