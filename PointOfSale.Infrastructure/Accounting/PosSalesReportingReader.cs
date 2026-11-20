using Microsoft.EntityFrameworkCore;
using PointOfSale.Core.Entities.Accounting;
using PointOfSale.Core.Enums;
using PointOfSale.Core.Interfaces.Accounting;
using PointOfSale.Infrastructure.Data;

namespace PointOfSale.Infrastructure.Accounting;

/// <summary>
/// Credit-normal 4010 period movement, not cash collection or posting activity.
/// Reversed originals stay in history; separate reversing lines offset them on
/// their recorded accounting dates. No rewriting of historical invoice edits.
/// </summary>
public static class PosSalesReportingReader
{
    public static IQueryable<LedgerEntry> RevenueEntries(ApplicationDbContext db, Guid tenantId) =>
        db.LedgerEntries.AsNoTracking()
            .Where(entry => entry.TenantId == tenantId && entry.JournalEntry.TenantId == tenantId &&
                            entry.Account.TenantId == tenantId &&
                            (entry.JournalEntry.Status == JournalStatus.Posted || entry.JournalEntry.Status == JournalStatus.Reversed) &&
                            entry.Account.AccountCode == AccountCodes.PosSales);

    public static async Task<PosSalesSnapshot> ReadAsync(ApplicationDbContext db, Guid tenantId, DateTime todayUtc, CancellationToken ct)
    {
        if (todayUtc.Kind != DateTimeKind.Utc || todayUtc.TimeOfDay != TimeSpan.Zero)
            throw new ArgumentException("A UTC accounting day is required.", nameof(todayUtc));
        var end = todayUtc.AddDays(1);
        var yesterday = todayUtc.AddDays(-1);
        var week = todayUtc.AddDays(-(int)todayUtc.DayOfWeek);
        var month = new DateTime(todayUtc.Year, todayUtc.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        var year = new DateTime(todayUtc.Year, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var result = await RevenueEntries(db, tenantId).Where(entry => entry.JournalEntry.JournalDate < end)
            .GroupBy(_ => 1).Select(group => new PosSalesSnapshot
            {
                TodaySales = group.Sum(e => e.JournalEntry.JournalDate >= todayUtc ? e.CreditAmount - e.DebitAmount : 0m),
                YesterdaySales = group.Sum(e => e.JournalEntry.JournalDate >= yesterday && e.JournalEntry.JournalDate < todayUtc ? e.CreditAmount - e.DebitAmount : 0m),
                WeekSales = group.Sum(e => e.JournalEntry.JournalDate >= week ? e.CreditAmount - e.DebitAmount : 0m),
                MonthSales = group.Sum(e => e.JournalEntry.JournalDate >= month ? e.CreditAmount - e.DebitAmount : 0m),
                YearSales = group.Sum(e => e.JournalEntry.JournalDate >= year ? e.CreditAmount - e.DebitAmount : 0m),
                TotalRevenue = group.Sum(e => e.CreditAmount - e.DebitAmount)
            }).SingleOrDefaultAsync(ct) ?? new PosSalesSnapshot();
        result.TodayTransactionCount = await db.JournalEntries.AsNoTracking()
            .Where(j => j.TenantId == tenantId && (j.Status == JournalStatus.Posted || j.Status == JournalStatus.Reversed) &&
                        j.SourceType == "SalesInvoice" && j.JournalDate >= todayUtc && j.JournalDate < end)
            .Select(j => j.SourceId).Distinct().CountAsync(ct);
        return result;
    }
}

public sealed class PosSalesSnapshot
{
    public decimal TodaySales { get; init; }
    public decimal YesterdaySales { get; init; }
    public decimal WeekSales { get; init; }
    public decimal MonthSales { get; init; }
    public decimal YearSales { get; init; }
    public decimal TotalRevenue { get; init; }
    public int TodayTransactionCount { get; set; }
    public decimal? SalesChangePercentage => YesterdaySales > 0m
        ? decimal.Round((TodaySales - YesterdaySales) / YesterdaySales * 100m, 2, MidpointRounding.AwayFromZero) : null;
}
