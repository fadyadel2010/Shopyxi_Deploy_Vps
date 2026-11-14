using Microsoft.EntityFrameworkCore;
using PointOfSale.Core.Entities;
using PointOfSale.Core.Entities.Affiliate;
using PointOfSale.Core.Enums;
using PointOfSale.Infrastructure.Data;

namespace PointOfSale.Infrastructure.Services.Affiliate;

/// <summary>Read-only canonical portal metadata. Never calculates eligibility or wallet availability.</summary>
public static class AffiliatePortalReadStore
{
    public static IQueryable<Order> Orders(ApplicationDbContext db, Guid affiliateId, Guid? storeId = null) =>
        from order in db.Orders.IgnoreQueryFilters().AsNoTracking()
        join attribution in db.AffiliateAttributions.IgnoreQueryFilters().AsNoTracking()
            on new { order.TenantId, OrderId = order.Id } equals new { attribution.TenantId, attribution.OrderId }
        where !order.IsDeleted && !attribution.IsDeleted && order.Source == OrderSource.Ecommerce &&
              order.AffiliateAccountId == affiliateId && attribution.AffiliateAccountId == affiliateId &&
              order.AffiliateReferralCode == attribution.ReferralCode && order.PromotionLinkId == attribution.PromotionLinkId &&
              (!storeId.HasValue || order.TenantId == storeId.Value)
        select order;

    public static IQueryable<Guid> SyntheticOrderIds(ApplicationDbContext db) =>
        db.SyntheticQaOrders.IgnoreQueryFilters().Select(q => q.OrderId)
            .Concat(db.SyntheticQaEvidence.IgnoreQueryFilters().Select(q => q.OrderId))
            .Concat(db.SyntheticQaCommissionPostings.IgnoreQueryFilters().Select(q => q.OrderId));

    // Only the requested, already-authorized page is materialized. No customer/address/payment-reference columns.
    public static async Task<List<AffiliateCanonicalOrder>> ReadOrdersAsync(
        ApplicationDbContext db, Guid affiliateId, IReadOnlyCollection<Guid> orderIds, Guid? storeId = null,
        CancellationToken ct = default)
    {
        var syntheticIds = SyntheticOrderIds(db);
        var result = await Orders(db, affiliateId, storeId).Where(o => orderIds.Contains(o.Id))
            .Select(o => new AffiliateCanonicalOrder
            {
                OrderId = o.Id, StoreTenantId = o.TenantId, OrderNumber = o.OrderNumber, CreatedAtUtc = o.CreatedAt,
                OrderTotal = o.Total, OrderCurrency = o.Currency, ShippingCharge = o.ShippingCost, PaymentFee = o.PaymentFee,
                OrderStatus = o.Status.ToString(), PaymentStatus = o.PaymentStatus == PaymentStatus.Collected ? "Collected" : o.PaymentStatus.ToString(),
                FulfillmentStatus = o.FulfillmentStatus.ToString(), PaymentMethod = o.PaymentMethodCode,
                IsSyntheticQa = syntheticIds.Contains(o.Id),
                HasUnclassifiedQaPayment = o.PaymentStatus == PaymentStatus.SyntheticQaCollected,
                Items = db.OrderItems.IgnoreQueryFilters().Where(i => i.OrderId == o.Id && !i.IsDeleted)
                    .OrderBy(i => i.Id).Select(i => new AffiliateCanonicalItem
                    {
                        OrderItemId = i.Id, ProductId = i.ProductId, VariantId = i.VariantId,
                        ProductName = i.ProductName, VariantName = i.VariantName, Quantity = i.Quantity,
                        LinePaymentAllocated = i.PaidAmount, LineRefunded = i.RefundedAmount,
                        CommissionTermsCaptured = i.AffiliateCommissionSnapshotCapturedAtUtc.HasValue,
                        CommissionEligibleAtCheckout = i.AffiliateCommissionEligible
                    }).ToList()
            }).ToListAsync(ct);
        var ids = result.Select(o => o.OrderId).ToArray();
        var context = await (from a in db.AffiliateAttributions.IgnoreQueryFilters().AsNoTracking()
                             join o in Orders(db, affiliateId, storeId) on new { a.TenantId, a.OrderId } equals new { o.TenantId, OrderId = o.Id }
                             where ids.Contains(o.Id) && a.AffiliateAccountId == affiliateId
                             select new { a.OrderId, a.Id, a.AffiliateAccountId, a.ReferralCode, a.Source, a.PromotionLinkId, a.AttributedAtUtc })
            .ToDictionaryAsync(a => a.OrderId, ct);
        var storeIds = result.Select(o => o.StoreTenantId).Distinct().ToArray();
        var stores = await db.Tenants.IgnoreQueryFilters().AsNoTracking().Where(t => storeIds.Contains(t.Id))
            .Select(t => new { t.Id, t.Name }).ToDictionaryAsync(t => t.Id, ct);
        var linkIds = context.Values.Where(a => a.PromotionLinkId.HasValue).Select(a => a.PromotionLinkId!.Value).ToArray();
        var links = await db.AffiliatePromotionLinks.IgnoreQueryFilters().AsNoTracking()
            .Where(l => linkIds.Contains(l.Id) && l.AffiliateAccountId == affiliateId)
            .Select(l => new { l.Id, l.TenantId, l.ReferralCode, l.ProductId }).ToDictionaryAsync(l => l.Id, ct);
        foreach (var order in result)
        {
            var a = context[order.OrderId];
            order.AttributionId = a.Id; order.AffiliateAccountId = a.AffiliateAccountId;
            order.ReferralCode = a.ReferralCode; order.AttributionSource = a.Source.ToString();
            order.AttributedAtUtc = a.AttributedAtUtc; order.PromotionLinkId = a.PromotionLinkId;
            order.StoreName = stores.GetValueOrDefault(order.StoreTenantId)?.Name;
            if (a.PromotionLinkId.HasValue && links.TryGetValue(a.PromotionLinkId.Value, out var link) &&
                link.TenantId == order.StoreTenantId && link.ReferralCode == a.ReferralCode)
                order.PromotedProductId = link.ProductId;
        }
        return result;
    }

