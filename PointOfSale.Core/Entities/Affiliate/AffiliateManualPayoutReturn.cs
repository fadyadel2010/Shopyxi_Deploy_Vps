namespace PointOfSale.Core.Entities.Affiliate;

/// <summary>Append-only receipt for a full, actually received return of a manual cash payout.
/// Original payout, withdrawal and Paid events remain immutable historical evidence.</summary>
public sealed class AffiliateManualPayoutReturn
{
    public const string AttestationText = "I CONFIRM THE FULL MANUAL PAYOUT HAS BEEN RETURNED TO THE ORIGINAL CASHBOX";
    public Guid Id { get; init; }
    public Guid StoreTenantId { get; init; }
    public Guid AffiliateAccountId { get; init; }
    public Guid RequestId { get; init; }
    public Guid ManualPayoutId { get; init; }
    public Guid OriginalJournalId { get; init; }
    public Guid BalanceLedgerId { get; init; }
    public Guid AdministratorId { get; init; }
    public decimal Amount { get; init; }
    public string Currency { get; init; } = "";
    public DateTime RecordedAtUtc { get; init; }
    public string Attestation { get; init; } = "";
    public string Reason { get; init; } = "";
    public string ReceiptReference { get; init; } = "";
    public string CommandFingerprint { get; init; } = "";
}

/// <summary>Exact Paid-event allocations released by a return; never a new commission award.</summary>
public sealed class AffiliateManualPayoutReturnAllocation
{
    public Guid Id { get; init; }
    public Guid ReturnId { get; init; }
    public Guid PaidEntryId { get; init; }
    public decimal Amount { get; init; }
}
