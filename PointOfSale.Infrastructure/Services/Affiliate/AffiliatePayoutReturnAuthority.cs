using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PointOfSale.Core.Entities.Affiliate;
using PointOfSale.Core.Entities.Accounting;
using PointOfSale.Core.Interfaces.Accounting;
using PointOfSale.Core.Enums;
using PointOfSale.Core.Models.Accounting;
using PointOfSale.Infrastructure.Accounting;
using PointOfSale.Infrastructure.Data;

namespace PointOfSale.Infrastructure.Services.Affiliate;

/// <summary>Returned cash, not an arbitrary positive adjustment, restores payout authority.
/// Every read verifies the complete immutable return tuple. Generic journals/bonuses cannot mint funds.</summary>
public static class AffiliatePayoutReturnAuthority
{
    public const string SourceType = "AffiliateManualPayoutReturn";
    public static string Key(Guid store, Guid request) => $"AFFILIATE_MANUAL_RETURN:v1:{store}:{request}";

    /// <summary>SQL projection after the caller has validated this affiliate/store scope
    /// with LoadAsync (or AffiliateCommissionEligibility.LoadAsync) in the same unit of work.</summary>
    public static IQueryable<AffiliateCommissionLedger> EffectiveHistory(ApplicationDbContext db) =>
        db.AffiliateCommissionLedger.IgnoreQueryFilters().Where(e => e.EntryType != CommissionEntryType.Paid ||
            !db.AffiliateManualPayoutReturnAllocations.Any(a => a.PaidEntryId == e.Id &&
                db.AffiliateManualPayoutReturns.Any(r => r.Id == a.ReturnId &&
                    r.AffiliateAccountId == e.AffiliateAccountId && r.StoreTenantId == e.StoreTenantId)));

    public static async Task<(decimal Amount, HashSet<Guid> PaidEntryIds)> LoadAsync(ApplicationDbContext db,
        Guid affiliate, Guid? store, CancellationToken ct)
    {
        var receipts = await db.AffiliateManualPayoutReturns.AsNoTracking()
            .Where(r => r.AffiliateAccountId == affiliate && (!store.HasValue || r.StoreTenantId == store.Value)).ToListAsync(ct);
        var paidIds = new HashSet<Guid>();
        foreach (var receipt in receipts)
        {
            var expected = await BuildPostingAsync(db, receipt.StoreTenantId, receipt.Id, ct);
            var journals = await db.JournalEntries.IgnoreQueryFilters().AsNoTracking().Include(j => j.LedgerEntries)
                .Where(j => j.TenantId == receipt.StoreTenantId && j.IdempotencyKey == expected.IdempotencyKey).ToListAsync(ct);
            if (journals.Count != 1) throw Conflict();
            var journal = journals[0];
            if (journal.Status != JournalStatus.Posted || journal.SourceType != SourceType || journal.SourceId != receipt.Id ||
                journal.CreatedByUserId != receipt.AdministratorId || journal.SourceReference != expected.SourceReference ||
                journal.LedgerEntries.Count != expected.Lines.Count || expected.Lines.Any(l =>
                    journal.LedgerEntries.Count(x => x.AccountId == l.AccountId && x.DebitAmount == l.DebitAmount && x.CreditAmount == l.CreditAmount) != 1))
                throw Conflict();
            foreach (var id in await db.AffiliateManualPayoutReturnAllocations.Where(a => a.ReturnId == receipt.Id).Select(a => a.PaidEntryId).ToListAsync(ct))
                if (!paidIds.Add(id)) throw Conflict();
        }
        return (receipts.Sum(r => r.Amount), paidIds);
    }

