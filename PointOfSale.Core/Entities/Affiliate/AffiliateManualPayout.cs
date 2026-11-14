namespace PointOfSale.Core.Entities.Affiliate;

/// <summary>Append-only administrator attestation and allocation plan. Never external verification.</summary>
public sealed class AffiliateManualPayout
{
    public const string AttestationText = "I CONFIRM THE MANUAL PAYOUT HAS BEEN PERFORMED";
    public Guid Id { get; init; }
    public Guid RequestId { get; init; }
    public Guid StoreTenantId { get; init; }
    public Guid AffiliateAccountId { get; init; }
    public Guid AffiliateUserId { get; init; }
    public decimal Amount { get; init; }
    public string Currency { get; init; } = "";
    public string Method { get; init; } = "";
    public string DestinationNumber { get; init; } = "";
    public string DestinationType { get; init; } = "";
    public DateTime? DestinationUpdatedAtUtc { get; init; }
    public DateTime DestinationCapturedAtUtc { get; init; }
    public string DestinationFingerprint { get; init; } = "";
    public Guid CashboxId { get; init; }
    public Guid TreasuryAccountId { get; init; }
    public Guid SourceAccountId { get; init; }
    public string SourceAccountCode { get; init; } = "";
    public string SourceAccountName { get; init; } = "";
    public Guid AdministratorId { get; init; }
    public string AdministratorRole { get; init; } = "";
    public DateTime ExecutedAtUtc { get; init; }
    public string Attestation { get; init; } = "";
    public string? InternalReference { get; init; }
    public string? Notes { get; init; }
    public Guid BalanceLedgerId { get; init; }
    public string AllocationsJson { get; init; } = "";
    public string CommandFingerprint { get; init; } = "";
}
