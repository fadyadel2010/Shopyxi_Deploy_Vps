using Microsoft.EntityFrameworkCore;
using PointOfSale.Core.Financial;
using PointOfSale.Infrastructure.Data;

namespace PointOfSale.Infrastructure.Accounting;

public static class FinancialReportingCurrency
{
    public static async Task<string> RequireTenantAsync(ApplicationDbContext db, Guid tenantId, CancellationToken ct = default)
    {
        if (tenantId == Guid.Empty) throw new ArgumentException("Tenant is required.", nameof(tenantId));
        var currency = await db.Tenants.IgnoreQueryFilters().AsNoTracking()
            .Where(t => t.Id == tenantId).Select(t => t.Currency).SingleOrDefaultAsync(ct);
        return FinancialCurrencyGuard.RequireEgpCurrency(currency, "tenant.currency", "financial-reporting");
    }
}
