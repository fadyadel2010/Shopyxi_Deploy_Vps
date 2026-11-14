using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using PointOfSale.Core.Entities.Accounting;
using PointOfSale.Core.Enums;
using PointOfSale.Core.Models.Accounting;
using PointOfSale.Infrastructure.Accounting;
using PointOfSale.Infrastructure.Data;

namespace PointOfSale.Tests;

public sealed class JournalActorPersistenceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RealLedger_PersistsActorAndReversalActor_WithoutBackfillingReplay(bool legacy)
    {
        using var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var tenant = Guid.NewGuid(); var owner = Guid.NewGuid(); var reviewer = Guid.NewGuid();
        var debit = Account.Create(tenant, "1010", "Actor cash", AccountType.Asset);
        var credit = Account.Create(tenant, "4010", "Actor revenue", AccountType.Revenue);
        db.LedgerAccounts.AddRange(debit, credit); await db.SaveChangesAsync();
        var actor = new AccountingActorContext();
        var service = new FinancialPostingService(db, NullLogger<FinancialPostingService>.Instance, actor: actor);
        PostingRequest Request() => new()
        {
            TenantId = tenant, JournalDate = DateTime.UtcNow.Date, SourceType = "ActorRegression",
            SourceId = tenant, Description = "Actor persistence regression", IdempotencyKey = $"ACTOR:{tenant}",
            Lines = [PostingLine.Debit(debit.Id, 10m), PostingLine.Credit(credit.Id, 10m)]
        };
        JournalEntry journal;
        using (actor.UseActor(tenant, legacy ? null : owner)) journal = await service.PostJournalAsync(Request());
        Assert.Equal(legacy ? null : owner, journal.CreatedByUserId);
        using (actor.UseActor(tenant, reviewer))
        {
            var replay = await service.PostJournalAsync(Request());
            Assert.Equal(journal.Id, replay.Id);
            Assert.Equal(legacy ? null : owner, replay.CreatedByUserId);
            var reversal = await service.ReverseJournalAsync(tenant, journal.Id, "Canonical test reversal");
            Assert.Equal(reviewer, reversal.CreatedByUserId);
            Assert.Equal(journal.Id, reversal.ReversesJournalId);
        }
        var rows = await db.JournalEntries.IgnoreQueryFilters().ToListAsync();
        Assert.Equal(2, rows.Count);
        Assert.All(rows, j => Assert.True(j.IsBalanced));
        Assert.Equal(0m, (await db.LedgerAccounts.IgnoreQueryFilters().SingleAsync(a => a.Id == debit.Id)).CurrentBalance);
        Assert.Equal(0m, (await db.LedgerAccounts.IgnoreQueryFilters().SingleAsync(a => a.Id == credit.Id)).CurrentBalance);
    }

    [Fact]
    public async Task RealLedger_RejectsCrossTenantActorBeforeWriting()
    {
        using var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var actor = new AccountingActorContext();
        using var scope = actor.UseActor(Guid.NewGuid(), Guid.NewGuid());
        var service = new FinancialPostingService(db, NullLogger<FinancialPostingService>.Instance, actor: actor);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.PostJournalAsync(new PostingRequest { TenantId = Guid.NewGuid() }));
        Assert.Empty(await db.JournalEntries.IgnoreQueryFilters().ToListAsync());
    }
}
