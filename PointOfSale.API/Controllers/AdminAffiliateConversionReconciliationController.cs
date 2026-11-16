using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MongoDB.Driver;
using PointOfSale.Core.Interfaces;
using PointOfSale.Infrastructure.Data;
using PointOfSale.Infrastructure.MongoDB.Documents.Affiliate;
using PointOfSale.Infrastructure.MongoDB.Services;
using PointOfSale.Infrastructure.Services.Affiliate;

namespace PointOfSale.API.Controllers;

/// <summary>Bounded analytics-only repair. It cannot create/change orders,
/// attribution, payment, fulfillment, commission, stock or communications.</summary>
[Authorize(Roles = "Owner,Admin")]
[ApiController]
[Route("api/admin/affiliate-analytics/conversions/reconciliation")]
public sealed class AdminAffiliateConversionReconciliationController(
    ApplicationDbContext sql, MongoDbContext mongo, ITenantContext tenant,
    AffiliateConversionProjector projector) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Inspect(CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-store";
        var tenantId = tenant.RequiredTenantId;
        var source = await sql.AffiliateAttributions.AsNoTracking().IgnoreQueryFilters()
            .Where(a => a.TenantId == tenantId && !a.IsDeleted &&
                sql.Orders.IgnoreQueryFilters().Any(o => o.TenantId == tenantId && o.Id == a.OrderId))
            .Select(a => new { a.OrderId, a.ReferralClickId }).ToListAsync(ct);
        var ids = source.Select(x => x.OrderId).ToList();
        var projected = await mongo.AffiliateOrderConversions.Find(x =>
            x.TenantId == tenantId && ids.Contains(x.OrderId)).Project(x => x.OrderId).ToListAsync(ct);
        return Ok(new
        {
            authoritativeAttributedOrders = source.Count,
            alreadyProjected = projected.Count,
            unprojectedOrders = source.Count(x => !projected.Contains(x.OrderId)),
            exactClickCandidates = source.Count(x => x.ReferralClickId.HasValue && !projected.Contains(x.OrderId)),
            ambiguousHistoricalClickLinksAreExcluded = true
        });
    }

    [HttpPost("{orderId:guid}")]
    public async Task<IActionResult> Repair(Guid orderId, CancellationToken ct)
    {
        var tenantId = tenant.RequiredTenantId;
        // Projector re-reads all SQL relationships and uses insert/conditional
        // semantics. Exact replay returns the same final projection.
        await projector.ProjectAsync(tenantId, orderId, ct: ct);
        var result = await mongo.AffiliateOrderConversions.Find(x =>
            x.TenantId == tenantId && x.OrderId == orderId).SingleAsync(ct);
        return Ok(new { result.OrderId, result.AttributionId, result.ClickId,
            result.ProductId, result.ConvertedAtUtc, idempotent = true });
    }
}
