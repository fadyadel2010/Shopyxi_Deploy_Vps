using MassTransit;
using Microsoft.EntityFrameworkCore;
using MongoDB.Driver;
using PointOfSale.Core.Entities.Affiliate;
using PointOfSale.Core.Events.Affiliate;
using PointOfSale.Infrastructure.Data;
using PointOfSale.Infrastructure.MongoDB.Documents.Affiliate;
using PointOfSale.Infrastructure.Services.Affiliate;

namespace PointOfSale.Infrastructure.Consumers.Affiliate;

public partial class AffiliateCommissionPaidProjectionConsumer
{
    private readonly ApplicationDbContext? _canonicalDb;

    public async Task Consume(ConsumeContext<AffiliateManualPayoutReturnedEvent> context)
    {
        var e = context.Message;
        if (_canonicalDb == null || !await _canonicalDb.AffiliateManualPayoutReturns.AnyAsync(r =>
            r.Id == e.ReturnId && r.StoreTenantId == e.StoreTenantId && r.AffiliateAccountId == e.AffiliateAccountId &&
            _canonicalDb.AffiliateManualPayoutReturnAllocations.Any(a => a.ReturnId == r.Id &&
                _canonicalDb.AffiliateCommissionLedger.IgnoreQueryFilters().Any(p => p.Id == a.PaidEntryId && p.OrderId == e.OrderId)), context.CancellationToken))
            throw AffiliatePayoutReturnAuthority.Conflict();
        await RebuildPaidFromSqlAsync(e.StoreTenantId, e.TenantId, e.OrderId, e.AffiliateAccountId,
            e.EventVersion, e.EventId, context.CancellationToken);
    }

    private async Task RebuildPaidFromSqlAsync(Guid store, Guid? eventTenant, Guid orderId, Guid affiliate,
        int version, Guid eventId, CancellationToken ct)
    {
        AffiliateMoneyProjectionConsistency.ValidateAuthorityMetadata(store, eventTenant, version, orderId, "AffiliatePaidHistory");
        var upstream = await _orders.Find(d => d.TenantId == store && d.OrderId == orderId && d.AffiliateAccountId == affiliate).FirstOrDefaultAsync(ct)
            ?? throw new InvalidOperationException("AFFILIATE_PAID_PROJECTION_UPSTREAM_INVALID: Earned projection must exist before paid history.");
        var previousUpdatedAt = upstream.UpdatedAtUtc;
        var eligibility = await AffiliateCommissionEligibility.LoadAsync(_canonicalDb!, affiliate, store, ct);
        var entries = eligibility.RealEntries.Where(e => e.OrderId == orderId).ToList();
        var earned = entries.Where(e => e.EntryType == CommissionEntryType.Earned).ToList();
        if (earned.Count == 0 || earned.Count != upstream.Items.Count || upstream.Currency != earned[0].Currency ||
            upstream.Items.Any(i => earned.Count(e => e.OrderItemId == i.OrderItemId) != 1))
            throw new InvalidOperationException("AFFILIATE_PAID_PROJECTION_UPSTREAM_INVALID: SQL item lineage must match the Earned projection.");
        foreach (var item in upstream.Items)
        {
            var history = entries.Where(e => e.OrderItemId == item.OrderItemId).ToList();
            var amount = history.Where(e => e.EntryType == CommissionEntryType.Earned).Sum(e => e.CommissionAmount);
            var reversed = history.Where(e => e.EntryType == CommissionEntryType.Reversed).Sum(e => e.CommissionAmount);
            item.SettlementStatus = reversed >= amount ? "Reversed" : reversed > 0 ? "PartiallyReversed" :
                history.Any(e => e.EntryType == CommissionEntryType.Paid) ? "Paid" :
                history.Any(e => e.EntryType == CommissionEntryType.Approved) ? "Approved" : "Earned";
        }
        // Recompose, never decrement a projection from an event amount. Delayed Paid,
        // return, replay and re-payment envelopes all converge to the same SQL facts.
        var statuses = upstream.Items.Select(i => i.SettlementStatus).ToList();
        var status = statuses.All(s => s == "Reversed") ? "Reversed" : statuses.Any(s => s is "Reversed" or "PartiallyReversed") ? "PartiallyReversed" :
            statuses.All(s => s == "Paid") ? "Paid" : statuses.All(s => s is "Paid" or "Approved") ? "Approved" : "Earned";
        var update = Builders<AffiliateOrderDocument>.Update.Set(d => d.Items, upstream.Items)
            .Set(d => d.TotalPaid, entries.Where(e => e.EntryType == CommissionEntryType.Paid).Sum(e => e.CommissionAmount))
            .Set(d => d.Status, status).Set(d => d.UpdatedAtUtc, DateTime.UtcNow)
            .Inc(d => d.PaidHistoryRevision, 1)
            .Max(d => d.LastCommissionPaidEventVersion, version).AddToSet(d => d.ProcessedEventIds, eventId.ToString());
        var revision = Builders<AffiliateOrderDocument>.Filter.Eq(d => d.PaidHistoryRevision, upstream.PaidHistoryRevision);
        if (upstream.PaidHistoryRevision == 0)
            revision |= Builders<AffiliateOrderDocument>.Filter.Exists("paidHistoryRevision", false);
        var filter = Builders<AffiliateOrderDocument>.Filter.Where(d => d.Id == upstream.Id && d.TenantId == store &&
            d.AffiliateAccountId == affiliate && d.UpdatedAtUtc == previousUpdatedAt) & revision;
        var result = await _orders.UpdateOneAsync(filter, update, cancellationToken: ct);
        if (result.MatchedCount != 1)
            throw new InvalidOperationException("AFFILIATE_PAID_PROJECTION_NOT_APPLIED: Concurrent projection update; replay from current SQL authority.");
        await AffiliateMoneyProjectionConsistency.RecomputeAffiliateMoneyAggregatesAsync(_orders, _dashboards, _summaries, store, affiliate, null, ct);
    }
}