    /// <summary>Also called by the central posting engine; validates even a substituted non-cash destination.</summary>
    public static async Task<PostingRequest> BuildPostingAsync(ApplicationDbContext db, Guid store, Guid returnId, CancellationToken ct)
    {
        var r = await db.AffiliateManualPayoutReturns.AsNoTracking().SingleOrDefaultAsync(x => x.Id == returnId && x.StoreTenantId == store, ct)
            ?? throw Conflict();
        var m = await db.AffiliateManualPayouts.AsNoTracking().SingleOrDefaultAsync(x => x.Id == r.ManualPayoutId && x.StoreTenantId == store, ct)
            ?? throw Conflict();
        var request = await db.AffiliatePayoutRequests.IgnoreQueryFilters().AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == r.RequestId && x.StoreTenantId == store, ct) ?? throw Conflict();
        var debit = await db.AffiliateBalanceLedger.IgnoreQueryFilters().AsNoTracking().SingleOrDefaultAsync(x => x.Id == m.BalanceLedgerId, ct)
            ?? throw Conflict();
        var credit = await db.AffiliateBalanceLedger.IgnoreQueryFilters().AsNoTracking().SingleOrDefaultAsync(x => x.Id == r.BalanceLedgerId, ct)
            ?? throw Conflict();
        if (m.RequestId != r.RequestId || m.AffiliateAccountId != r.AffiliateAccountId || m.Amount != r.Amount || m.Currency != r.Currency ||
            m.Attestation != AffiliateManualPayout.AttestationText || r.Amount <= 0 || r.Attestation != AffiliateManualPayoutReturn.AttestationText ||
            string.IsNullOrWhiteSpace(r.Reason) || string.IsNullOrWhiteSpace(r.ReceiptReference) || r.CommandFingerprint.Length != 64 ||
            request.AffiliateAccountId != r.AffiliateAccountId || request.Currency != r.Currency || request.Status != PayoutRequestStatus.Returned ||
            request.ApprovedAmount != r.Amount || request.RemainingAmount != 0 || request.PaidBy != m.AdministratorId ||
            request.PaymentReference != AffiliateSettlementOrchestrator.ManualReference(request.Id) ||
            debit.StoreTenantId != store || debit.AffiliateAccountId != r.AffiliateAccountId || debit.EntryType != BalanceEntryType.Withdrawal ||
            debit.ReferenceId != request.Id || debit.Amount != -r.Amount || debit.Currency != r.Currency ||
            credit.StoreTenantId != store || credit.AffiliateAccountId != r.AffiliateAccountId || credit.EntryType != BalanceEntryType.Adjustment ||
            credit.ReferenceId != r.Id || credit.Amount != r.Amount || credit.Currency != r.Currency ||
            credit.IdempotencyKey != $"AFFILIATE_BALANCE:ADJUSTMENT:{r.Id}") throw Conflict();
        var original = await db.JournalEntries.IgnoreQueryFilters().AsNoTracking().Include(j => j.LedgerEntries)
            .SingleOrDefaultAsync(j => j.Id == r.OriginalJournalId && j.TenantId == store, ct) ?? throw Conflict();
        var key = AffiliateSettlementOrchestrator.ManualJournalKey(store, request.Id);
        var payableId = await db.LedgerAccounts.IgnoreQueryFilters().Where(a => a.TenantId == store && a.AccountCode == AccountCodes.AffiliatePayable)
            .Select(a => a.Id).SingleAsync(ct);
        if (original.Status != JournalStatus.Posted || original.SourceType != "AffiliateCommissionPaid" || original.SourceId != request.Id ||
            original.IdempotencyKey != key || original.SourceReference != key || original.CreatedByUserId != m.AdministratorId ||
            original.LedgerEntries.Count != 2 ||
            original.LedgerEntries.Count(l => l.AccountId == m.SourceAccountId && l.CreditAmount == r.Amount && l.DebitAmount == 0) != 1 ||
            original.LedgerEntries.Count(l => l.AccountId == payableId && l.DebitAmount == r.Amount && l.CreditAmount == 0) != 1)
            throw Conflict();
        // Keep historical mapping proof. Current active/funded mapping is checked only
        // during execution, never to erase a committed receipt after later deactivation.
        var allocations = await db.AffiliateManualPayoutReturnAllocations.AsNoTracking().Where(a => a.ReturnId == r.Id).ToListAsync(ct);
        var expected = JsonSerializer.Deserialize<Allocation[]>(m.AllocationsJson) ?? throw Conflict();
        if (allocations.Count == 0 || expected.Length != allocations.Count || allocations.Sum(a => a.Amount) != r.Amount ||
            expected.Select(a => a.ApprovedEntryId).Distinct().Count() != expected.Length) throw Conflict();
        foreach (var a in allocations)
        {
            var paid = await db.AffiliateCommissionLedger.IgnoreQueryFilters().AsNoTracking().SingleOrDefaultAsync(e => e.Id == a.PaidEntryId, ct)
                ?? throw Conflict();
            var match = expected.SingleOrDefault(x => x.ApprovedEntryId == paid.OriginalEntryId) ?? throw Conflict();
            if (paid.EntryType != CommissionEntryType.Paid || paid.StoreTenantId != store || paid.AffiliateAccountId != r.AffiliateAccountId ||
                paid.Currency != r.Currency || paid.CommissionAmount != a.Amount || a.Amount != match.Amount ||
                paid.OrderId != match.OrderId || paid.OrderItemId != match.OrderItemId || match.JournalKey != key ||
                (match.PaidEntryId.HasValue ? paid.Id != match.PaidEntryId : paid.IdempotencyKey != $"AFFILIATE_COMM:PAID:{paid.OrderItemId}"))
                throw Conflict();
        }
        return new PostingRequest { TenantId = store, JournalDate = r.RecordedAtUtc.Date, SourceType = SourceType,
            SourceId = r.Id, SourceReference = Key(store, request.Id), IdempotencyKey = Key(store, request.Id),
            Description = "Full manual Affiliate payout returned to original cashbox", UserId = r.AdministratorId,
            Lines = [PostingLine.Debit(m.SourceAccountId, r.Amount), PostingLine.Credit(payableId, r.Amount)] };
    }

    internal sealed record Allocation(Guid ApprovedEntryId, Guid OrderId, Guid OrderItemId, decimal Amount, string JournalKey, Guid? PaidEntryId = null);
    internal static InvalidOperationException Conflict() => new("AFFILIATE_PAYOUT_RETURN_AUTHORITY_CONFLICT: Return receipt, allocation and accounting authority must agree.");
}
