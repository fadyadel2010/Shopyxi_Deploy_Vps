using Microsoft.EntityFrameworkCore;
using PointOfSale.Core.Enums;
using PointOfSale.Core.Interfaces.Accounting;
using PointOfSale.Infrastructure.Data;
using PointOfSale.Core.Financial;

namespace PointOfSale.Infrastructure.Analytics;

public interface IStorefrontAnalyticsAuthorityReader
{
    Task<IReadOnlyDictionary<Guid, DateTime>> GetRecognizedOrderActivityAsync(
        Guid tenantId, DateTime startUtc, DateTime endUtc, CancellationToken cancellationToken = default);
    Task<StorefrontCommerceAnalyticsSnapshot> GetCommerceSnapshotAsync(
        Guid tenantId,
        DateTime startUtc,
        DateTime endUtc,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyDictionary<Guid, decimal>> GetNetRevenueByOrderAsync(
        Guid tenantId,
        IEnumerable<Guid> orderIds,
        DateTime asOfUtc,
        CancellationToken cancellationToken = default);
}

public sealed record StorefrontCommerceAnalyticsSnapshot(
    IReadOnlyList<Guid> PurchaseOrderIds,
    IReadOnlyDictionary<Guid, decimal> NetRevenueByOrder)
{
    public long PurchaseCount => PurchaseOrderIds.Count;
    public decimal NetRevenue => NetRevenueByOrder.Values.Sum();
    public decimal RecognizedRevenue { get; init; }
    public IReadOnlyDictionary<Guid, decimal> RecognizedRevenueByOrder { get; init; } = new Dictionary<Guid, decimal>();
    public decimal Adjustments => NetRevenue - RecognizedRevenue;
    public string CurrencyCode { get; init; } = string.Empty;
    public string FinancialBasis => "journal-date-utc-period-movement";
}

/// <summary>
/// Reads storefront purchase and revenue truth from PostgreSQL commerce and accounting domains.
/// Redis and MongoDB analytics projections must never replace this reader for financial metrics.
/// </summary>
public sealed class StorefrontAnalyticsAuthorityReader : IStorefrontAnalyticsAuthorityReader
{
    private static readonly string[] StorefrontRevenueSourceTypes =
    [
        "Order_Payment",
        "Order_COD",
        "Order_Wallet",
        "Order_UnresolvedPaymentClearance"
    ];

    private readonly ApplicationDbContext _dbContext;

