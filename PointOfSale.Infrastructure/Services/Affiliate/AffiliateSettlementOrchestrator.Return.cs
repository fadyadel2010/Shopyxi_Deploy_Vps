using System.Data;
using System.Security;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PointOfSale.Core.Entities;
using PointOfSale.Core.Entities.Affiliate;
using PointOfSale.Core.Enums;
using PointOfSale.Core.Interfaces.Affiliate;

namespace PointOfSale.Infrastructure.Services.Affiliate;

public partial class AffiliateSettlementOrchestrator
{
    public static string ReturnReviewFingerprint(AffiliateManualPayout payout, Guid journalId, string sourceFingerprint) =>
        Hash(JsonSerializer.Serialize(new { payout.Id, payout.RequestId, payout.StoreTenantId, payout.Amount, payout.Currency,
            payout.BalanceLedgerId, payout.SourceAccountId, payout.CommandFingerprint, journalId, sourceFingerprint }));

    public Task<ManualPayoutReturnResult> ReturnAdminManualPayoutAsync(Guid requestId, Guid storeTenantId,
        Guid administratorId, AdminManualPayoutReturnCommand command, CancellationToken ct = default)
    {
        if (!command.ReceivedManually || command.Attestation != AffiliateManualPayoutReturn.AttestationText)
            throw new InvalidOperationException("AFFILIATE_PAYOUT_RETURN_ATTESTATION_REQUIRED: Confirm actual receipt of the full returned cash.");
        if (command.ReviewFingerprint?.Length != 64 || string.IsNullOrWhiteSpace(command.ReceiptReference) || command.ReceiptReference.Length > 200 ||
            string.IsNullOrWhiteSpace(command.Reason) || command.Reason.Length > 1000 || requestId == Guid.Empty)
            throw new InvalidOperationException("AFFILIATE_PAYOUT_RETURN_CONTRACT_INVALID: Review the original payout, return receipt and reason.");
        if (storeTenantId == Guid.Empty || _db.ScopedTenantId != storeTenantId)
            throw new SecurityException("AFFILIATE_MANUAL_TENANT_REQUIRED: Same-store financial authority is required.");
        return ExecuteWithFreshRetryScopeAsync(o => o.ReturnManualCoreAsync(requestId, storeTenantId, administratorId, command, ct), storeTenantId, ct);
    }

