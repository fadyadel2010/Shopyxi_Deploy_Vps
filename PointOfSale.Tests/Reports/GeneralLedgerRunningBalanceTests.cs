using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using PointOfSale.Core.Entities;
using PointOfSale.Core.Entities.Accounting;
using PointOfSale.Core.Enums;
using PointOfSale.Core.Models.Accounting.Reports;
using PointOfSale.Infrastructure.Accounting;
using PointOfSale.Infrastructure.Data;
using PointOfSale.Tests.Infrastructure;

namespace PointOfSale.Tests.Reports;

public sealed class GeneralLedgerRunningBalanceTests : IDisposable
{
    private static readonly DateTime Day = new(2026, 11, 13, 0, 0, 0, DateTimeKind.Utc);
    private readonly Guid _tenant = Guid.NewGuid();
    private readonly ApplicationDbContext _db;
    private readonly DbContext _fixture;
    private readonly Account _cash;
    private readonly Account _payable;

    public GeneralLedgerRunningBalanceTests()
    {
        var name = Guid.NewGuid().ToString();
        var store = new InMemoryDatabaseRoot();
        _db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(name, store).Options, new TestTenantContext(_tenant));
        // Import immutable read-path fixtures into a local-only in-memory store.
        // The real ApplicationDbContext below only reads them; do not invoke or
        // weaken its relational posting/period authority to manufacture test data.
        _fixture = new DbContext(new DbContextOptionsBuilder()
            .UseInMemoryDatabase(name, store).UseModel(_db.Model).Options);
        _cash = Account.Create(_tenant, "1010", "EGP cash", AccountType.Asset);
        _payable = Account.Create(_tenant, "2010", "EGP payable", AccountType.Liability);
        _fixture.Add(new Tenant { Id = _tenant, Name = "Local immutable ledger fixture", Currency = "EGP" });
        _fixture.AddRange(_cash, _payable);
        _fixture.SaveChanges();
    }

    // Exact amounts, line numbers, snapshots and posting timestamps from preserved
    // readonly-before.jsonl. No Production writes or historical-fixture replacement.
    private async Task SeedProductionFixture()
    {
        var rows = new[] {
            (2, "2026-11-12T13:29:47.977333Z", 1, 11m, 0m, 11m),
            (4, "2026-11-12T13:35:16.793294Z", 3, 0m, 11m, 0m),
            (13, "2026-11-13T21:48:01.335879Z", 1, 10.30m, 0m, 10.30m),
            (14, "2026-11-13T21:50:06.943245Z", 2, 0m, .30m, 10m),
            (15, "2026-11-13T21:51:34.343008Z", 1, .30m, 0m, 10.30m),
            (17, "2026-11-13T22:00:09.571359Z", 1, 10.05m, 0m, 20.35m),
            (19, "2026-11-13T22:02:55.403745Z", 2, 0m, 10.05m, 10.30m),
            (22, "2026-11-13T22:55:22.840206Z", 2, 0m, .01m, 10.29m),
            (23, "2026-11-13T22:55:58.555699Z", 2, .01m, 0m, 10.30m),
            (24, "2026-11-13T22:56:32.874410Z", 1, 0m, 10.30m, 0m)
        };
        var journals = new Dictionary<int, JournalEntry>();
        foreach (var (number, timestamp, line, debit, credit, snapshot) in rows)
        {
            var posted = DateTime.Parse(timestamp, null, System.Globalization.DateTimeStyles.AdjustToUniversal);
            journals[number] = Add(number, posted.Date, posted, _cash, debit, credit, snapshot, line);
        }
        foreach (var (original, reversal) in new[] { (14, 15), (22, 23), (13, 24) })
        {
            journals[original].MarkReversed(journals[reversal].Id, "Retained legitimate reversal");
            journals[reversal].MarkAsReversal(journals[original].Id);
            journals[reversal].SetSourceType("Reversal");
        }
        await SaveFixture();
    }

    [Theory]
    [InlineData(15, 10.30)]
    [InlineData(17, 20.35)]
    [InlineData(24, 0.00)]
    [InlineData(13, 10.30)]
    [InlineData(19, 10.30)]
    [InlineData(22, 10.29)]
    [InlineData(23, 10.30)]
    [InlineData(14, 10.00)]
    public async Task PreservedEightMismatches_FollowPostingOrderAndCumulativeMovement(int number, decimal expected)
    {
        await SeedProductionFixture();
        var before = await _db.LedgerEntries.AsNoTracking().Select(x => new { x.Id, x.BalanceAfter, x.LineNumber }).ToListAsync();
        var report = await Report(_cash, Day.AddDays(-12), Day.AddDays(1));
        Assert.Equal(new[] { 2, 4, 13, 14, 15, 17, 19, 22, 23, 24 }.Select(Number), report.Entries.Select(x => x.JournalNumber));
        Assert.Equal(expected, Assert.Single(report.Entries, x => x.JournalNumber == Number(number)).BalanceAfter);
        AssertChain(report, creditNormal: false);
        Assert.Equal(31.66m, report.TotalDebits);
        Assert.Equal(31.66m, report.TotalCredits);
        Assert.Equal(0m, report.OpeningBalance);
        Assert.Equal(0m, report.ClosingBalance);
        Assert.Equal(before, await _db.LedgerEntries.AsNoTracking().Select(x => new { x.Id, x.BalanceAfter, x.LineNumber }).ToListAsync());
        Assert.False(_db.ChangeTracker.HasChanges());
    }

    [Fact]
    public async Task ExplicitArithmetic_Opening100_Debit50_Credit20_Is100150130()
    {
        Add(1, Day.AddDays(-1), Day, _cash, 100m, 0m, 100m, 1);
        Add(2, Day, Day.AddHours(1), _cash, 50m, 0m, 150m, 2);
        Add(3, Day, Day.AddHours(2), _cash, 0m, 20m, 130m, 1);
        await SaveFixture();
        var report = await Report(_cash, Day, Day);
        Assert.Equal(new[] { 100m, 150m, 130m }, new[] { report.OpeningBalance }.Concat(report.Entries.Select(x => x.BalanceAfter)));
        AssertChain(report, false);
    }

    [Fact]
    public async Task BusinessDatePrecedesPostingTime_BackdatedSnapshotsAreNotPresentationBalances()
    {
        Add(1, Day, Day.AddHours(1), _cash, 50m, 0m, 50m, 1);
        Add(2, Day.AddDays(-1), Day.AddHours(2), _cash, 100m, 0m, 150m, 1);
        Add(3, Day, Day.AddHours(3), _cash, 0m, 20m, 130m, 1);
        await SaveFixture();
        var report = await Report(_cash, Day, Day);
        Assert.Equal(100m, report.OpeningBalance);
        Assert.Equal(new[] { 150m, 130m }, report.Entries.Select(x => x.BalanceAfter));
        AssertChain(report, false);
        var all = await Report(_cash, Day.AddDays(-1), Day);
        Assert.Equal(new[] { 2, 1, 3 }.Select(Number), all.Entries.Select(x => x.JournalNumber));
        AssertChain(all, false);
    }

    [Fact]
    public async Task EqualPostingTimestamp_UsesJournalNumberThenWithinJournalLine_NotInsertionOrCreatedAt()
    {
        var second = Add(2, Day, Day, _cash, 0m, 20m, 30m, 1);
        var first = Add(1, Day, Day, _cash, 40m, 0m, 50m, 3);
        AddLine(first, _cash, 10m, 0m, 10m, 2);
        second.CreatedAt = Day.AddDays(-10);
        first.CreatedAt = Day.AddDays(10);
        await SaveFixture();
        var report = await Report(_cash, Day, Day);
        Assert.Equal(new[] { Number(1), Number(1), Number(2) }, report.Entries.Select(x => x.JournalNumber));
        Assert.Equal(new[] { 10m, 50m, 30m }, report.Entries.Select(x => x.BalanceAfter));
        AssertChain(report, false);
        Assert.Equal(report.Entries.Select(x => x.BalanceAfter), (await Report(_cash, Day, Day)).Entries.Select(x => x.BalanceAfter));
    }

    [Fact]
    public async Task CreditNormalAndDebitNormalAccounts_HaveIndependentChains_AndKeepFourDecimalPrecision()
    {
        Add(1, Day.AddDays(-1), Day, _cash, 100.0001m, 0m, 100.0001m, 1);
        Add(2, Day.AddDays(-1), Day, _payable, 0m, 100.0001m, 100.0001m, 1);
        Add(3, Day, Day.AddHours(1), _cash, .0051m, 0m, 100.0052m, 2);
        Add(4, Day, Day.AddHours(2), _cash, 0m, .0002m, 100.0050m, 1);
        Add(5, Day, Day.AddHours(1), _payable, 0m, .0051m, 100.0052m, 2);
        Add(6, Day, Day.AddHours(2), _payable, .0002m, 0m, 100.0050m, 1);
        await SaveFixture();
        var cash = await Report(_cash, Day, Day);
        var payable = await Report(_payable, Day, Day);
        Assert.Equal(new[] { 100.0052m, 100.0050m }, cash.Entries.Select(x => x.BalanceAfter));
        Assert.Equal(cash.Entries.Select(x => x.BalanceAfter), payable.Entries.Select(x => x.BalanceAfter));
        AssertChain(cash, false);
        AssertChain(payable, true);
    }

    [Fact]
    public async Task DateAndAccountAndTenantFilters_IncludeEarlierOpening_ExcludeFutureAndForeignActivity()
    {
        Add(1, Day.AddDays(-2), Day, _cash, 100m, 0m, 100m, 1);
        Add(2, Day, Day.AddHours(1), _cash, 50m, 0m, 150m, 2);
        Add(3, Day.AddDays(1), Day.AddHours(2), _cash, 0m, 20m, 130m, 1);
        Add(4, Day, Day, _payable, 0m, 77m, 77m, 1);
        var foreign = Account.Create(Guid.NewGuid(), "1010", "Foreign", AccountType.Asset);
        _fixture.Add(foreign);
        Add(5, Day, Day, foreign, 999m, 0m, 999m, 1);
        await SaveFixture();
        var report = await Report(_cash, Day, Day);
        Assert.Single(report.Entries);
        Assert.Equal(100m, report.OpeningBalance);
        Assert.Equal(150m, report.ClosingBalance);
        var next = await Report(_cash, Day.AddDays(1), Day.AddDays(1));
        Assert.Equal(report.ClosingBalance, next.OpeningBalance);
        Assert.Equal(130m, next.ClosingBalance);
        var empty = await Report(_cash, Day.AddDays(2), Day.AddDays(3));
        Assert.Empty(empty.Entries);
        Assert.Equal(130m, empty.OpeningBalance);
        Assert.Equal(empty.OpeningBalance, empty.ClosingBalance);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Report(foreign, Day, Day));
    }

    [Fact]
    public async Task OpeningUsesStrictBeforeStart_NotSubtractOneDay_AndRespectsInclusiveEnd()
    {
        Add(1, Day, Day.AddHours(1), _cash, 100m, 0m, 100m, 1);
        Add(2, Day.AddDays(1), Day.AddDays(1).AddHours(1), _cash, 50m, 0m, 150m, 1);
        await SaveFixture();
        var report = await Report(_cash, Day.AddHours(12), Day.AddDays(1));
        Assert.Equal(100m, report.OpeningBalance);
        Assert.Equal(150m, Assert.Single(report.Entries).BalanceAfter);
        AssertChain(report, false);
    }

    [Fact]
    public async Task CompleteUnpaginatedResponse_KeepsBalanceAcrossConsumerPageBoundaries_AndReversals()
    {
        await SeedProductionFixture();
        var report = await Report(_cash, Day.AddDays(-1), Day);
        // The actual API/UI has no paging arguments: all balances are computed before
        // any consumer slices. Test each possible page boundary, not a fictitious API.
        for (var pageSize = 1; pageSize < report.Entries.Count; pageSize++)
        {
            var previous = report.OpeningBalance;
            foreach (var page in report.Entries.Chunk(pageSize))
                foreach (var row in page)
                {
                    previous += row.DebitAmount - row.CreditAmount;
                    Assert.Equal(previous, row.BalanceAfter);
                }
            Assert.Equal(report.ClosingBalance, previous);
        }
    }

    private JournalEntry Add(int number, DateTime businessDate, DateTime posted, Account account,
        decimal debit, decimal credit, decimal snapshot, int line)
    {
        var journal = JournalEntry.Create(account.TenantId, Number(number), businessDate, "Fixture", Guid.NewGuid(),
            null, "Immutable local test fixture", Guid.NewGuid().ToString(), null);
        journal.SetPostedAtUtc(posted);
        _fixture.Add(journal);
        AddLine(journal, account, debit, credit, snapshot, line);
        return journal;
    }

    private void AddLine(JournalEntry journal, Account account, decimal debit, decimal credit, decimal snapshot, int line)
    {
        _fixture.Add(debit > 0
            ? LedgerEntry.CreateDebit(account.TenantId, journal.Id, account.Id, debit, snapshot, line)
            : LedgerEntry.CreateCredit(account.TenantId, journal.Id, account.Id, credit, snapshot, line));
    }

    private async Task SaveFixture()
    {
        await _fixture.SaveChangesAsync();
        _fixture.ChangeTracker.Clear();
        _db.ChangeTracker.Clear();
    }

    private Task<GeneralLedgerReport> Report(Account account, DateTime from, DateTime to) =>
        new FinancialReportService(_db, NullLogger<FinancialReportService>.Instance)
            .GetGeneralLedgerAsync(_tenant, account.Id, from, to);

    private static string Number(int number) => $"JE-2026-{number:D6}";
    private static void AssertChain(GeneralLedgerReport report, bool creditNormal)
    {
        var running = report.OpeningBalance;
        foreach (var row in report.Entries)
        {
            running += creditNormal ? row.CreditAmount - row.DebitAmount : row.DebitAmount - row.CreditAmount;
            Assert.Equal(running, row.BalanceAfter);
        }
        Assert.Equal(running, report.ClosingBalance);
    }

    public void Dispose()
    {
        _fixture.Dispose();
        _db.Dispose();
    }
}
