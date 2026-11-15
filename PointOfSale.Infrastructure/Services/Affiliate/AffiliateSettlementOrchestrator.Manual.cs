using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PointOfSale.Core.Entities;
using PointOfSale.Core.Entities.Accounting;
using PointOfSale.Core.Entities.Affiliate;
using PointOfSale.Core.Enums;
using PointOfSale.Core.Interfaces.Affiliate;
using PointOfSale.Core.Interfaces.Accounting;

namespace PointOfSale.Infrastructure.Services.Affiliate;

public partial class AffiliateSettlementOrchestrator
{
    private sealed record ManualExecution(User Actor, AdminManualPayoutCommand Command, Guid ConfirmationId,
        DateTime ExecutedAtUtc, TreasuryAccount? Funding = null);

    public async Task<PayoutExecutionResult> ExecuteAdminManualPayoutAsync(Guid requestId, Guid storeTenantId,
        Guid administratorId, AdminManualPayoutCommand command, CancellationToken ct = default)
    {
        ValidateManualCommand(command);
        if (storeTenantId == Guid.Empty || _db.ScopedTenantId != storeTenantId)
            throw new SecurityException("AFFILIATE_MANUAL_TENANT_REQUIRED: The current unit of work must belong to the payout store.");
        var actor = await ValidateManualActorAsync(storeTenantId, administratorId, ct);
        var request = await _db.AffiliatePayoutRequests.IgnoreQueryFilters().AsNoTracking()
            .SingleOrDefaultAsync(r => r.Id == requestId && r.StoreTenantId == storeTenantId, ct)
            ?? throw new InvalidOperationException("AFFILIATE_PAYOUT_REQUEST_NOT_FOUND: Withdrawal not found in this store.");
        ValidateManualRequest(request, command);
        // PostgreSQL timestamps retain microseconds; return the same instant on first execution and replay.
        var manual = new ManualExecution(actor, command, Guid.NewGuid(), new DateTime(DateTime.UtcNow.Ticks / 10 * 10, DateTimeKind.Utc));
        return await ExecuteWithFreshRetryScopeAsync(o => o.ExecutePayoutCoreAsync(
            request.AffiliateAccountId, storeTenantId, command.Amount, SettlementSource.AdminManualPayout,
            null, requestId, null, ManualReference(requestId), command.Amount, command.Notes,
            administratorId, nameof(SettlementSource.AdminManualPayout), ct, manual), storeTenantId, ct);
    }

    public static string ManualReference(Guid request) => $"ADMIN-MANUAL:{request:D}";
    public static string ManualJournalKey(Guid store, Guid request) => $"AFFILIATE_MANUAL_PAYOUT:v1:{store}:{request}";
    public static string DestinationFingerprint(AffiliatePayoutRequest r) => Hash(JsonSerializer.Serialize(new {
        r.Id, r.StoreTenantId, r.AffiliateAccountId, r.PayoutWalletNumberSnapshot, r.PayoutWalletTypeSnapshot,
        r.PayoutWalletUpdatedAtUtcSnapshot, r.PayoutWalletSnapshotCapturedAtUtc }));
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static string CommandFingerprint(ManualExecution m)
    {
        // Preserve replay of immutable P5.12 receipts, whose command predates source binding.
        if (m.Command.SourceFingerprint == null)
            return Hash(JsonSerializer.Serialize(new { m.Actor.Id, Command = new {
                m.Command.CashboxId, m.Command.Amount, m.Command.Currency, m.Command.DestinationFingerprint,
                m.Command.Method, m.Command.PerformedManually, m.Command.Attestation,
                m.Command.InternalReference, m.Command.Notes } }));
        return Hash(JsonSerializer.Serialize(new { m.Actor.Id, m.Command }));
    }