    private async Task<ManualPayoutReturnResult> ReturnManualCoreAsync(Guid requestId, Guid store,
        Guid actor, AdminManualPayoutReturnCommand command, CancellationToken ct)
    {
        await using var tx = await _db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        var initial = await _db.AffiliatePayoutRequests.IgnoreQueryFilters().AsNoTracking()
            .SingleOrDefaultAsync(r => r.Id == requestId && r.StoreTenantId == store, ct)
            ?? throw new InvalidOperationException("AFFILIATE_PAYOUT_REQUEST_NOT_FOUND: Withdrawal not found in this store.");
        await AffiliatePayoutService.AcquireFinancialLockAsync(_db, initial.AffiliateAccountId, store, ct);
        await ValidateManualActorAsync(store, actor, ct);
        var r = await _db.AffiliatePayoutRequests.IgnoreQueryFilters().SingleAsync(x => x.Id == requestId && x.StoreTenantId == store, ct);
        var fingerprint = Hash(JsonSerializer.Serialize(new { requestId, store, actor, command.ReviewFingerprint,
            command.ReceivedManually, command.Attestation, ReceiptReference = command.ReceiptReference.Trim(), Reason = command.Reason.Trim() }));
        var saved = await _db.AffiliateManualPayoutReturns.AsNoTracking().SingleOrDefaultAsync(x => x.RequestId == requestId, ct);
        if (saved != null)
        {
            if (saved.StoreTenantId != store || saved.CommandFingerprint != fingerprint)
                throw new InvalidOperationException("AFFILIATE_PAYOUT_IDEMPOTENCY_CONFLICT: This payout was returned with different confirmation details.");
            await AffiliatePayoutReturnAuthority.LoadAsync(_db, r.AffiliateAccountId, store, ct);
            await tx.CommitAsync(ct);
            return new(saved.Id, saved.RecordedAtUtc, true);
        }
        if (r.Status != PayoutRequestStatus.Paid)
            throw new InvalidOperationException("AFFILIATE_PAYOUT_RETURN_STATE_INVALID: Only a complete paid manual request may be returned.");
        var m = await _db.AffiliateManualPayouts.AsNoTracking().SingleOrDefaultAsync(x => x.RequestId == requestId && x.StoreTenantId == store, ct)
            ?? throw new InvalidOperationException("AFFILIATE_PAYOUT_RETURN_MANUAL_ONLY: Provider payouts require trusted returned-transfer evidence, not a cash attestation.");
        var debit = await _db.AffiliateBalanceLedger.IgnoreQueryFilters().AsNoTracking().SingleAsync(x => x.Id == m.BalanceLedgerId, ct);
        await EnsureCommittedManualTupleAsync(r, debit, ct);
        var key = ManualJournalKey(store, requestId);
        var journal = await _db.JournalEntries.IgnoreQueryFilters().AsNoTracking()
            .SingleOrDefaultAsync(j => j.TenantId == store && j.SourceId == requestId && j.IdempotencyKey == key, ct)
            ?? throw new InvalidOperationException("AFFILIATE_PAYOUT_RETURN_AUTHORITY_CONFLICT: Legacy split journals require explicit reconciliation before return.");
        var source = await AffiliateManualCashSource.ResolveAsync(_db, store, m.CashboxId, ct)
            ?? throw AffiliatePayoutReturnAuthority.Conflict();
        var sourceFingerprint = AffiliateManualCashSource.Fingerprint(m.CashboxId, source);
        source = await LoadManualFundingAsync(store, m.CashboxId, 0m, sourceFingerprint, ct);
        if (source.AccountId != m.SourceAccountId || source.Id != m.TreasuryAccountId ||
            ReturnReviewFingerprint(m, journal.Id, sourceFingerprint) != command.ReviewFingerprint)
            throw new InvalidOperationException("AFFILIATE_MANUAL_REQUEST_CHANGED: Original cashbox mapping or reviewed payout changed. Refresh before confirming.");
        var id = Guid.NewGuid();
        var adjustment = AffiliateBalanceLedger.CreateAdjustment(store, r.AffiliateAccountId, m.Amount, m.Currency,
            "Full manual cash payout returned; original withdrawal retained", id);
        var receipt = new AffiliateManualPayoutReturn { Id = id, StoreTenantId = store, AffiliateAccountId = r.AffiliateAccountId,
            RequestId = requestId, ManualPayoutId = m.Id, OriginalJournalId = journal.Id, BalanceLedgerId = adjustment.Id,
            AdministratorId = actor, Amount = m.Amount, Currency = m.Currency,
            RecordedAtUtc = new DateTime(DateTime.UtcNow.Ticks / 10 * 10, DateTimeKind.Utc),
            Attestation = command.Attestation, ReceiptReference = command.ReceiptReference.Trim(), Reason = command.Reason.Trim(), CommandFingerprint = fingerprint };
        var allocations = JsonSerializer.Deserialize<ManualAllocation[]>(m.AllocationsJson) ?? throw AffiliatePayoutReturnAuthority.Conflict();
        foreach (var a in allocations)
        {
            var paid = await _db.AffiliateCommissionLedger.IgnoreQueryFilters().AsNoTracking().SingleAsync(p =>
                p.EntryType == CommissionEntryType.Paid && p.OriginalEntryId == a.ApprovedEntryId && p.StoreTenantId == store &&
                p.AffiliateAccountId == r.AffiliateAccountId && (a.PaidEntryId.HasValue ? p.Id == a.PaidEntryId.Value :
                    p.IdempotencyKey == "AFFILIATE_COMM:PAID:" + p.OrderItemId), ct);
            _db.AffiliateManualPayoutReturnAllocations.Add(new() { Id = Guid.NewGuid(), ReturnId = id, PaidEntryId = paid.Id, Amount = a.Amount });
        }
        _db.AffiliateBalanceLedger.Add(adjustment);
        _db.AffiliateManualPayoutReturns.Add(receipt);
        r.MarkReturned();
        _db.AuditLogs.Add(new AuditLog { TenantId = store, UserId = actor, ActionType = "AffiliateManualPayoutReturned",
            EntityType = nameof(AffiliateManualPayoutReturn), EntityId = id.ToString(), Source = nameof(AffiliateSettlementOrchestrator), SuccessFlag = true,
            Description = "Administrator confirms full cash returned to the original cashbox; original financial history retained.",
            AfterDataJson = JsonSerializer.Serialize(new { receipt.Id, receipt.RequestId, receipt.ManualPayoutId, receipt.OriginalJournalId,
                receipt.BalanceLedgerId, receipt.Amount, receipt.Currency, receipt.Attestation, receipt.ReceiptReference, receipt.Reason }) });
        await _db.SaveChangesAsync(ct);
        var posting = await AffiliatePayoutReturnAuthority.BuildPostingAsync(_db, store, id, ct);
        await _postingService.PostJournalAsync(posting, ct);
        await AffiliatePayoutReturnAuthority.LoadAsync(_db, r.AffiliateAccountId, store, ct);
        foreach (var orderId in allocations.Select(a => a.OrderId).Distinct())
            await _eventPublisher.PublishAsync(new PointOfSale.Core.Events.Affiliate.AffiliateManualPayoutReturnedEvent {
                TenantId = store, StoreTenantId = store, AffiliateAccountId = r.AffiliateAccountId, OrderId = orderId,
                ReturnId = id, AffiliateAuthorityTenantId = await ResolveAffiliateAuthorityTenantIdAsync(r.AffiliateAccountId, ct),
                CorrelationId = id.ToString() }, ct);
        await _db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return new(id, receipt.RecordedAtUtc, false);
    }
}
