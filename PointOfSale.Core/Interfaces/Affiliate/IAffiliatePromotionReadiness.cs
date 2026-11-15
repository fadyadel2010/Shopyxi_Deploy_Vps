namespace PointOfSale.Core.Interfaces.Affiliate;

/// <summary>Current SQL/checkout admission, independent of persisted policy participation.</summary>
public interface IAffiliatePromotionReadiness
{
    Task<AffiliateMerchantPromotionReadiness> EvaluateMerchantAsync(Guid tenantId,
        CancellationToken ct = default);
    Task<AffiliatePromotionReadiness> EvaluateAsync(Guid tenantId, Guid productId,
        CancellationToken ct = default);

    /// <summary>Requires an open transaction; locks authority until its caller commits.</summary>
    Task<AffiliatePromotionReadiness> EvaluateForMutationAsync(Guid tenantId, Guid productId,
        CancellationToken ct = default);
}

public sealed record AffiliateMerchantPromotionReadiness(
    bool StorefrontReady, bool PaymentReady, bool ShippingReady, IReadOnlyList<string> BlockerCodes)
{
    public bool IsReady => StorefrontReady && PaymentReady && ShippingReady;
}

public sealed record AffiliatePromotionReadiness(
    bool ProductFound,
    bool StorefrontReady,
    bool PaymentReady,
    bool ShippingReady,
    bool ProductSellable,
    bool PolicyEnabled,
    bool CategoryReady,
    IReadOnlyList<string> BlockerCodes)
{
    public bool PrerequisitesReady => ProductFound && StorefrontReady && PaymentReady && ShippingReady && ProductSellable;
    public bool IsPromotionEligible => PrerequisitesReady && PolicyEnabled && CategoryReady;
}

public sealed class AffiliatePromotionNotReadyException(AffiliatePromotionReadiness readiness)
    : InvalidOperationException("Affiliate promotion prerequisites are not satisfied.")
{
    public AffiliatePromotionReadiness Readiness { get; } = readiness;
}
