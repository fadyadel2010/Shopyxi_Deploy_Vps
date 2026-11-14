using PointOfSale.Core.Interfaces;

namespace PointOfSale.Core.Entities;

public enum FinancialVoucherKind { Receipt, Payment }
public enum FinancialVoucherStatus { Issued, Reversed }

/// <summary>A numbered receipt/payment document for an already-posted canonical party payment.</summary>
public sealed class FinancialVoucher : BaseEntity, ITenantOwned
{
    public Guid TenantId { get; set; }
    public string Number { get; set; } = string.Empty;
    public string RequestFingerprint { get; set; } = string.Empty;
    public FinancialVoucherKind Kind { get; set; }
    public Guid SourcePaymentId { get; set; }
    public Guid PartyId { get; set; }
    public string PartyName { get; set; } = string.Empty;
    public decimal Amount { get; set; }
    public string Currency { get; set; } = string.Empty;
    public DateTime PaymentDate { get; set; }
    public Guid JournalId { get; set; }
    public string Reason { get; set; } = string.Empty;
    public long Version { get; set; } = 1;
    public FinancialVoucherStatus Status { get; set; }
    public Guid? ReversalJournalId { get; set; }
    public Guid? ReversedBy { get; set; }
    public DateTime? ReversedAtUtc { get; set; }
    public string? ReversalReason { get; set; }
}

public sealed class FinancialVoucherEvent : BaseEntity, ITenantOwned
{
    public Guid TenantId { get; set; }
    public Guid FinancialVoucherId { get; set; }
    public long Version { get; set; }
    public string Action { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
    public Guid JournalId { get; set; }
}