    public StorefrontAnalyticsAuthorityReader(ApplicationDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    // Explicit operational posting activity, intentionally not period financial revenue.
    public async Task<IReadOnlyDictionary<Guid, DateTime>> GetRecognizedOrderActivityAsync(
        Guid tenantId, DateTime startUtc, DateTime endUtc, CancellationToken cancellationToken = default)
    {
        ValidateRange(tenantId, startUtc, endUtc);
        var currency = await RequireTenantCurrencyAsync(tenantId, cancellationToken);
        var rows = await _dbContext.LedgerEntries.AsNoTracking()
            .Where(entry => entry.TenantId == tenantId && entry.JournalEntry.TenantId == tenantId &&
                            entry.Account.TenantId == tenantId &&
                            (entry.JournalEntry.Status == JournalStatus.Posted || entry.JournalEntry.Status == JournalStatus.Reversed) &&
                            StorefrontRevenueSourceTypes.Contains(entry.JournalEntry.SourceType) &&
                            entry.Account.AccountCode == AccountCodes.EcommerceSales && entry.CreditAmount > 0m &&
                            entry.JournalEntry.PostedAtUtc >= startUtc && entry.JournalEntry.PostedAtUtc < endUtc)
            .GroupBy(entry => entry.JournalEntry.SourceId)
            .Select(g => new { OrderId = g.Key, PostedAtUtc = g.Max(entry => entry.JournalEntry.PostedAtUtc) })
            .ToListAsync(cancellationToken);
        var orders = await ReadOrdersAsync(tenantId, rows.Select(x => x.OrderId).ToArray(), currency, cancellationToken);
        if (rows.Any(x => !orders.Contains(x.OrderId)))
            throw new InvalidOperationException("Recognition activity has no matching tenant-scoped commerce order.");
        return rows.Where(x => orders.Contains(x.OrderId)).ToDictionary(x => x.OrderId, x => x.PostedAtUtc);
    }

    public async Task<StorefrontCommerceAnalyticsSnapshot> GetCommerceSnapshotAsync(
        Guid tenantId,
        DateTime startUtc,
        DateTime endUtc,
        CancellationToken cancellationToken = default)
    {
        ValidateRange(tenantId, startUtc, endUtc);

        var currency = await RequireTenantCurrencyAsync(tenantId, cancellationToken);
        var movements = await ReadMovementsAsync(tenantId, startUtc, endUtc, cancellationToken);
        var orderIds = movements.Select(x => x.OrderId).Distinct().ToArray();
        var orders = await ReadOrdersAsync(tenantId, orderIds, currency, cancellationToken);
        if (orderIds.Except(orders).Any())
            throw new InvalidOperationException("Order revenue has no matching tenant-scoped commerce order.");
        var eligible = movements.Where(x => orders.Contains(x.OrderId)).ToArray();
        var recognized = eligible.Where(x => x.IsRecognition && x.Credit > 0m).ToArray();
        return new StorefrontCommerceAnalyticsSnapshot(
            recognized.Select(x => x.OrderId).Distinct().ToArray(),
            eligible.GroupBy(x => x.OrderId).ToDictionary(g => g.Key, g => g.Sum(x => x.Net)))
        {
            RecognizedRevenue = recognized.Sum(x => x.Credit),
            RecognizedRevenueByOrder = recognized.GroupBy(x => x.OrderId).ToDictionary(g => g.Key, g => g.Sum(x => x.Credit)),
            CurrencyCode = currency
        };
    }

    public async Task<IReadOnlyDictionary<Guid, decimal>> GetNetRevenueByOrderAsync(
        Guid tenantId,
        IEnumerable<Guid> orderIds,
        DateTime asOfUtc,
        CancellationToken cancellationToken = default)
    {
        ValidateRange(tenantId, DateTime.SpecifyKind(DateTime.MinValue, DateTimeKind.Utc), asOfUtc);
        var currency = await RequireTenantCurrencyAsync(tenantId, cancellationToken);

        var distinctOrderIds = orderIds
            .Where(id => id != Guid.Empty)
            .Distinct()
            .ToArray();

        if (distinctOrderIds.Length == 0)
        {
            return new Dictionary<Guid, decimal>();
        }

        var orders = await ReadOrdersAsync(tenantId, distinctOrderIds, currency, cancellationToken);
        var movements = await ReadMovementsAsync(tenantId, null, asOfUtc, cancellationToken, orders);
        var revenue = movements.Where(x => orders.Contains(x.OrderId))
            .GroupBy(x => x.OrderId).ToDictionary(g => g.Key, g => g.Sum(x => x.Net));

        foreach (var orderId in orders)
        {
            revenue.TryAdd(orderId, 0m);
        }

        return revenue;
    }

    private async Task<string> RequireTenantCurrencyAsync(Guid tenantId, CancellationToken ct)
    {
        return await PointOfSale.Infrastructure.Accounting.FinancialReportingCurrency.RequireTenantAsync(_dbContext, tenantId, ct);
    }

    private async Task<HashSet<Guid>> ReadOrdersAsync(Guid tenantId, Guid[] ids, string currency, CancellationToken ct)
    {
        // Deletion never erases posted financial history. Tenant isolation is explicit.
        var orders = await _dbContext.Orders.IgnoreQueryFilters().AsNoTracking()
            .Where(order => order.TenantId == tenantId && order.Source == OrderSource.Ecommerce && ids.Contains(order.Id))
            .Select(order => new { order.Id, order.Currency }).ToListAsync(ct);
        foreach (var order in orders)
            FinancialCurrencyGuard.RequireExpectedCurrency(order.Currency, currency, "order.currency", $"order:{order.Id}");
        return orders.Select(x => x.Id).ToHashSet();
    }

    private async Task<List<OrderMovement>> ReadMovementsAsync(Guid tenantId, DateTime? startUtc, DateTime endUtc,
        CancellationToken ct, IReadOnlyCollection<Guid>? orderFilter = null)
    {
        var query = _dbContext.LedgerEntries.AsNoTracking()
            .Where(entry => entry.TenantId == tenantId && entry.JournalEntry.TenantId == tenantId &&
                            entry.Account.TenantId == tenantId &&
                            (entry.JournalEntry.Status == JournalStatus.Posted || entry.JournalEntry.Status == JournalStatus.Reversed) &&
                            entry.Account.AccountCode == AccountCodes.EcommerceSales &&
                            entry.JournalEntry.JournalDate < endUtc);
        if (startUtc.HasValue) query = query.Where(entry => entry.JournalEntry.JournalDate >= startUtc.Value);
        if (orderFilter != null)
        {
            // Lifetime attribution for one order must not load every order's ledger.
            var ids = orderFilter.ToArray();
            var roots = await _dbContext.JournalEntries.AsNoTracking()
                .Where(j => j.TenantId == tenantId && ids.Contains(j.SourceId) &&
                    (StorefrontRevenueSourceTypes.Contains(j.SourceType) || j.SourceType == "Order_Refund"))
                .Select(j => j.Id).ToListAsync(ct);
            var reachable = roots.ToHashSet();
            var frontier = roots.ToArray();
            while (frontier.Length > 0)
            {
                var children = await _dbContext.JournalEntries.AsNoTracking()
                    .Where(j => j.TenantId == tenantId && j.SourceType == "Reversal" &&
                        j.ReversesJournalId.HasValue && frontier.Contains(j.ReversesJournalId.Value))
                    .Select(j => j.Id).ToListAsync(ct);
                frontier = children.Where(id => reachable.Add(id)).ToArray();
            }
            var journalsForOrders = reachable.ToArray();
            query = query.Where(entry => journalsForOrders.Contains(entry.JournalEntryId));
        }
        var lines = await query.Select(entry => new
        {
            JournalId = entry.JournalEntryId,
            entry.JournalEntry.SourceId, entry.JournalEntry.SourceType, entry.JournalEntry.ReversesJournalId,
            entry.CreditAmount, entry.DebitAmount
        }).ToListAsync(ct);
        var journals = lines.GroupBy(x => x.JournalId).ToDictionary(g => g.Key,
            g => new Origin(g.First().SourceId, g.First().SourceType, g.First().ReversesJournalId));
        // Resolve ancestors in batches. SourceReference is display text, never identity.
        // Reversing a reversal is legal; detect cycles rather than guessing an order.
        var attempted = new HashSet<Guid>(journals.Keys);
        while (true)
        {
            var missing = journals.Values.Where(x => x.Reverses.HasValue)
                .Select(x => x.Reverses!.Value).Where(id => attempted.Add(id)).Distinct().ToArray();
            if (missing.Length == 0) break;
            var parents = await _dbContext.JournalEntries.AsNoTracking()
                .Where(j => j.TenantId == tenantId && missing.Contains(j.Id))
                .Select(j => new { j.Id, j.SourceId, j.SourceType, j.ReversesJournalId }).ToListAsync(ct);
            foreach (var parent in parents)
                journals[parent.Id] = new(parent.SourceId, parent.SourceType, parent.ReversesJournalId);
        }
        var result = new List<OrderMovement>();
        foreach (var line in lines)
        {
            var origin = journals[line.JournalId];
            var visited = new HashSet<Guid> { line.JournalId };
            while (origin.SourceType == "Reversal")
            {
                if (!origin.Reverses.HasValue || !visited.Add(origin.Reverses.Value) ||
                    !journals.TryGetValue(origin.Reverses.Value, out var parent))
                    throw new InvalidOperationException("Unresolvable tenant-scoped revenue reversal.");
                origin = parent;
            }
            if (!StorefrontRevenueSourceTypes.Contains(origin.SourceType) && origin.SourceType != "Order_Refund")
                continue; // e.g. CommerceHub commission and its reversals are not order sales.
            result.Add(new(origin.SourceId, line.CreditAmount - line.DebitAmount, line.CreditAmount,
                StorefrontRevenueSourceTypes.Contains(line.SourceType)));
        }
        return result;
    }

    private sealed record Origin(Guid SourceId, string SourceType, Guid? Reverses);
    private sealed record OrderMovement(Guid OrderId, decimal Net, decimal Credit, bool IsRecognition);

    private static void ValidateRange(Guid tenantId, DateTime startUtc, DateTime endUtc)
    {
        if (tenantId == Guid.Empty)
        {
            throw new ArgumentException("TenantId is required.", nameof(tenantId));
        }

        if (startUtc.Kind != DateTimeKind.Utc || endUtc.Kind != DateTimeKind.Utc)
        {
            throw new ArgumentException("Analytics ranges must use UTC timestamps.");
        }

        if (endUtc <= startUtc)
        {
            throw new ArgumentOutOfRangeException(nameof(endUtc), "EndUtc must be later than StartUtc.");
        }
    }
}
