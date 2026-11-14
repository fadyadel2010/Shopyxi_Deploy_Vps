using System.Data;
using System.Globalization;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PointOfSale.Core.Entities;
using PointOfSale.Core.Entities.Affiliate;
using PointOfSale.Core.Enums;
using PointOfSale.Core.Interfaces;
using PointOfSale.Core.Interfaces.Affiliate;
using PointOfSale.Infrastructure.Data;
using PointOfSale.Infrastructure.MultiTenancy;
using PointOfSale.Infrastructure.Services.Affiliate;

namespace PointOfSale.API.Services;

/// <summary>Operator-owned, expiring allow-list for ONE deliberate simulation. No secrets.</summary>
public sealed class AffiliateQaPayoutOptions
{
    public const string Section = "AffiliateQaPayout";
    public bool Enabled { get; set; }
    public Guid StoreTenantId { get; set; }
    public Guid AffiliateAccountId { get; set; }
    public Guid WithdrawalRequestId { get; set; }
    public Guid SourceOrderId { get; set; }
    public decimal Amount { get; set; }
    public string Currency { get; set; } = "";
    public DateTime ExpiresAtUtc { get; set; }
}

/// <summary>
/// Simulates only an external payout FACT, never a transfer. All money mutations remain
/// in the existing orchestrator. The private async capability follows its fresh retry
/// scopes, but cannot be obtained by the real/manual payment endpoints.
/// </summary>
public sealed class AffiliateQaPayoutSimulation(
    ApplicationDbContext db, IWebHostEnvironment environment, IOptions<AffiliateQaPayoutOptions> options,
    IServiceScopeFactory scopes) : IAffiliatePayoutEvidenceVerifier
{
    public const string Source = "QA_SIMULATED_PAYOUT_NO_TRANSFER";
    public const string Acknowledgement = "SIMULATED QA PAYOUT - NO MONEY TRANSFER";
    private static readonly AsyncLocal<AffiliateQaPayoutOptions?> Execution = new();
    private AffiliateQaPayoutOptions Policy => options.Value;
    public static string MarkerPath(string root) => Path.Combine(root, ".ai", "qa", "affiliate-payout.enabled");
    public static string Reference(Guid request) => $"QA-SIMULATED-NO-TRANSFER:{request:D}";

    public static bool AllowsEnvironment(string name) =>
        name.Equals("Development", StringComparison.OrdinalIgnoreCase) || name.Equals("QA", StringComparison.OrdinalIgnoreCase);

    /// <summary>Configuration binding may return Local for a zoned instant; compare its UTC instant.</summary>
    private DateTime ExpiryUtc => Policy.ExpiresAtUtc.ToUniversalTime();

    public bool IsEnabled => AllowsEnvironment(environment.EnvironmentName) && Policy.Enabled &&
        Policy.ExpiresAtUtc != default && Policy.ExpiresAtUtc.Kind != DateTimeKind.Unspecified && ExpiryUtc > DateTime.UtcNow &&
        ExpiryUtc <= DateTime.UtcNow.AddHours(1) &&
        Policy.StoreTenantId != Guid.Empty && Policy.AffiliateAccountId != Guid.Empty &&
        Policy.WithdrawalRequestId != Guid.Empty && Policy.SourceOrderId != Guid.Empty &&
        Policy.Amount >= AffiliatePayoutRequest.MinimumThreshold && Policy.Currency == "EGP" &&
        File.Exists(MarkerPath(environment.ContentRootPath));

    private void RequireEnabled()
    {
        if (!IsEnabled) throw new InvalidOperationException("AFFILIATE_PAYOUT_QA_SIMULATION_DISABLED");
        var expectedDatabase = environment.IsDevelopment() ? "PosEcommerceDb_Baseline" : "PosEcommerceDb_QA";
        if (!db.Database.IsNpgsql() || db.Database.GetDbConnection().Database != expectedDatabase)
            throw new InvalidOperationException("AFFILIATE_PAYOUT_QA_SIMULATION_DATABASE_MISMATCH");
    }

    private static string Fingerprint(AffiliateQaPayoutOptions p) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        $"{Source}\n{p.StoreTenantId:D}\n{p.AffiliateAccountId:D}\n{p.WithdrawalRequestId:D}\n{p.SourceOrderId:D}\n{p.Amount.ToString("0.00", CultureInfo.InvariantCulture)}\n{p.Currency}")));

    private async Task<AffiliatePayoutRequest> ValidateAuthorityAsync(Guid actor, Guid store, Guid requestId, CancellationToken ct)
    {
        RequireEnabled();
        var p = Policy;
        if (store != p.StoreTenantId || requestId != p.WithdrawalRequestId)
            throw new InvalidOperationException("AFFILIATE_PAYOUT_QA_SIMULATION_SCOPE_MISMATCH");
        if (!await db.Users.IgnoreQueryFilters().AsNoTracking().AnyAsync(u => u.Id == actor && u.TenantId == store &&
            u.IsActive && !u.IsDeleted && (u.Role == UserRole.Admin || u.Role == UserRole.Owner), ct))
            throw new SecurityException("Current authorized store administrator required.");
        var r = await db.AffiliatePayoutRequests.IgnoreQueryFilters().AsNoTracking().SingleOrDefaultAsync(r =>
            r.Id == requestId && r.StoreTenantId == store && r.AffiliateAccountId == p.AffiliateAccountId, ct)
            ?? throw new InvalidOperationException("AFFILIATE_PAYOUT_REQUEST_NOT_FOUND");
        if (r.Status is not (PayoutRequestStatus.Approved or PayoutRequestStatus.Paid) || r.ApprovedBy == null ||
            r.ApprovedAtUtc == null || r.ApprovedAmount != p.Amount || r.RequestedAmount != p.Amount || r.Currency != p.Currency)
            throw new InvalidOperationException("AFFILIATE_PAYOUT_REQUEST_STATUS_INVALID");
        var account = await db.AffiliateAccounts.IgnoreQueryFilters().AsNoTracking().SingleAsync(a => a.Id == p.AffiliateAccountId, ct);
        if (!account.IsActive || account.IsDeleted) throw new InvalidOperationException("AFFILIATE_PAYOUT_ACCOUNT_INACTIVE");
        r.EnsurePayoutWalletSnapshotMatches(account.PayoutWalletNumber, account.PayoutWalletType, account.PayoutWalletUpdatedAtUtc);
        var eligibility = await AffiliateCommissionEligibility.LoadAsync(db, p.AffiliateAccountId, store, ct);
        // Do not let FIFO spend some other order or any Synthetic QA commission.
        var approved = eligibility.RealEntries.Where(e => e.EntryType == CommissionEntryType.Approved).ToArray();
        if (approved.Length == 0 || approved.Any(e => e.OrderId != p.SourceOrderId) ||
            eligibility.NetApproved != p.Amount || eligibility.SyntheticEntries.Any(e => e.OrderId == p.SourceOrderId))
            throw new InvalidOperationException("AFFILIATE_PAYOUT_QA_SIMULATION_LINEAGE_MISMATCH");
        if (r.Status == PayoutRequestStatus.Paid && r.PaymentReference != Reference(requestId))
            throw new InvalidOperationException("AFFILIATE_PAYOUT_IDEMPOTENCY_CONFLICT");
        return r;
    }

    public async Task<PayoutExecutionResult> ExecuteAsync(Guid actor, Guid store, Guid requestId, string acknowledgement, CancellationToken ct)
    {
        if (acknowledgement != Acknowledgement)
            throw new InvalidOperationException("AFFILIATE_PAYOUT_QA_SIMULATION_ACKNOWLEDGEMENT_REQUIRED");
        await ValidateAuthorityAsync(actor, store, requestId, ct);
        // Admission is durable and separately auditable; it is not money movement. A failed
        // payout may leave one unallocated simulated fact, never an uncommitted debit.
        await AffiliateFinancialRetry.ExecuteAsync(async attempt =>
        {
            if (attempt > 1) db.ChangeTracker.Clear();
            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
            var lockId = BitConverter.ToInt64(SHA256.HashData(Encoding.UTF8.GetBytes($"qa-payout-evidence:{requestId:D}")), 0);
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({lockId})", ct);
            await ValidateAuthorityAsync(actor, store, requestId, ct);
            var reference = Reference(requestId);
            var existing = await db.AffiliatePayoutTransferEvidence.IgnoreQueryFilters().AsNoTracking()
                .SingleOrDefaultAsync(e => e.RailNamespace == "AFFILIATE_PAYOUT" && e.NormalizedPaymentReference == reference.ToUpperInvariant(), ct);
            if (existing == null)
            {
                var evidence = AffiliatePayoutTransferEvidence.Create(store, "AFFILIATE_PAYOUT", reference,
                    Policy.Amount, Policy.Currency, DateTime.UtcNow, Source, Fingerprint(Policy));
                db.AffiliatePayoutTransferEvidence.Add(evidence);
                db.AuditLogs.Add(new AuditLog { TenantId = store, UserId = actor, ActionType = Source,
                    EntityType = nameof(AffiliatePayoutTransferEvidence), EntityId = evidence.Id.ToString(), Source = Source,
                    SuccessFlag = true, Description = $"{Acknowledgement}. Request={requestId}; source order={Policy.SourceOrderId}; " +
                        "Non-transferable simulated evidence. Canonical funding, allocation, accounting and outbox remain required." });
                await db.SaveChangesAsync(ct);
            }
            else if (!Matches(existing, Policy))
                throw new InvalidOperationException("AFFILIATE_PAYOUT_IDEMPOTENCY_CONFLICT");
            await tx.CommitAsync(ct);
            return true;
        }, ct: ct);

        var prior = Execution.Value;
        Execution.Value = Policy;
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            if (scope.ServiceProvider.GetRequiredService<ITenantContext>() is TenantContext tenant) tenant.SetTenantId(store);
            return await scope.ServiceProvider.GetRequiredService<IAffiliateSettlementOrchestrator>().ExecuteAffiliatePayoutAsync(
                Policy.AffiliateAccountId, store, Policy.Amount, SettlementSource.WithdrawalEngine, Reference(requestId),
                payoutRequestId: requestId, paidByUserId: actor, adminNotes: Acknowledgement, ct: ct);
        }
        finally { Execution.Value = prior; }
    }

    private static bool Matches(AffiliatePayoutTransferEvidence e, AffiliateQaPayoutOptions p) =>
        !e.IsDeleted && e.StoreTenantId == p.StoreTenantId && e.RailNamespace == "AFFILIATE_PAYOUT" &&
        e.PaymentReference == Reference(p.WithdrawalRequestId) &&
        e.NormalizedPaymentReference == Reference(p.WithdrawalRequestId).ToUpperInvariant() &&
        e.ExpectedAmount == p.Amount && e.Currency == p.Currency && e.VerifiedAtUtc != default &&
        e.VerificationSource == Source && e.VerificationFingerprint == Fingerprint(p);

    public async Task<bool> VerifyAsync(AffiliatePayoutTransferEvidence evidence, Guid affiliateAccountId,
        Guid storeTenantId, decimal amount, CancellationToken ct)
    {
        var grant = Execution.Value;
        if (!IsEnabled || grant == null || Fingerprint(grant) != Fingerprint(Policy) ||
            affiliateAccountId != Policy.AffiliateAccountId || storeTenantId != Policy.StoreTenantId ||
            amount != Policy.Amount || !Matches(evidence, Policy)) return false;
        RequireEnabled();
        return await db.AffiliatePayoutRequests.IgnoreQueryFilters().AsNoTracking().AnyAsync(r =>
            r.Id == Policy.WithdrawalRequestId && r.AffiliateAccountId == affiliateAccountId && r.StoreTenantId == storeTenantId &&
            r.Status == PayoutRequestStatus.Approved && r.ApprovedAmount == amount && r.Currency == Policy.Currency, ct);
    }
}
