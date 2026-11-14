using PointOfSale.Core.Interfaces;

namespace PointOfSale.Core.Entities.Accounting;

public enum AccountingPeriodStatus { Draft, Open, Closed }

/// <summary>Inclusive UTC business-date boundaries. History is never reassigned.</summary>
public sealed class AccountingPeriod : BaseEntity, ITenantOwned
{
    public Guid TenantId { get; set; }
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public DateOnly StartDate { get; set; }
    public DateOnly EndDate { get; set; }
    public AccountingPeriodStatus Status { get; set; }
    public long Version { get; set; } = 1;
    public Guid? ClosedBy { get; set; }
    public DateTime? ClosedAtUtc { get; set; }
    public Guid? ReopenedBy { get; set; }
    public DateTime? ReopenedAtUtc { get; set; }
    public string Reason { get; set; } = string.Empty;
}

/// <summary>
/// A write fence, not financial data. Every period transition and new posting
/// updates this row in its transaction. Unlike an advisory lock alone, the
/// changed tuple also rejects stale PostgreSQL SERIALIZABLE snapshots.
/// </summary>
public sealed class AccountingPeriodAuthority : ITenantOwned
{
    public Guid TenantId { get; set; }
    public long Revision { get; set; }
}

public sealed class AccountingPeriodEvent : BaseEntity, ITenantOwned
{
    public Guid TenantId { get; set; }
    public Guid AccountingPeriodId { get; set; }
    public AccountingPeriodStatus? PreviousStatus { get; set; }
    public AccountingPeriodStatus Status { get; set; }
    public long Version { get; set; }
    public string Action { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
    public decimal TotalDebits { get; set; }
    public decimal TotalCredits { get; set; }
    public int JournalCount { get; set; }
}
