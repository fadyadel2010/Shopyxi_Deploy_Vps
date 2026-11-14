using Microsoft.EntityFrameworkCore;
using PointOfSale.Core.Entities;
using PointOfSale.Core.Financial;
using PointOfSale.Infrastructure.Data;

namespace PointOfSale.Infrastructure.Accounting;

/// <summary>
/// POS payments/journals have no original-currency/rate snapshot. Until an audited
/// rebase workflow exists, a persisted tenant's accounting currency is immutable,
/// even with zero current balances. Compare SQL, not caller-supplied original values.
/// </summary>
internal static class TenantAccountingCurrencyGuard
{
    public static async Task ValidateAsync(ApplicationDbContext db, bool useAsync, CancellationToken ct = default)
    {
        db.ChangeTracker.DetectChanges();
        var pending = db.ChangeTracker.Entries<Tenant>()
            .Where(x => x.State == EntityState.Modified && x.Property(t => t.Currency).IsModified).ToList();
        foreach (var entry in pending)
        {
            var query = db.Tenants.IgnoreQueryFilters().AsNoTracking().Where(t => t.Id == entry.Entity.Id).Select(t => new { t.Currency });
            var stored = useAsync ? await query.SingleOrDefaultAsync(ct) : query.SingleOrDefault();
            if (stored == null) throw new DbUpdateConcurrencyException("The store changed or is no longer available. Reload before updating.");
            var context = entry.Entity.Id.ToString();
            var persisted = FinancialCurrencyGuard.RequireCurrency(stored.Currency, "tenant.accountingCurrency", context);
            var requested = FinancialCurrencyGuard.RequireCurrency(entry.Entity.Currency, "tenant.requestedAccountingCurrency", context);
            if (persisted != requested) throw new AccountingCurrencyChangeException();
            entry.Entity.Currency = persisted;
        }
    }
}