    public static async Task<AffiliateCommissionActivityPage> ReadActivityAsync(ApplicationDbContext db, Guid affiliateId,
        int page = 1, int pageSize = 20, Guid? storeId = null, CancellationToken ct = default)
    {
        page = Math.Clamp(page, 1, 1_000_000); pageSize = Math.Clamp(pageSize, 1, 100);
        var query = db.AffiliateCommissionLedger.IgnoreQueryFilters().AsNoTracking()
            .Where(e => e.AffiliateAccountId == affiliateId && (!storeId.HasValue || e.StoreTenantId == storeId.Value));
        var count = await query.CountAsync(ct);
        var entries = await query.OrderByDescending(e => e.RecordedAtUtc).ThenByDescending(e => e.Id)
            .Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        // Maximum lineage depth is two: Paid -> Approved -> Earned. Load each level as a batch, never per row.
        var byId = entries.ToDictionary(e => e.Id);
        for (var depth = 0; depth < 2; depth++)
        {
            var parents = byId.Values.Where(e => e.OriginalEntryId.HasValue && !byId.ContainsKey(e.OriginalEntryId.Value))
                .Select(e => e.OriginalEntryId!.Value).Distinct().ToArray();
            if (parents.Length == 0) break;
            foreach (var parent in await query.Where(e => parents.Contains(e.Id)).ToListAsync(ct)) byId[parent.Id] = parent;
        }
        var orders = (await ReadOrdersAsync(db, affiliateId, entries.Select(e => e.OrderId).Distinct().ToArray(), storeId, ct))
            .ToDictionary(o => o.OrderId);
        var rows = entries.Select(e =>
        {
            orders.TryGetValue(e.OrderId, out var order);
            var item = order?.Items.SingleOrDefault(i => i.OrderItemId == e.OrderItemId);
            var valid = HasLineage(e, order, byId, new HashSet<Guid>());
            return new AffiliateCommissionActivityEntry
            {
                EntryId = e.Id, RecordedAtUtc = e.RecordedAtUtc, EntryType = e.EntryType.ToString(),
                Amount = e.CommissionAmount, Currency = e.Currency, CommissionBase = e.BaseAmount,
                StoreTenantId = e.StoreTenantId, OrderId = e.OrderId, OrderItemId = e.OrderItemId, AttributionId = e.AttributionId,
                OriginalEntryId = valid ? e.OriginalEntryId : null,
                OrderNumber = valid ? order?.OrderNumber : null, ProductName = valid ? item?.ProductName : null,
                SourceOrderAvailable = valid, IsSyntheticQa = order?.IsSyntheticQa == true,
                Classification = !valid ? "UnclassifiedLineage" : order!.IsSyntheticQa ? "SyntheticQaExcluded" : "HistoryOnlyUseBalanceApi",
                ReasonCode = e.EntryType == CommissionEntryType.Reversed ? "ReversalReasonNotRecorded" : null
            };
        }).ToList();
        return new() { Entries = rows, Page = page, PageSize = pageSize, TotalCount = count, TotalPages = (int)Math.Ceiling(count / (double)pageSize) };
    }

