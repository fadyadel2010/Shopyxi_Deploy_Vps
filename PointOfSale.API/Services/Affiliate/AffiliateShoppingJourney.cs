using PointOfSale.API.DTOs.Cart;

namespace PointOfSale.API.Services.Affiliate;

/// <summary>Server-stamped first touch, carried by the canonical cart and checkout snapshot.
/// Never populated from a request DTO. Current SQL admission must still be checked.</summary>
public sealed record AffiliateShoppingJourney(Guid TenantId, Guid AffiliateAccountId, string ReferralCode, Guid? PromotionLinkId)
{
    public Guid? ReferralClickId { get; init; }
    public Guid? ReferralProductId { get; init; }

    // Request-local, server-stamped context; never populated by a request DTO.
    public static AffiliateShoppingJourney? FromRequest(HttpContext? context) =>
        context?.Items[typeof(AffiliateShoppingJourney)] as AffiliateShoppingJourney;

    public static AffiliateShoppingJourney? FromCart(CartDto cart)
    {
        if (cart.AffiliateJourney is { } journey)
            return journey.TenantId == cart.TenantId ? journey : null;

        // Upgrade pre-contract carts from their earliest server-admitted line. Do
        // not infer ownership from URL, customer headers or legacy funnel hints.
        var first = cart.Items.Where(i => i.IsAffiliateEligibleLine && i.AffiliateAccountId.HasValue &&
                !string.IsNullOrWhiteSpace(i.AffiliateReferralCode))
            .OrderBy(i => i.AddedAtUtc).FirstOrDefault();
        return first == null ? null : new(cart.TenantId, first.AffiliateAccountId!.Value,
            first.AffiliateReferralCode!, first.PromotionLinkId);
    }
}
