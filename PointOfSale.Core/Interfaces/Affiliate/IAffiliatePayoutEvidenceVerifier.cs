using PointOfSale.Core.Entities.Affiliate;

namespace PointOfSale.Core.Interfaces.Affiliate;

/// <summary>Additional explicitly registered evidence authorities; no verifier means fail closed.</summary>
public interface IAffiliatePayoutEvidenceVerifier
{
    Task<bool> VerifyAsync(AffiliatePayoutTransferEvidence evidence, Guid affiliateAccountId,
        Guid storeTenantId, decimal amount, CancellationToken ct);
}
