using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PointOfSale.Core.Entities.Affiliate;
using PointOfSale.Core.Enums;
using PointOfSale.Core.Exceptions.Affiliate;
using PointOfSale.Infrastructure.Data;

namespace PointOfSale.Infrastructure.Services.Affiliate;

/// <summary>
/// Payment classification of immutable commission history, independent of QA runtime configuration.
/// All derived entries must resolve to the same persisted Earned/order/item/attribution lineage.
/// Never drop an unclassifiable reversal: block the scope instead of increasing its net balance.
/// </summary>
public sealed record AffiliateCommissionEligibility(
    IReadOnlyList<AffiliateCommissionLedger> RealEntries,
    IReadOnlyList<AffiliateCommissionLedger> SyntheticEntries)
{
    public decimal ReturnedWithdrawalAmount { get; init; }
    public decimal NetApproved => NetApprovedAmount(RealEntries);
    public decimal SyntheticApprovedExcluded => NetApprovedAmount(SyntheticEntries);

    public static decimal NetApprovedAmount(IEnumerable<AffiliateCommissionLedger> entries) => entries
        .GroupBy(e => e.OrderItemId)
        .Sum(g => Math.Max(0m,
            g.Where(e => e.EntryType == CommissionEntryType.Approved).Sum(e => e.CommissionAmount) -
            g.Where(e => e.EntryType == CommissionEntryType.Reversed).Sum(e => e.CommissionAmount)));

    public static async Task<AffiliateCommissionEligibility> LoadAsync(
        ApplicationDbContext db, Guid affiliateAccountId, Guid? storeTenantId,
        CancellationToken ct = default, ILogger? logger = null)
    {
        var entries = await db.AffiliateCommissionLedger.IgnoreQueryFilters().AsNoTracking()
            .Where(e => e.AffiliateAccountId == affiliateAccountId &&
                (!storeTenantId.HasValue || e.StoreTenantId == storeTenantId.Value)).ToListAsync(ct);
        var orderIds = entries.Select(e => e.OrderId).Distinct().ToArray();
        var itemIds = entries.Select(e => e.OrderItemId).Distinct().ToArray();
        var attributionIds = entries.Select(e => e.AttributionId).Distinct().ToArray();
        var orders = await db.Orders.IgnoreQueryFilters().AsNoTracking().Where(o => orderIds.Contains(o.Id))
            .Select(o => new { o.Id, o.TenantId, o.Currency, o.PaymentStatus }).ToDictionaryAsync(o => o.Id, ct);
        var items = await db.OrderItems.IgnoreQueryFilters().AsNoTracking().Where(i => itemIds.Contains(i.Id))
            .Select(i => new { i.Id, i.OrderId }).ToDictionaryAsync(i => i.Id, ct);
        var attributions = await db.AffiliateAttributions.IgnoreQueryFilters().AsNoTracking()
            .Where(a => attributionIds.Contains(a.Id))
            .Select(a => new { a.Id, a.OrderId, a.TenantId, a.AffiliateAccountId }).ToDictionaryAsync(a => a.Id, ct);
        // Any durable QA artifact is exclusion evidence; a collected state without these is ambiguous.
        var syntheticOrders = (await db.SyntheticQaOrders.IgnoreQueryFilters().AsNoTracking()
            .Where(q => orderIds.Contains(q.OrderId)).Select(q => q.OrderId)
            .Concat(db.SyntheticQaEvidence.IgnoreQueryFilters().Where(q => orderIds.Contains(q.OrderId)).Select(q => q.OrderId))
            .Concat(db.SyntheticQaCommissionPostings.IgnoreQueryFilters().Where(q => orderIds.Contains(q.OrderId)).Select(q => q.OrderId))
            .Distinct().ToListAsync(ct)).ToHashSet();
        var byId = entries.ToDictionary(e => e.Id);
        var classified = new Dictionary<Guid, bool>();
        var problems = new HashSet<Guid>();

        bool? Classify(AffiliateCommissionLedger entry, HashSet<Guid> visiting)
        {
            if (classified.TryGetValue(entry.Id, out var cached)) return cached;
            if (!visiting.Add(entry.Id)) { problems.Add(entry.Id); return null; }
            if (!orders.TryGetValue(entry.OrderId, out var order) || order.TenantId != entry.StoreTenantId ||
                !items.TryGetValue(entry.OrderItemId, out var item) || item.OrderId != order.Id ||
                !attributions.TryGetValue(entry.AttributionId, out var attribution) ||
                attribution.OrderId != order.Id || attribution.TenantId != entry.StoreTenantId ||
                attribution.AffiliateAccountId != entry.AffiliateAccountId ||
                string.IsNullOrWhiteSpace(entry.Currency) ||
                !string.Equals(entry.Currency.Trim(), order.Currency?.Trim(), StringComparison.OrdinalIgnoreCase) ||
                (order.PaymentStatus == PaymentStatus.SyntheticQaCollected && !syntheticOrders.Contains(order.Id)))
            { problems.Add(entry.Id); return null; }

            var synthetic = syntheticOrders.Contains(order.Id);
            if (entry.EntryType == CommissionEntryType.Earned)
            {
                if (entry.OriginalEntryId.HasValue) { problems.Add(entry.Id); return null; }
            }
            else
            {
                if (!entry.OriginalEntryId.HasValue || !byId.TryGetValue(entry.OriginalEntryId.Value, out var original) ||
                    original.OrderId != entry.OrderId || original.OrderItemId != entry.OrderItemId ||
                    original.AttributionId != entry.AttributionId || original.StoreTenantId != entry.StoreTenantId ||
                    original.AffiliateAccountId != entry.AffiliateAccountId ||
                    !string.Equals(original.Currency.Trim(), entry.Currency.Trim(), StringComparison.OrdinalIgnoreCase) ||
                    (entry.EntryType == CommissionEntryType.Paid ? original.EntryType != CommissionEntryType.Approved :
                        entry.EntryType is not (CommissionEntryType.Approved or CommissionEntryType.Reversed) ||
                        original.EntryType != CommissionEntryType.Earned))
                { problems.Add(entry.Id); return null; }
                var parent = Classify(original, visiting);
                if (!parent.HasValue) { problems.Add(entry.Id); return null; }
                synthetic |= parent.Value;
            }
            visiting.Remove(entry.Id);
            classified[entry.Id] = synthetic;
            return synthetic;
        }

        foreach (var entry in entries) Classify(entry, new HashSet<Guid>());
        if (problems.Count > 0)
        {
            var message = $"AFFILIATE_COMMISSION_LINEAGE_UNCLASSIFIED: Payment eligibility blocked. Affiliate={affiliateAccountId}; Store={storeTenantId}; Entries={string.Join(",", problems.Order())}";
            logger?.LogError("{ClassificationProblem}", message);
            throw new AffiliatePayoutAuthorityException(message);
        }
        var returned = await AffiliatePayoutReturnAuthority.LoadAsync(db, affiliateAccountId, storeTenantId, ct);
        // Keep raw append-only activity accessible separately; financial/lifecycle
        // totals use only Paid events whose exact payout has not been returned.
        return new(entries.Where(e => !classified[e.Id] && !returned.PaidEntryIds.Contains(e.Id)).ToList(),
            entries.Where(e => classified[e.Id]).ToList()) { ReturnedWithdrawalAmount = returned.Amount };
    }
}
