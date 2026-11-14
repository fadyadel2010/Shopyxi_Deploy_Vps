using Microsoft.EntityFrameworkCore;
using PointOfSale.Core.Entities;
using PointOfSale.Core.Entities.Accounting;
using PointOfSale.Core.Security;
using PointOfSale.Infrastructure.Data;

namespace PointOfSale.Infrastructure.Accounting;

public sealed class AccountingPeriodException(string code, string message) : InvalidOperationException(message)
{
    public string Code { get; } = code;
}

internal sealed class AccountingPeriodMutationScope : IDisposable
{
    private static readonly AsyncLocal<int> Depth = new();
    public static bool IsAuthorized => Depth.Value > 0;
    public AccountingPeriodMutationScope() => Depth.Value++;
    public void Dispose() => Depth.Value--;
}

public static class AccountingPeriodPostingGuard
{
    public static async Task LockAsync(ApplicationDbContext db, Guid tenantId, bool create, CancellationToken ct)
    {
        if (tenantId == Guid.Empty || (db.ScopedTenantId.HasValue && db.ScopedTenantId != tenantId))
            throw new UnauthorizedAccessException("Accounting period authority cannot cross the current tenant boundary.");
        if (!db.Database.IsRelational())
            throw new AccountingPeriodException("PERIOD_RELATIONAL_REQUIRED", "Accounting period authority requires a relational database.");
        if (db.Database.CurrentTransaction == null)
            throw new InvalidOperationException("Accounting period authority requires the caller's transaction.");
        if (db.Database.ProviderName != "Npgsql.EntityFrameworkCore.PostgreSQL")
            throw new AccountingPeriodException("PERIOD_POSTGRES_REQUIRED", "Accounting period concurrency requires PostgreSQL.");

        var affected = create
            ? await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO "AccountingPeriodAuthorities" ("TenantId", "Revision") VALUES ({tenantId}, 1)
                ON CONFLICT ("TenantId") DO UPDATE SET "Revision" = "AccountingPeriodAuthorities"."Revision" + 1
                """, ct)
            : await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE "AccountingPeriodAuthorities" SET "Revision" = "Revision" + 1 WHERE "TenantId" = {tenantId}
                """, ct);
        if (affected != 1)
            throw new AccountingPeriodException("PERIOD_REQUIRED", "Create and open an accounting period before posting.");
    }

    public static async Task<AccountingPeriod> RequireOpenAsync(ApplicationDbContext db, Guid tenantId, DateTime date, CancellationToken ct)
    {
        if (date == default)
            throw new AccountingPeriodException("PERIOD_DATE_REQUIRED", "An authoritative posting date is required.");
        await LockAsync(db, tenantId, false, ct);
        var day = DateOnly.FromDateTime(date);
        var periods = await db.AccountingPeriods.IgnoreQueryFilters().AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.StartDate <= day && x.EndDate >= day).Take(2).ToListAsync(ct);
        if (periods.Count != 1)
            throw new AccountingPeriodException("PERIOD_REQUIRED", "The posting date must resolve to exactly one accounting period.");
        if (periods[0].Status != AccountingPeriodStatus.Open)
            throw new AccountingPeriodException("PERIOD_NOT_OPEN", "The accounting period is not open. No posting was created.");
        return periods[0];
    }
}

public sealed record AccountingPeriodTotals(int JournalCount, decimal TotalDebits, decimal TotalCredits);