    internal static bool HasLineage(AffiliateCommissionLedger e, AffiliateCanonicalOrder? o,
        IReadOnlyDictionary<Guid, AffiliateCommissionLedger> byId, HashSet<Guid> visited)
    {
        if (o == null || !visited.Add(e.Id) || e.StoreTenantId != o.StoreTenantId || e.AttributionId != o.AttributionId ||
            e.AffiliateAccountId != o.AffiliateAccountId || e.OrderId != o.OrderId ||
            !o.Items.Any(i => i.OrderItemId == e.OrderItemId) ||
            !string.Equals(e.Currency.Trim(), o.OrderCurrency.Trim(), StringComparison.OrdinalIgnoreCase) ||
            (o.HasUnclassifiedQaPayment && !o.IsSyntheticQa)) return false;
        if (e.EntryType == CommissionEntryType.Earned) return !e.OriginalEntryId.HasValue;
        return e.OriginalEntryId.HasValue && byId.TryGetValue(e.OriginalEntryId.Value, out var parent) &&
               parent.OrderItemId == e.OrderItemId && parent.OrderId == e.OrderId &&
               (e.EntryType == CommissionEntryType.Paid ? parent.EntryType == CommissionEntryType.Approved :
                   e.EntryType is CommissionEntryType.Approved or CommissionEntryType.Reversed && parent.EntryType == CommissionEntryType.Earned) &&
               HasLineage(parent, o, byId, visited);
    }
}

public sealed class AffiliateCanonicalOrder
{
    public Guid OrderId { get; set; }
    public Guid StoreTenantId { get; set; }
    public string? StoreName { get; set; }
    public string OrderNumber { get; set; } = "";
    public DateTime CreatedAtUtc { get; set; }
    public decimal OrderTotal { get; set; }
    public string OrderCurrency { get; set; } = "";
    public decimal ShippingCharge { get; set; }
    public decimal PaymentFee { get; set; }
    public string OrderStatus { get; set; } = "";
    public string PaymentStatus { get; set; } = "";
    public string FulfillmentStatus { get; set; } = "";
    public string PaymentMethod { get; set; } = "";
    public bool IsSyntheticQa { get; set; }
    public bool HasUnclassifiedQaPayment { get; set; }
    public string PaymentEligibilityClassification => IsSyntheticQa ? "SyntheticQaExcluded" :
        HasUnclassifiedQaPayment ? "UnclassifiedQaPayment" : "HistoryOnlyUseBalanceApi";
    public Guid AttributionId { get; set; }
    public Guid AffiliateAccountId { get; set; }
    public string ReferralCode { get; set; } = "";
    public string AttributionSource { get; set; } = "";
    public DateTime AttributedAtUtc { get; set; }
    public Guid? PromotionLinkId { get; set; }
    public Guid? PromotedProductId { get; set; }
    public List<AffiliateCanonicalItem> Items { get; set; } = [];
}

public sealed class AffiliateCanonicalItem
{
    public Guid OrderItemId { get; set; }
    public Guid ProductId { get; set; }
    public Guid? VariantId { get; set; }
    public string ProductName { get; set; } = "";
    public string? VariantName { get; set; }
    public int Quantity { get; set; }
    public decimal LinePaymentAllocated { get; set; }
    public decimal LineRefunded { get; set; }
    public bool CommissionTermsCaptured { get; set; }
    public bool CommissionEligibleAtCheckout { get; set; }
}

public sealed class AffiliateCommissionActivityPage
{
    public List<AffiliateCommissionActivityEntry> Entries { get; set; } = [];
    public int Page { get; set; }
    public int PageSize { get; set; }
    public int TotalCount { get; set; }
    public int TotalPages { get; set; }
}

public sealed class AffiliateCommissionActivityEntry
{
    public Guid EntryId { get; set; }
    public DateTime RecordedAtUtc { get; set; }
    public string EntryType { get; set; } = "";
    public decimal Amount { get; set; }
    public string Currency { get; set; } = "";
    public decimal CommissionBase { get; set; }
    public Guid StoreTenantId { get; set; }
    public Guid OrderId { get; set; }
    public Guid OrderItemId { get; set; }
    public Guid AttributionId { get; set; }
    public Guid? OriginalEntryId { get; set; }
    public string? OrderNumber { get; set; }
    public string? ProductName { get; set; }
    public bool SourceOrderAvailable { get; set; }
    public bool IsSyntheticQa { get; set; }
    public string Classification { get; set; } = "";
    public string? ReasonCode { get; set; }
}
