using Microsoft.EntityFrameworkCore;
using PointOfSale.Core.Entities;
using PointOfSale.Core.Entities.Accounting;
using PointOfSale.Core.Enums;
using PointOfSale.Core.Interfaces;
using PointOfSale.Infrastructure.Data;

namespace PointOfSale.Infrastructure.Accounting;

/// <summary>
/// Rejects new financial source writes before any source, stock or balance in
/// that SaveChanges can commit. Posting itself is independently checked by the
/// journal engine. Callers that save several times still own the outer business
/// transaction; this guard never commits that transaction.
/// </summary>
internal static class AccountingPeriodSourceGuard
{
    public static IReadOnlyList<(Guid TenantId, DateTime Date)> PendingDates(ApplicationDbContext db)
    {
        db.ChangeTracker.DetectChanges();
        var dates = new List<(Guid, DateTime)>();
        foreach (var entry in db.ChangeTracker.Entries().Where(x => x.State is EntityState.Added or EntityState.Modified or EntityState.Deleted).ToList())
        {
            // Line-only changes must not bypass the document's date authority.
            BaseEntity? parent = entry.Entity switch
            {
                SalesInvoiceItem x => db.SalesInvoices.Local.SingleOrDefault(p => p.Id == x.SalesInvoiceId),
                PurchaseInvoiceItem x => db.PurchaseInvoices.Local.SingleOrDefault(p => p.Id == x.PurchaseInvoiceId),
                ReturnItem x => db.Returns.Local.SingleOrDefault(p => p.Id == x.ReturnId),
                _ => null
            };
            if (entry.Entity is SalesInvoiceItem or PurchaseInvoiceItem or ReturnItem)
            {
                if (parent == null)
                    throw new AccountingPeriodException("PERIOD_SOURCE_REQUIRED", "Financial line changes require the tracked source document and its original date.");
                if (parent is SalesInvoice sale && sale.Status != InvoiceStatus.Draft) dates.Add((sale.TenantId, sale.InvoiceDate));
                if (parent is PurchaseInvoice purchase && purchase.Status != InvoiceStatus.Draft) dates.Add((purchase.TenantId, purchase.InvoiceDate));
                if (parent is Return returned) dates.Add((returned.TenantId, returned.ReturnDate));
                continue;
            }
            if (entry.Entity is not ITenantOwned owned) continue;
            // Existing source metadata/status changes do not rewrite its journal.
            // Financial edits, including moving dates, must check both old and new dates.
            string? dateProperty = entry.Entity switch
            {
                SalesInvoice x when x.Status != InvoiceStatus.Draft => nameof(x.InvoiceDate),
                PurchaseInvoice x when x.Status != InvoiceStatus.Draft => nameof(x.InvoiceDate),
                Return x => nameof(x.ReturnDate),
                CustomerPayment x => nameof(x.PaymentDate),
                SupplierPayment x => nameof(x.PaymentDate),
                PartyLedgerAdjustment x => nameof(x.AdjustmentDate),
                AccountLedger x => nameof(x.TransactionDate),
                Expense x when !x.RequiresApproval || x.IsApproved => nameof(x.ExpenseDate),
                JournalEntry x when entry.State == EntityState.Added => nameof(x.JournalDate),
                AccountingOutboxMessage x when entry.State == EntityState.Added => nameof(x.OccurredAtUtc),
                _ => null
            };
            if (dateProperty == null) continue;
            var draftAdmission = entry.State == EntityState.Modified && (entry.Entity is SalesInvoice or PurchaseInvoice) &&
                entry.Property("Status").IsModified && (InvoiceStatus)entry.Property("Status").OriginalValue! == InvoiceStatus.Draft;
            var financialChange = entry.State is EntityState.Added or EntityState.Deleted || draftAdmission || entry.Properties.Any(p => p.IsModified &&
                (p.Metadata.Name == dateProperty || p.Metadata.Name.Contains("Amount", StringComparison.Ordinal) ||
                 p.Metadata.Name is "Subtotal" or "IsApproved" or "AccountingMutationVersion" or "CustomerId" or "SupplierId" or "WarehouseId" or "PaymentMethod"));
            if (!financialChange) continue;
            dates.Add((owned.TenantId, (DateTime)entry.Property(dateProperty).CurrentValue!));
            if (entry.State == EntityState.Modified)
                dates.Add((owned.TenantId, (DateTime)entry.Property(dateProperty).OriginalValue!));
        }
        return dates.Distinct().OrderBy(x => x.Item1).ThenBy(x => x.Item2).ToList();
    }
}