    private static void ValidateManualCommand(AdminManualPayoutCommand c)
    {
        if (!c.PerformedManually || c.Attestation != AffiliateManualPayout.AttestationText)
            throw new InvalidOperationException("AFFILIATE_MANUAL_ATTESTATION_REQUIRED: Explicit confirmation of an actually performed manual payout is required.");
        if (c.Method != "Cash")
            throw new InvalidOperationException("AFFILIATE_MANUAL_METHOD_INVALID: Only manual cash from a mapped cashbox is supported.");
        if (c.CashboxId == Guid.Empty || c.Amount <= 0 || string.IsNullOrWhiteSpace(c.Currency) || c.DestinationFingerprint?.Length != 64 ||
            c.InternalReference?.Length > 200 || c.Notes?.Length > 2000)
            throw new InvalidOperationException("AFFILIATE_MANUAL_CONTRACT_INVALID: Review amount, currency, destination, cashbox and text limits.");
    }

    private async Task<User> ValidateManualActorAsync(Guid store, Guid actor, CancellationToken ct)
    {
        // The preflight read does not authorize a financial commit. Once inside
        // the canonical settlement transaction, hold the current exact actor
        // through posting/audit/commit so an independent revocation cannot commit
        // between this check and the financial effects. Serializable conflicts
        // after a lock wait continue through the existing fresh-scope retry path.
        if (_db.Database.IsNpgsql() && _db.Database.CurrentTransaction is not null)
            await _db.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT 1 FROM \"Users\" WHERE \"Id\" = {actor} AND \"TenantId\" = {store} FOR SHARE", ct);
        return await _db.Users.IgnoreQueryFilters().AsNoTracking().SingleOrDefaultAsync(u => u.Id == actor && u.TenantId == store &&
            u.IsActive && !u.IsDeleted && (u.Role == UserRole.Owner || u.Role == UserRole.Admin), ct)
        ?? throw new SecurityException("AFFILIATE_MANUAL_ADMIN_REQUIRED: An active administrator of this store is required.");
    }

    private static void ValidateManualRequest(AffiliatePayoutRequest r, AdminManualPayoutCommand c)
    {
        if (r.Status is not (PayoutRequestStatus.Approved or PayoutRequestStatus.Paid) || r.ApprovedBy == null || r.ApprovedAtUtc == null)
            throw new InvalidOperationException("AFFILIATE_PAYOUT_REQUEST_STATUS_INVALID: Approve the withdrawal first.");
        if (r.ApprovedAmount != c.Amount || r.Currency != c.Currency || r.PayoutWalletSnapshotCapturedAtUtc == null ||
            DestinationFingerprint(r) != c.DestinationFingerprint || r.ApprovedAmount > r.RequestedAmount ||
            (r.Status == PayoutRequestStatus.Approved && r.RemainingAmount != c.Amount))
            throw new InvalidOperationException("AFFILIATE_MANUAL_REQUEST_CHANGED: Refresh the approved amount, currency and destination before confirming.");
    }

    private async Task<TreasuryAccount> LoadManualFundingAsync(Guid store, Guid cashboxId, decimal amount, string? sourceFingerprint, CancellationToken ct)
    {
        if (_db.Database.IsRelational() && _db.Database.CurrentTransaction == null)
            throw new InvalidOperationException("Manual funding locks require the settlement transaction.");
        if (_db.Database.IsNpgsql())
        {
            // Hold reviewed physical identity/mapping stable through the outer financial commit.
            // Ledger-account writes are additionally protected by xmin in FinancialPostingService.
            var reference = cashboxId.ToString("N");
            await _db.Database.ExecuteSqlInterpolatedAsync($"""
                SELECT 1 FROM "Cashboxes" WHERE "Id" = {cashboxId} AND "TenantId" = {store} FOR SHARE
                """, ct);
            await _db.Database.ExecuteSqlInterpolatedAsync($"""
                SELECT 1 FROM "TreasuryAccounts" WHERE "TenantId" = {store} AND "ReferenceCode" = {reference} FOR SHARE
                """, ct);
        }
        var treasury = await AffiliateManualCashSource.ResolveAsync(_db, store, cashboxId, ct);
        if (treasury?.Account == null)
            throw new InvalidOperationException("AFFILIATE_MANUAL_SOURCE_INVALID: Select an active same-store cashbox with a dedicated nonnegative treasury ledger mapping.");
        if (sourceFingerprint != AffiliateManualCashSource.Fingerprint(cashboxId, treasury))
            throw new InvalidOperationException("AFFILIATE_MANUAL_REQUEST_CHANGED: The reviewed source mapping changed or is missing. Refresh before confirming.");
        // The financial posting service performs the actual debit with account concurrency
        // protection. Read the ledger as well, rejecting stale cached account balances.
        var ledgerFunds = await _db.LedgerEntries.IgnoreQueryFilters().Where(l => l.TenantId == store &&
            l.AccountId == treasury.AccountId && (l.JournalEntry.Status == JournalStatus.Posted || l.JournalEntry.Status == JournalStatus.Reversed))
            .SumAsync(l => (decimal?)(l.DebitAmount - l.CreditAmount), ct) ?? 0m;
        if (ledgerFunds != treasury.Account.CurrentBalance)
            throw new InvalidOperationException("AFFILIATE_MANUAL_FUNDING_INCONSISTENT: Source ledger and account balance disagree. Reconcile treasury first.");
        if (ledgerFunds < amount)
            throw new InvalidOperationException("AFFILIATE_MANUAL_SOURCE_UNDERFUNDED: The selected cashbox cannot fund this payout. Fund it through an authorized treasury operation first.");
        return treasury;
    }

    private async Task AddManualConfirmationAsync(AffiliatePayoutRequest r, AffiliateBalanceLedger debit,
        ExactPayoutPlan plan, ManualExecution m, IReadOnlyDictionary<Guid, Guid> paidEntryIds, CancellationToken ct)
    {
        var account = await _db.AffiliateAccounts.IgnoreQueryFilters().AsNoTracking().SingleAsync(a => a.Id == r.AffiliateAccountId, ct);
        if (!account.IsActive || account.IsDeleted || !await _db.AffiliateUsers.IgnoreQueryFilters().AnyAsync(u =>
            u.Id == account.UserId && u.AffiliateAccountId == account.Id && !u.IsDeleted, ct))
            throw new InvalidOperationException("AFFILIATE_PAYOUT_ACCOUNT_INACTIVE: Affiliate identity must be active.");
        var currency = await _db.Tenants.IgnoreQueryFilters().Where(t => t.Id == r.StoreTenantId).Select(t => t.Currency).SingleAsync(ct);
        if (currency != r.Currency || plan.SelectedEntries.Any(e => e.ApprovedEntry.Currency != r.Currency))
            throw new InvalidOperationException("AFFILIATE_MANUAL_CURRENCY_INVALID: Commission, request and treasury store currency must match.");
        var confirmation = new AffiliateManualPayout {
            Id = m.ConfirmationId, RequestId = r.Id, StoreTenantId = r.StoreTenantId,
            AffiliateAccountId = account.Id, AffiliateUserId = account.UserId, Amount = r.ApprovedAmount!.Value, Currency = r.Currency,
            Method = m.Command.Method, DestinationNumber = r.PayoutWalletNumberSnapshot!, DestinationType = r.PayoutWalletTypeSnapshot!,
            DestinationUpdatedAtUtc = r.PayoutWalletUpdatedAtUtcSnapshot, DestinationCapturedAtUtc = r.PayoutWalletSnapshotCapturedAtUtc!.Value,
            DestinationFingerprint = DestinationFingerprint(r), CashboxId = m.Command.CashboxId, TreasuryAccountId = m.Funding!.Id,
            SourceAccountId = m.Funding.AccountId, SourceAccountCode = m.Funding.Account!.AccountCode, SourceAccountName = m.Funding.Name,
            AdministratorId = m.Actor.Id, AdministratorRole = m.Actor.Role.ToString(), ExecutedAtUtc = m.ExecutedAtUtc,
            Attestation = AffiliateManualPayout.AttestationText, InternalReference = m.Command.InternalReference, Notes = m.Command.Notes,
            BalanceLedgerId = debit.Id, CommandFingerprint = CommandFingerprint(m),
            AllocationsJson = JsonSerializer.Serialize(plan.SelectedEntries.Select(e => new {
                ApprovedEntryId = e.ApprovedEntry.Id, e.ApprovedEntry.OrderId, e.ApprovedEntry.OrderItemId,
                Amount = e.PayableCommissionAmount, JournalKey = ManualJournalKey(r.StoreTenantId, r.Id),
                PaidEntryId = paidEntryIds[e.ApprovedEntry.Id] }))
        };
        _db.AffiliateManualPayouts.Add(confirmation);
        _db.AuditLogs.Add(new AuditLog { TenantId = r.StoreTenantId, UserId = m.Actor.Id,
            ActionType = "AffiliateAdminManualPayout", EntityType = nameof(AffiliateManualPayout), EntityId = confirmation.Id.ToString(),
            Source = nameof(SettlementSource.AdminManualPayout), SuccessFlag = true,
            Description = "Administrator attests manual cash payout performed. No electronic transfer is initiated or verified.",
            AfterDataJson = JsonSerializer.Serialize(new { confirmation.Id, RequestId = r.Id, confirmation.Amount, confirmation.Currency,
                confirmation.CashboxId, confirmation.SourceAccountId, confirmation.AdministratorRole, confirmation.ExecutedAtUtc,
                confirmation.Attestation, confirmation.BalanceLedgerId, confirmation.AllocationsJson }) });
        await _db.SaveChangesAsync(ct);
    }

    private async Task ValidateManualReplayAsync(AffiliatePayoutRequest r, ManualExecution m, CancellationToken ct)
    {
        var saved = await _db.AffiliateManualPayouts.AsNoTracking().SingleOrDefaultAsync(x => x.RequestId == r.Id, ct);
        if (saved == null || saved.CommandFingerprint != CommandFingerprint(m))
            throw new InvalidOperationException("AFFILIATE_PAYOUT_IDEMPOTENCY_CONFLICT: Manual payout already recorded with a different actor or confirmation.");
    }

    private async Task EnsureCommittedManualTupleAsync(AffiliatePayoutRequest r, AffiliateBalanceLedger debit, CancellationToken ct)
    {
        var saved = await _db.AffiliateManualPayouts.AsNoTracking().SingleOrDefaultAsync(x => x.RequestId == r.Id, ct);
        if (saved == null || saved.StoreTenantId != r.StoreTenantId || saved.AffiliateAccountId != r.AffiliateAccountId ||
            saved.Amount != r.ApprovedAmount || saved.Currency != r.Currency || saved.BalanceLedgerId != debit.Id ||
            saved.AdministratorId != r.PaidBy || saved.Attestation != AffiliateManualPayout.AttestationText ||
            r.PaymentReference != ManualReference(r.Id))
            throw new InvalidOperationException("AFFILIATE_PAYOUT_IDEMPOTENCY_CONFLICT: Manual attestation and committed withdrawal disagree.");
        var journals = await _db.JournalEntries.IgnoreQueryFilters().AsNoTracking().Include(j => j.LedgerEntries)
            .Where(j => j.TenantId == r.StoreTenantId && j.SourceType == "AffiliateCommissionPaid" && j.SourceId == r.Id).ToListAsync(ct);
        if (journals.Count == 0 || journals.Any(j => j.Status != JournalStatus.Posted || j.CreatedByUserId != saved.AdministratorId) ||
            journals.Sum(j => j.LedgerEntries.Where(l => l.AccountId == saved.SourceAccountId).Sum(l => l.CreditAmount - l.DebitAmount)) != saved.Amount)
            throw new InvalidOperationException("AFFILIATE_PAYOUT_IDEMPOTENCY_CONFLICT: Manual source account debit does not match the attestation.");
        var allocations = JsonSerializer.Deserialize<ManualAllocation[]>(saved.AllocationsJson)!;
        var payoutKey = ManualJournalKey(r.StoreTenantId, r.Id);
        if (journals.Any(j => j.IdempotencyKey == payoutKey))
        {
            var journal = journals.Single();
            var payable = await _accountResolver.GetByCodeAsync(r.StoreTenantId, AccountCodes.AffiliatePayable, ct);
            var credit = journal.LedgerEntries.Where(l => l.AccountId == saved.SourceAccountId).ToList();
            var debits = journal.LedgerEntries.Where(l => l.AccountId == payable.Id).ToList();
            if (journal.IdempotencyKey != payoutKey || journal.SourceReference != payoutKey ||
                journal.LedgerEntries.Count != 2 || credit.Count != 1 || debits.Count != 1 ||
                credit[0].CreditAmount != saved.Amount || credit[0].DebitAmount != 0 ||
                debits[0].DebitAmount != saved.Amount || debits[0].CreditAmount != 0 ||
                allocations.Length == 0 || allocations.Any(a => a.Amount <= 0 || a.JournalKey != payoutKey) ||
                allocations.Select(a => a.ApprovedEntryId).Distinct().Count() != allocations.Length ||
                allocations.Select(a => a.OrderItemId).Distinct().Count() != allocations.Length ||
                allocations.Sum(a => a.Amount) != saved.Amount)
                throw new InvalidOperationException("AFFILIATE_PAYOUT_IDEMPOTENCY_CONFLICT: Payout-level journal or allocations disagree.");
        }
        foreach (var a in allocations)
            if (!await _db.AffiliateCommissionLedger.IgnoreQueryFilters().AnyAsync(p => p.OriginalEntryId == a.ApprovedEntryId &&
                p.EntryType == CommissionEntryType.Paid && p.StoreTenantId == r.StoreTenantId &&
                p.AffiliateAccountId == r.AffiliateAccountId && p.OrderId == a.OrderId && p.OrderItemId == a.OrderItemId &&
                p.Currency == saved.Currency && p.CommissionAmount == a.Amount &&
                (a.PaidEntryId.HasValue ? p.Id == a.PaidEntryId.Value : p.IdempotencyKey == "AFFILIATE_COMM:PAID:" + p.OrderItemId) &&
                _db.AffiliateCommissionLedger.IgnoreQueryFilters().Any(parent => parent.Id == a.ApprovedEntryId &&
                    parent.EntryType == CommissionEntryType.Approved && parent.StoreTenantId == r.StoreTenantId &&
                    parent.AffiliateAccountId == r.AffiliateAccountId && parent.OrderId == a.OrderId &&
                    parent.OrderItemId == a.OrderItemId && parent.AttributionId == p.AttributionId &&
                    parent.Currency == p.Currency && parent.CommissionAmount >= p.CommissionAmount), ct))
                throw new InvalidOperationException("AFFILIATE_PAYOUT_IDEMPOTENCY_CONFLICT: Manual allocation lineage is incomplete.");
    }
    private sealed record ManualAllocation(Guid ApprovedEntryId, Guid OrderId, Guid OrderItemId, decimal Amount, string JournalKey, Guid? PaidEntryId = null);

    private async Task<PayoutExecutionResult> ManualResultAsync(Guid requestId, bool replay, CancellationToken ct)
    {
        var saved = await _db.AffiliateManualPayouts.AsNoTracking().SingleOrDefaultAsync(x => x.RequestId == requestId, ct);
        return new() { WasIdempotentSkip = replay, ManualPayoutId = saved?.Id, ExecutedAtUtc = saved?.ExecutedAtUtc };
    }
}
