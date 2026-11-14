using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using PointOfSale.API.DTOs.Cart;
using PointOfSale.API.Security;
using PointOfSale.API.Services.Cart;
using PointOfSale.Core.Enums;
using PointOfSale.Infrastructure.Data;

namespace PointOfSale.API.Services.Affiliate;

public interface IAffiliateShoppingJourneyService
{
    Task<AffiliateShoppingJourney?> GetActiveAsync(HttpContext context, Guid tenantId, CancellationToken ct);
    Task BindActiveCartAsync(HttpContext context, Guid tenantId, AffiliateShoppingJourney journey, CancellationToken ct);
}

public sealed class AffiliateShoppingJourneyService(ICartService carts, ApplicationDbContext db) : IAffiliateShoppingJourneyService
{
    private sealed record JourneySnapshot(AffiliateShoppingJourney? AffiliateJourney);
    private static async Task<(CartOwner? Owner, Guid? CustomerId, string? SessionId)> ResolveOwnerAsync(HttpContext context, Guid tenantId)
    {
        CartOwner? owner = null;
        Guid? customerId = null;
        // Same authenticated schemes/claims as the cart and checkout controllers;
        // never use X-Customer-Id or a browser tenant identifier as authority.
        foreach (var scheme in new[] { CustomerSecurityConstants.AuthenticationScheme, "StorefrontJwt" })
        {
            var principal = (await context.AuthenticateAsync(scheme)).Principal;
            if (principal?.Identity?.IsAuthenticated != true) continue;
            var claim = principal.FindFirst("CustomerId")?.Value ?? principal.FindFirst("customer_id")?.Value
                ?? principal.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (!Guid.TryParse(claim, out var id)) continue;
            var tenantClaim = principal.FindFirst("TenantId")?.Value ?? principal.FindFirst("tenant_id")?.Value;
            if (Guid.TryParse(tenantClaim, out var claimedTenant) && claimedTenant != tenantId)
                throw new UnauthorizedAccessException("Customer tenant mismatch.");
            customerId = id;
            owner = new CartOwner { OwnerType = "customer", OwnerId = id.ToString("N") };
            break;
        }
        var sessionId = context.Request.Headers["X-Session-Id"].FirstOrDefault()?.Trim();
        if (owner == null && !string.IsNullOrWhiteSpace(sessionId))
            owner = new CartOwner { OwnerType = "session", OwnerId = sessionId };
        return (owner, customerId, sessionId);
    }

    public async Task BindActiveCartAsync(HttpContext context, Guid tenantId, AffiliateShoppingJourney journey, CancellationToken ct)
    {
        var (owner, _, _) = await ResolveOwnerAsync(context, tenantId);
        if (owner == null) return;
        if (await carts.GetJourneyCartAsync(tenantId, owner, context.TraceIdentifier, ct) == null) return;
        var accepted = await carts.BindAffiliateJourneyAsync(tenantId, owner, journey, context.TraceIdentifier, ct);
        if (accepted != null && accepted != journey)
            throw new InvalidOperationException("The active cart first touch changed during admission. Retry the request.");
    }

    public async Task<AffiliateShoppingJourney?> GetActiveAsync(HttpContext context, Guid tenantId, CancellationToken ct)
    {
        var (owner, customerId, sessionId) = await ResolveOwnerAsync(context, tenantId);
        if (owner == null) return null;

        var cart = await carts.GetJourneyCartAsync(tenantId, owner, context.TraceIdentifier, ct);
        if (cart != null)
        {
            // An order is the synchronous terminal authority, even if asynchronous
            // checkout conversion or post-commit Redis cleanup has not caught up.
            var converted = await db.Orders.AsNoTracking().IgnoreQueryFilters()
                .AnyAsync(o => o.TenantId == tenantId && o.CartId == cart.CartId, ct);
            if (!converted && AffiliateShoppingJourney.FromCart(cart) is { } binding)
                return binding;
        }

        // A live checkout can outlast its Redis cart. Abandoned is recoverable,
        // not active: it only preserves first touch while its cart is still live.
        var snapshots = await db.CheckoutSessions.AsNoTracking().IgnoreQueryFilters()
            .Where(s => s.TenantId == tenantId && s.InvalidatedAtUtc == null &&
                s.Status == CheckoutSessionStatus.Active && s.ConvertedOrderId == null &&
                (customerId.HasValue ? s.OnlineCustomerId == customerId :
                    s.OnlineCustomerId == null && s.SessionId == sessionId) &&
                !db.Orders.IgnoreQueryFilters().Any(o => o.TenantId == tenantId && o.CartId == s.CartId))
            .OrderByDescending(s => s.LastActivityAtUtc)
            .Select(s => s.ItemsSnapshotJson).ToListAsync(ct);
        foreach (var snapshot in snapshots)
        {
            var items = JsonSerializer.Deserialize<List<JourneySnapshot>>(snapshot,
                new JsonSerializerOptions(JsonSerializerDefaults.Web));
            var binding = items?.Select(i => i.AffiliateJourney).FirstOrDefault(j => j != null);
            if (binding != null && binding.TenantId == tenantId) return binding;
        }
        return null;
    }
}