public sealed class AccountingPeriodService(ApplicationDbContext db)
{
    public async Task<AccountingPeriod> CreateAsync(Guid tenantId, Guid actor, string code, string name,
        DateOnly start, DateOnly end, bool open, string reason, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(code) || code.Trim().Length > 50 ||
            string.IsNullOrWhiteSpace(name) || name.Trim().Length > 200 || start > end || start == default || end == DateOnly.MaxValue)
            throw new AccountingPeriodException("PERIOD_INVALID", "A code, name and valid inclusive date boundaries are required.");
        ValidateReason(reason);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await AccountingPeriodPostingGuard.LockAsync(db, tenantId, true, ct);
        await RequireActorAsync(tenantId, actor, ct);
        if (await db.AccountingPeriods.IgnoreQueryFilters().AnyAsync(x => x.TenantId == tenantId &&
            (x.Code == code.Trim() || (x.StartDate <= end && x.EndDate >= start)), ct))
            throw new AccountingPeriodException("PERIOD_OVERLAP", "Period code or inclusive dates overlap an existing period.");
        using var authority = new AccountingPeriodMutationScope();
        var period = new AccountingPeriod
        {
            Id = Guid.NewGuid(), TenantId = tenantId, Code = code.Trim(), Name = name.Trim(), StartDate = start, EndDate = end,
            Status = open ? AccountingPeriodStatus.Open : AccountingPeriodStatus.Draft,
            CreatedBy = actor, CreatedAt = DateTime.UtcNow, Reason = reason.Trim()
        };
        db.AccountingPeriods.Add(period);
        AppendEvent(period, actor, "Create", null, new(0, 0, 0));
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return period;
    }

    public async Task<AccountingPeriod> TransitionAsync(Guid tenantId, Guid id, Guid actor, string action,
        long version, string reason, CancellationToken ct = default)
    {
        ValidateReason(reason);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await AccountingPeriodPostingGuard.LockAsync(db, tenantId, false, ct);
        await RequireActorAsync(tenantId, actor, ct);
        // Never reuse a pre-lock tracked period snapshot.
        var period = await db.AccountingPeriods.IgnoreQueryFilters().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == id, ct)
            ?? throw new AccountingPeriodException("PERIOD_NOT_FOUND", "Accounting period not found.");
        await db.Entry(period).ReloadAsync(ct);
        if (period.Version != version)
            throw new AccountingPeriodException("PERIOD_CONFLICT", "The period changed. Reload before confirming.");
        var previous = period.Status;
        var totals = await TotalsAsync(tenantId, period, ct);
        switch (action)
        {
            case "open" when previous == AccountingPeriodStatus.Draft:
                period.Status = AccountingPeriodStatus.Open;
                break;
            case "close" when previous == AccountingPeriodStatus.Open:
                // Conservative tenant-wide barrier: even an outbox row with missing/legacy
                // source-date metadata blocks closing. Never guess that it belongs elsewhere.
                if (await db.AccountingOutboxMessages.IgnoreQueryFilters().AnyAsync(x => x.TenantId == tenantId && x.ProcessedAtUtc == null, ct))
                    throw new AccountingPeriodException("PERIOD_PENDING_OUTBOX", "Resolve all pending or failed accounting outbox work before closing.");
                var from = Utc(period.StartDate);
                var until = Utc(period.EndDate).AddDays(1);
                if (await db.JournalEntries.IgnoreQueryFilters().Where(x => x.TenantId == tenantId && x.JournalDate >= from && x.JournalDate < until)
                    .AnyAsync(x => x.LedgerEntries.Count < 2 || x.LedgerEntries.Sum(l => l.DebitAmount) != x.LedgerEntries.Sum(l => l.CreditAmount), ct))
                    throw new AccountingPeriodException("PERIOD_UNBALANCED", "An incomplete or unbalanced journal blocks closing.");
                period.Status = AccountingPeriodStatus.Closed;
                period.ClosedBy = actor;
                period.ClosedAtUtc = DateTime.UtcNow;
                break;
            case "reopen" when previous == AccountingPeriodStatus.Closed:
                period.Status = AccountingPeriodStatus.Open;
                period.ReopenedBy = actor;
                period.ReopenedAtUtc = DateTime.UtcNow;
                break;
            default:
                throw new AccountingPeriodException("PERIOD_INVALID_TRANSITION", "This action is not valid for the current period status.");
        }
        using var authority = new AccountingPeriodMutationScope();
        period.Version++;
        period.Reason = reason.Trim();
        period.UpdatedBy = actor;
        period.UpdatedAt = DateTime.UtcNow;
        AppendEvent(period, actor, action, previous, totals);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return period;
    }

    public async Task<AccountingPeriodTotals> TotalsAsync(Guid tenantId, AccountingPeriod period, CancellationToken ct = default)
    {
        if (period.TenantId != tenantId) throw new UnauthorizedAccessException();
        var from = Utc(period.StartDate);
        var until = Utc(period.EndDate).AddDays(1);
        var journals = db.JournalEntries.IgnoreQueryFilters().Where(x => x.TenantId == tenantId && x.JournalDate >= from && x.JournalDate < until);
        var lines = db.LedgerEntries.IgnoreQueryFilters().Where(x => x.TenantId == tenantId && journals.Select(j => j.Id).Contains(x.JournalEntryId));
        return new(await journals.CountAsync(ct), await lines.SumAsync(x => (decimal?)x.DebitAmount, ct) ?? 0,
            await lines.SumAsync(x => (decimal?)x.CreditAmount, ct) ?? 0);
    }

    private async Task RequireActorAsync(Guid tenantId, Guid actor, CancellationToken ct)
    {
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM \"Users\" WHERE \"TenantId\" = {tenantId} AND \"Id\" = {actor} FOR SHARE", ct);
        var user = await db.Users.IgnoreQueryFilters().AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == actor && x.IsActive && !x.IsDeleted, ct);
        if (user == null || !PosRolePermissionCatalog.IsGranted(user.Role, PosPermissions.FinanceAdmin))
            throw new UnauthorizedAccessException("An active same-tenant finance administrator is required.");
    }

    private void AppendEvent(AccountingPeriod period, Guid actor, string action, AccountingPeriodStatus? previous, AccountingPeriodTotals totals)
    {
        db.AccountingPeriodEvents.Add(new AccountingPeriodEvent
        {
            Id = Guid.NewGuid(), TenantId = period.TenantId, AccountingPeriodId = period.Id, CreatedBy = actor,
            CreatedAt = DateTime.UtcNow, Action = action, PreviousStatus = previous, Status = period.Status,
            Version = period.Version, Reason = period.Reason, JournalCount = totals.JournalCount,
            TotalDebits = totals.TotalDebits, TotalCredits = totals.TotalCredits
        });
    }

    private static void ValidateReason(string reason)
    {
        if (string.IsNullOrWhiteSpace(reason) || reason.Trim().Length > 1000)
            throw new AccountingPeriodException("PERIOD_REASON_REQUIRED", "An explanatory reason of up to 1000 characters is required.");
    }
    public static DateTime Utc(DateOnly date) => date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
}
