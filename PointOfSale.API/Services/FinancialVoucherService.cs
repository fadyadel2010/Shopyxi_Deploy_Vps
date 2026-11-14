using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using PointOfSale.Core.Entities;
using PointOfSale.Core.Enums;
using PointOfSale.Core.Financial;
using PointOfSale.Core.Interfaces.Accounting;
using PointOfSale.Core.Security;
using PointOfSale.Infrastructure.Accounting;
using PointOfSale.Infrastructure.Data;

namespace PointOfSale.API.Services;

public sealed record IssueFinancialVoucher(Guid OperationId, FinancialVoucherKind Kind, Guid SourcePaymentId, string Reason);
public sealed record ReverseFinancialVoucher(long Version, string Reason);
public sealed class FinancialVoucherException(string code, string message) : InvalidOperationException(message)
{
    public string Code { get; } = code;
}

/// <summary>Issuance never posts money twice. Reversal compensates the original journal AND party statement atomically.</summary>
public sealed class FinancialVoucherService(ApplicationDbContext db, ILedgerService ledger, IFinancialPostingService posting)
{
    public async Task<FinancialVoucher> IssueAsync(Guid tenantId, Guid actor, IssueFinancialVoucher request, CancellationToken ct = default)
    {
        Clean(); var reason = Reason(request.Reason);
        if (!Enum.IsDefined(request.Kind) || request.OperationId == Guid.Empty || request.SourcePaymentId == Guid.Empty)
            throw new FinancialVoucherException("VOUCHER_REQUEST", "Choose a receipt/customer payment or payment/supplier payment source and a unique operation ID.");
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await Actor(tenantId, actor, request.Kind, ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({tenantId + ":voucher-operation:" + request.OperationId}, 0))", ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({tenantId + ":voucher:" + request.SourcePaymentId + ":" + request.Kind}, 0))", ct);
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{request.Kind}:{request.SourcePaymentId}:{reason}")));
        var existing = await db.FinancialVouchers.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == request.OperationId, ct);
        if (existing != null)
        {
            if (existing.Id == request.OperationId && existing.RequestFingerprint == fingerprint) return existing;
            throw new FinancialVoucherException("VOUCHER_ALREADY_ISSUED", "This source payment already has a voucher, or the operation ID belongs to a different request. Open the retained voucher instead.");
        }
        if (await db.FinancialVouchers.AnyAsync(x => x.TenantId == tenantId && x.Kind == request.Kind && x.SourcePaymentId == request.SourcePaymentId, ct))
            throw new FinancialVoucherException("VOUCHER_ALREADY_ISSUED", "This source payment already has a retained voucher. Open that voucher instead.");
        if (await db.FinancialVouchers.IgnoreQueryFilters().AnyAsync(x => x.Id == request.OperationId, ct))
            throw new FinancialVoucherException("VOUCHER_OPERATION_UNAVAILABLE", "Generate a new operation ID.");
        var source = await Source(tenantId, request.Kind, request.SourcePaymentId, ct);
        var sourceType = request.Kind == FinancialVoucherKind.Receipt ? nameof(CustomerPayment) : nameof(SupplierPayment);
        var journals = await db.JournalEntries.AsNoTracking().Include(x => x.LedgerEntries).Where(x => x.TenantId == tenantId &&
            x.SourceType == sourceType && x.SourceId == request.SourcePaymentId && x.Status == JournalStatus.Posted).Take(2).ToListAsync(ct);
        if (journals.Count != 1 || journals[0].LedgerEntries.Count < 2 || journals[0].JournalDate.Date != source.Date.Date ||
            journals[0].LedgerEntries.Sum(x => x.DebitAmount) != source.Amount || journals[0].LedgerEntries.Sum(x => x.CreditAmount) != source.Amount)
            throw new FinancialVoucherException("VOUCHER_PAYMENT_NOT_POSTED", "Wait for the canonical payment accounting to finish. Exactly one balanced matching payment journal is required; voucher issuance never creates a second money posting.");
        var suffix = await db.Database.SqlQueryRaw<long>("SELECT nextval('public.financial_voucher_number_seq') AS \"Value\"").SingleAsync(ct);
        using var authority = FinancialVoucherMutationScope.Authorize();
        var voucher = new FinancialVoucher
        {
            Id = request.OperationId, TenantId = tenantId, Kind = request.Kind, SourcePaymentId = request.SourcePaymentId, RequestFingerprint = fingerprint,
            Number = $"{(request.Kind == FinancialVoucherKind.Receipt ? "RV" : "PV")}-{DateTime.UtcNow:yyyyMMdd}-{suffix}",
            PartyId = source.PartyId, PartyName = source.Name, PaymentDate = source.Date, Amount = source.Amount,
            Currency = await Currency(tenantId, ct), JournalId = journals[0].Id, Reason = reason, CreatedBy = actor, CreatedAt = DateTime.UtcNow
        };
        db.FinancialVouchers.Add(voucher); Event(voucher, actor, "Issue", reason, voucher.JournalId);
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return voucher;
    }

    public async Task<FinancialVoucher> ReverseAsync(Guid tenantId, Guid id, Guid actor, ReverseFinancialVoucher request, CancellationToken ct = default)
    {
        Clean(); var reason = Reason(request.Reason);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var voucher = await db.FinancialVouchers.FromSqlInterpolated($"SELECT * FROM \"FinancialVouchers\" WHERE \"TenantId\"={tenantId} AND \"Id\"={id} FOR UPDATE").SingleOrDefaultAsync(ct)
            ?? throw new FinancialVoucherException("VOUCHER_NOT_FOUND", "Voucher not found.");
        await db.Entry(voucher).ReloadAsync(ct);
        await Actor(tenantId, actor, voucher.Kind, ct);
        if (voucher.Status == FinancialVoucherStatus.Reversed && voucher.Version == request.Version + 1 && voucher.ReversalReason == reason) return voucher;
        if (request.Version < 1 || voucher.Status != FinancialVoucherStatus.Issued || voucher.Version != request.Version)
            throw new FinancialVoucherException("VOUCHER_CONFLICT", "Voucher status or version changed. Reload before confirming.");
        if (await Currency(tenantId, ct) != voucher.Currency)
            throw new FinancialVoucherException("VOUCHER_CURRENCY_MISMATCH", "The voucher's frozen payment currency does not match the tenant accounting currency. No conversion or compensation was applied.");
        var source = await Source(tenantId, voucher.Kind, voucher.SourcePaymentId, ct);
        if (source.Amount != voucher.Amount || source.PartyId != voucher.PartyId || source.Date != voucher.PaymentDate)
            throw new FinancialVoucherException("VOUCHER_SOURCE_CHANGED", "The original source authority changed. No compensation was applied.");
        var journal = await db.JournalEntries.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == voucher.JournalId &&
            x.SourceId == voucher.SourcePaymentId && x.SourceType == (voucher.Kind == FinancialVoucherKind.Receipt ? nameof(CustomerPayment) : nameof(SupplierPayment)) && x.Status == JournalStatus.Posted, ct);
        if (journal == null) throw new FinancialVoucherException("VOUCHER_JOURNAL_CHANGED", "The source journal is unavailable or already reversed. Reconcile the original payment before continuing.");
        using var authority = FinancialVoucherMutationScope.Authorize();
        var now = DateTime.UtcNow;
        // Party -> period -> ledger follows existing canonical party-payment ordering.
        if (voucher.Kind == FinancialVoucherKind.Receipt)
        {
            var balance = await ledger.ApplyCustomerTransaction(voucher.PartyId, now, TransactionType.DebitAdjustment, voucher.Number,
                voucher.Amount, 0, voucher.Id, reason, actor);
            db.CustomerTransactions.Add(new CustomerTransaction { Id = Guid.NewGuid(), TenantId = tenantId, CustomerId = voucher.PartyId,
                TransactionDate = now, TransactionType = TransactionType.DebitAdjustment, DebitAmount = voucher.Amount, BalanceAfter = balance,
                ReferenceId = voucher.Id, ReferenceNumber = voucher.Number, Notes = reason, CreatedBy = actor, CreatedAt = now });
            await ledger.RebuildPartyTransactionBalancesAsync(PartyType.Customer, voucher.PartyId, ct);
        }
        else
        {
            var balance = await ledger.ApplySupplierTransaction(voucher.PartyId, now, TransactionType.CreditAdjustment, voucher.Number,
                0, voucher.Amount, voucher.Id, reason, actor);
            db.SupplierTransactions.Add(new SupplierTransaction { Id = Guid.NewGuid(), TenantId = tenantId, SupplierId = voucher.PartyId,
                TransactionDate = now, TransactionType = TransactionType.CreditAdjustment, CreditAmount = voucher.Amount, BalanceAfter = balance,
                ReferenceId = voucher.Id, ReferenceNumber = voucher.Number, Notes = reason, CreatedBy = actor, CreatedAt = now });
            await ledger.RebuildPartyTransactionBalancesAsync(PartyType.Supplier, voucher.PartyId, ct);
        }
        foreach (var entry in db.ChangeTracker.Entries<AccountLedger>().Where(x => x.State == EntityState.Added && x.Entity.ReferenceId == voucher.Id))
            entry.Entity.CreatedBy = actor;
        var summary = reason.Length <= 400 ? reason : reason[..360] + "… (full reason in voucher history)";
        var reversal = await posting.ReverseJournalAsync(tenantId, voucher.JournalId, summary, actor, ct);
        // Change voucher state after the posting engine's internal SaveChanges.
        voucher.Status = FinancialVoucherStatus.Reversed; voucher.Version++; voucher.ReversalJournalId = reversal.Id;
        voucher.ReversedBy = actor; voucher.ReversedAtUtc = now; voucher.ReversalReason = reason; voucher.UpdatedBy = actor; voucher.UpdatedAt = now;
        Event(voucher, actor, "Reverse", reason, reversal.Id);
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return voucher;
    }

    private async Task Actor(Guid tenantId, Guid actor, FinancialVoucherKind kind, CancellationToken ct)
    {
        if (db.ScopedTenantId != tenantId || !db.Database.IsNpgsql() || db.Database.CurrentTransaction == null) throw new UnauthorizedAccessException();
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM \"Users\" WHERE \"TenantId\"={tenantId} AND \"Id\"={actor} FOR SHARE", ct);
        var user = await db.Users.IgnoreQueryFilters().AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == actor && x.IsActive && !x.IsDeleted, ct);
        if (user == null || !PosRolePermissionCatalog.IsGranted(user.Role, PosPermissions.FinanceOperate) ||
            !PosRolePermissionCatalog.IsGranted(user.Role, kind == FinancialVoucherKind.Receipt ? PosPermissions.CustomersBalanceManage : PosPermissions.SuppliersBalanceManage))
            throw new UnauthorizedAccessException("Current finance and party-balance authority is required.");
    }
    private async Task<(Guid PartyId, string Name, DateTime Date, decimal Amount)> Source(Guid tenantId, FinancialVoucherKind kind, Guid id, CancellationToken ct)
    {
        if (kind == FinancialVoucherKind.Receipt)
        {
            var payment = await db.CustomerPayments.AsNoTracking().Include(x => x.Customer).SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == id && !x.IsDeleted, ct);
            if (payment == null || payment.Amount <= 0 || payment.Customer.TenantId != tenantId || payment.Customer.IsDeleted || !payment.Customer.IsActive)
                throw new FinancialVoucherException("VOUCHER_SOURCE", "Select an existing positive payment for an active same-tenant customer.");
            return (payment.CustomerId, payment.Customer.Name, payment.PaymentDate, payment.Amount);
        }
        var supplierPayment = await db.SupplierPayments.AsNoTracking().Include(x => x.Supplier).SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == id && !x.IsDeleted && x.PurchaseInvoiceId == null, ct);
        if (supplierPayment == null || supplierPayment.Amount <= 0 || supplierPayment.Supplier.TenantId != tenantId || supplierPayment.Supplier.IsDeleted || !supplierPayment.Supplier.IsActive)
            throw new FinancialVoucherException("VOUCHER_SOURCE", "Select an existing standalone positive payment for an active same-tenant supplier. Invoice settlements retain their invoice authority.");
        return (supplierPayment.SupplierId, supplierPayment.Supplier.Name, supplierPayment.PaymentDate, supplierPayment.Amount);
    }
    private async Task<string> Currency(Guid tenantId, CancellationToken ct) => FinancialCurrencyGuard.RequireCurrency(
        await db.Tenants.IgnoreQueryFilters().AsNoTracking().Where(x => x.Id == tenantId).Select(x => x.Currency).SingleAsync(ct), "financialVoucher", tenantId.ToString());
    private static string Reason(string reason) => !string.IsNullOrWhiteSpace(reason) && reason.Trim().Length <= 1000 ? reason.Trim()
        : throw new FinancialVoucherException("VOUCHER_REASON", "An audit reason of up to 1000 characters is required.");
    private void Clean() { if (db.ChangeTracker.HasChanges()) throw new FinancialVoucherException("VOUCHER_PENDING_STATE", "The voucher workflow cannot flush unrelated caller changes."); }
    private void Event(FinancialVoucher voucher, Guid actor, string action, string reason, Guid journalId) => db.FinancialVoucherEvents.Add(new FinancialVoucherEvent
    { Id = Guid.NewGuid(), TenantId = voucher.TenantId, FinancialVoucherId = voucher.Id, Version = voucher.Version, Action = action, Reason = reason, JournalId = journalId, CreatedBy = actor, CreatedAt = DateTime.UtcNow });
}
