using Microsoft.EntityFrameworkCore;
using PointOfSale.API.DTOs.Cart;
using PointOfSale.API.Services.Payments;
using PointOfSale.API.Services.Storefront;
using PointOfSale.Core.Entities;
using PointOfSale.Core.Enums;
using PointOfSale.Core.Interfaces.Affiliate;
using PointOfSale.Infrastructure.Data;
using PointOfSale.Infrastructure.Elasticsearch.Services;
using PointOfSale.Infrastructure.MongoDB.Documents;

namespace PointOfSale.API.Services.Affiliate;

/// <summary>
/// No cached eligibility and no writes: policy remains participation, never publication.
/// Checkout's runtime payment and shipping authorities perform local admission only;
/// this service never executes a payment, quote, shipment, or provider connection test.
/// </summary>
public sealed class AffiliatePromotionReadinessService(
    ApplicationDbContext db,
    IStorefrontPaymentRuntimeReadinessService payments,
    IShippingMethodService shipping) : IAffiliatePromotionReadiness
{
    public async Task<AffiliatePromotionReadiness> EvaluateAsync(Guid tenantId, Guid productId,
        CancellationToken ct = default)
    {
        // Ownership is resolved before reading merchant configuration. Foreign and
        // nonexistent identifiers have the identical safe result, including no blockers.
        var product = await db.Products.AsNoTracking().IgnoreQueryFilters()
            .SingleOrDefaultAsync(p => p.TenantId == tenantId && p.Id == productId && !p.IsDeleted, ct);
        if (tenantId == Guid.Empty || product == null)
            return new(false, false, false, false, false, false, false, Array.Empty<string>());

        var merchant = await EvaluateMerchantAsync(tenantId, ct);
        var blockers = merchant.BlockerCodes.ToList();
        var tenant = await db.Tenants.AsNoTracking().IgnoreQueryFilters()
            .SingleOrDefaultAsync(t => t.Id == tenantId, ct);
        var shippingReady = merchant.ShippingReady;

        var productBlockers = new List<string>();
        if (!product.IsActive) productBlockers.Add("PRODUCT_NOT_ACTIVE");
        if (product.PublishStatus == PublishStatus.Archived) productBlockers.Add("PRODUCT_ARCHIVED");
        if (product.PublishStatus != PublishStatus.Published) productBlockers.Add("PRODUCT_NOT_PUBLISHED");
        if (!product.IsOnlineVisible || !product.IsSearchable) productBlockers.Add("PRODUCT_NOT_SELLABLE");
        if (!string.Equals(tenant?.Currency?.Trim(), "EGP", StringComparison.OrdinalIgnoreCase))
            productBlockers.Add("PRODUCT_CONFIGURATION_INVALID");

        // Same SQL recomposition, semantic variant, category lineage, pricing and
        // backorder/non-stock rules used by normal Storefront and Affiliate reads.
        var current = await ProductSearchSqlAuthorityOverlay.ApplyAsync(db,
            new ProductDocument { ProductId = productId, TenantId = tenantId }, tenantId, ct);
        if (current == null || string.IsNullOrWhiteSpace(product.Name) ||
            !current.Variants.Any(v => v.IsActive) || current.DefaultUnit is not { ConversionFactor: > 0 })
            productBlockers.Add("PRODUCT_CONFIGURATION_INVALID");
        if (current == null || !current.TryResolveAffiliatePublicPricing(out var pricing))
            productBlockers.Add("PRODUCT_NOT_SELLABLE");
        else if (!pricing.InStock)
            productBlockers.Add("PRODUCT_NOT_SELLABLE");
        else if (shippingReady)
        {
            // A priced variant in one warehouse and stock in an unrelated warehouse
            // must not manufacture a purchasable offer. At least one real variant
            // must be fulfillable through one configured checkout shipping method.
            var fulfillable = false;
            foreach (var variant in current.Variants.Where(v => v.IsActive && (product.HasVariants ? v.EffectivePrice > 0 : v.IsDefault)))
            {
                var availability = await shipping.GetShippingMethodsAvailabilityForConfiguredWarehousesAsync(
                    tenantId, product.HasVariants ? variant.EffectivePrice : pricing.Price ?? 0, product.Weight,
                    new[] { new CartItemDto { ProductId = productId, VariantId = variant.VariantId, Quantity = 1 } },
                    "affiliate-product-readiness", ct);
                if (availability.ShippingMethods.Count > 0) { fulfillable = true; break; }
            }
            if (!fulfillable) productBlockers.Add("PRODUCT_NOT_SELLABLE");
        }
        blockers.AddRange(productBlockers);

        var policy = await db.AffiliateProductPolicies.AsNoTracking().IgnoreQueryFilters()
            .SingleOrDefaultAsync(p => p.TenantId == tenantId && p.ProductId == productId && !p.IsDeleted, ct);
        var categoryReady = policy?.AffiliateCategoryId is Guid categoryId &&
            await db.AffiliateCategories.AsNoTracking().IgnoreQueryFilters().AnyAsync(category =>
                category.Id == categoryId && category.IsActive && !category.IsDeleted &&
                ((category.Level == 1 && category.ParentId == null) ||
                 (category.Level == 2 && category.ParentId != null &&
                  db.AffiliateCategories.IgnoreQueryFilters().Any(parent => parent.Id == category.ParentId &&
                      parent.IsActive && !parent.IsDeleted && parent.Level == 1 && parent.ParentId == null))), ct);
        if (policy?.IsEnabled != true) blockers.Add("AFFILIATE_POLICY_DISABLED");
        else if (!categoryReady) blockers.Add("AFFILIATE_CATEGORY_REQUIRED");
        return new(true, merchant.StorefrontReady, merchant.PaymentReady, shippingReady, productBlockers.Count == 0,
            policy?.IsEnabled == true, categoryReady, blockers.Distinct().ToArray());
    }

    public async Task<AffiliateMerchantPromotionReadiness> EvaluateMerchantAsync(Guid tenantId,
        CancellationToken ct = default)
    {
        if (tenantId == Guid.Empty)
            return new(false, false, false, Array.Empty<string>());
        var blockers = new List<string>();
        var tenant = await db.Tenants.AsNoTracking().IgnoreQueryFilters()
            .SingleOrDefaultAsync(t => t.Id == tenantId, ct);
        var status = await db.StoreConfigs.AsNoTracking().IgnoreQueryFilters()
            .Where(c => c.TenantId == tenantId).Select(c => c.StorefrontStatus).SingleOrDefaultAsync(ct);
        var enabled = !string.IsNullOrWhiteSpace(status) && !string.Equals(status.Trim(), "disabled", StringComparison.OrdinalIgnoreCase);
        var active = tenant is { IsActive: true, Status: TenantStatus.Active, DeletedAtUtc: null } &&
            string.Equals(status?.Trim(), "live", StringComparison.OrdinalIgnoreCase);
        if (!enabled) blockers.Add("STOREFRONT_NOT_ENABLED");
        if (!active) blockers.Add("STOREFRONT_NOT_ACTIVE");
        var paymentReady = false;
        var methods = await db.TenantPaymentMethods.AsNoTracking().IgnoreQueryFilters()
            .Where(m => m.TenantId == tenantId && m.IsEnabled && !m.IsDeleted)
            .Select(m => m.Code).Distinct().OrderBy(code => code).ToListAsync(ct);
        foreach (var code in methods)
        {
            if ((await payments.EvaluateAsync(tenantId, code, ct)).IsReady)
            {
                paymentReady = true;
                break;
            }
        }
        if (!paymentReady) blockers.Add("PAYMENT_METHOD_REQUIRED");
        // SQL-backed checkout availability, with carrier-specific admission only.
        // Manual shipping needs no external geographic mappings or provider call.
        var methodsAvailable = await shipping.GetShippingMethodsAvailabilityForConfiguredWarehousesAsync(
            tenantId, 0, 0, Array.Empty<CartItemDto>(), "affiliate-publishing-readiness", ct);
        var shippingReady = methodsAvailable.ShippingMethods.Count > 0;
        if (!shippingReady) blockers.Add("SHIPPING_METHOD_REQUIRED");
        return new(enabled && active, paymentReady, shippingReady, blockers);
    }

    public async Task<AffiliatePromotionReadiness> EvaluateForMutationAsync(Guid tenantId, Guid productId,
        CancellationToken ct = default)
    {
        if (db.Database.CurrentTransaction == null)
            throw new InvalidOperationException("Affiliate admission requires an authority transaction.");
        if (!await db.Products.AsNoTracking().IgnoreQueryFilters()
                .AnyAsync(p => p.Id == productId && p.TenantId == tenantId && !p.IsDeleted, ct))
            return new(false, false, false, false, false, false, false, Array.Empty<string>());
        // PostgreSQL row locks conflict with normal UPDATE/DELETE regardless of
        // which Admin endpoint performs it. They survive until policy/link commit.
        // Readiness changes serialize either before admission (rejected) or after
        // commit (immediately hidden by fresh read-side admission). No new schema.
        if (!db.Database.IsNpgsql())
            throw new InvalidOperationException("Affiliate mutation locking requires PostgreSQL.");
        await db.Database.SqlQuery<Guid>($"SELECT \"Id\" AS \"Value\" FROM \"Tenants\" WHERE \"Id\" = {tenantId} FOR SHARE").ToListAsync(ct);
        foreach (var table in new[] { "StoreConfigs", "TenantPaymentMethods", "ShippingMethods", "Warehouses",
                     "Products", "ProductVariants", "ProductVariantStocks", "Units", "Categories",
                     "AffiliateProductPolicies", "CarrierConfigurations", "CarrierAccounts", "CarrierWarehouseAssignments", "CarrierServiceLevelEnablements" })
        {
            // Identifiers come only from this closed server-owned allowlist.
            await db.Database.SqlQueryRaw<Guid>(
                $"SELECT \"Id\" AS \"Value\" FROM \"{table}\" WHERE \"TenantId\" = {{0}} ORDER BY \"Id\" FOR SHARE", tenantId).ToListAsync(ct);
        }
        await db.Database.SqlQuery<Guid>($"SELECT \"Id\" AS \"Value\" FROM \"ProductUnits\" WHERE \"ProductId\" = {productId} FOR SHARE").ToListAsync(ct);
        await db.Database.SqlQuery<Guid>($"SELECT \"Id\" AS \"Value\" FROM \"AffiliateCategories\" WHERE \"Id\" IN (SELECT \"AffiliateCategoryId\" FROM \"AffiliateProductPolicies\" WHERE \"TenantId\" = {tenantId} AND \"ProductId\" = {productId}) OR \"Id\" IN (SELECT \"ParentId\" FROM \"AffiliateCategories\" WHERE \"Id\" IN (SELECT \"AffiliateCategoryId\" FROM \"AffiliateProductPolicies\" WHERE \"TenantId\" = {tenantId} AND \"ProductId\" = {productId})) ORDER BY \"Id\" FOR SHARE").ToListAsync(ct);
        return await EvaluateAsync(tenantId, productId, ct);
    }
}
