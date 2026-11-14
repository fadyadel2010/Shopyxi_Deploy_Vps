#if DEBUG
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using PointOfSale.Core.Entities;
using PointOfSale.Core.Entities.Affiliate;
using PointOfSale.Core.Interfaces.Affiliate;
using PointOfSale.Infrastructure.Data;

namespace PointOfSale.API.Development;

/// <summary>
/// Fixed-task local simulation of the external fact ONLY. Has no provider/HTTP/money writer.
/// All payout, funding, allocation, cash and accounting operations remain in the orchestrator.
/// </summary>
public sealed class AffiliateP53dPayoutEvidence(ApplicationDbContext db, IWebHostEnvironment environment)
    : IAffiliatePayoutEvidenceVerifier
{
    public const string Source = "DEV_P53D_SIMULATED_NO_TRANSFER";
    public static readonly Guid StoreId = Guid.Parse("c9c5c1ae-949e-498e-bc63-07242daeb49d");
    public static readonly Guid WithdrawalId = Guid.Parse("2c6953e7-d79c-479b-bd2d-6ba2499eda0f");
    public static readonly Guid AffiliateId = Guid.Parse("01a169fd-ffbb-77b7-b97c-547985d3cffd");
    public static readonly Guid OrderId = Guid.Parse("5aab57e4-fa68-494c-b478-9ad64af75b82");
    public static readonly Guid ProductId = Guid.Parse("2fea751c-e9ac-6650-751b-16e5e3ecb957");
    public static string Reference(Guid requestId) => $"P53D-SIMULATED-NO-TRANSFER:{requestId:D}";
    private string Directory => AffiliateP53dMailSink.DirectoryPath(environment.ContentRootPath);
    public bool IsEnabled => environment.IsDevelopment() && File.Exists(Path.Combine(Directory, "payout.enabled"));

    public async Task<bool> VerifyAsync(AffiliatePayoutTransferEvidence evidence, Guid affiliateAccountId,
        Guid storeTenantId, decimal amount, CancellationToken ct)
    {
        if (!IsEnabled || affiliateAccountId != AffiliateId || storeTenantId != StoreId || amount != 100m || evidence.IsDeleted ||
            evidence.VerificationSource != Source || evidence.StoreTenantId != StoreId ||
            evidence.ExpectedAmount != 100m || evidence.Currency != "EGP" ||
            evidence.RailNamespace != "AFFILIATE_PAYOUT" || evidence.VerifiedAtUtc == default)
            return false;
        var requestIdText = evidence.PaymentReference.Replace("P53D-SIMULATED-NO-TRANSFER:", "", StringComparison.Ordinal);
        if (!Guid.TryParseExact(requestIdText, "D", out var requestId) || requestId != WithdrawalId || evidence.PaymentReference != Reference(requestId) ||
            evidence.NormalizedPaymentReference != Reference(requestId).ToUpperInvariant() ||
            evidence.VerificationFingerprint != Fingerprint(requestId, affiliateAccountId)) return false;
        return await HasTaskAuthorityAsync(requestId, affiliateAccountId, ct);
    }

    private async Task<bool> HasTaskAuthorityAsync(Guid requestId, Guid affiliateId, CancellationToken ct)
    {
        if (!IsEnabled || requestId != WithdrawalId || affiliateId != AffiliateId) return false;
        var connection = new Npgsql.NpgsqlConnectionStringBuilder(db.Database.GetConnectionString());
        if (connection.Host != "192.168.1.5" || connection.Database != "PosEcommerceDb_Baseline") return false;
        await db.Database.OpenConnectionAsync(ct);
        await using (var command = db.Database.GetDbConnection().CreateCommand())
        {
            command.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
            command.CommandText = "SELECT current_database(), host(inet_server_addr())";
            await using var reader = await command.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct) || reader.GetString(0) != "PosEcommerceDb_Baseline" || reader.GetString(1) != "192.168.1.5") return false;
        }
        var request = await db.AffiliatePayoutRequests.IgnoreQueryFilters().AsNoTracking().SingleOrDefaultAsync(r =>
            r.Id == requestId && r.StoreTenantId == StoreId && r.AffiliateAccountId == affiliateId, ct);
        if (request?.Status != PayoutRequestStatus.Approved || request.ApprovedAmount != 100m || request.Currency != "EGP") return false;
        var identity = await db.AffiliateUsers.IgnoreQueryFilters().AsNoTracking().AnyAsync(u =>
            u.AffiliateAccountId == affiliateId && u.Email == AffiliateP53dMailSink.Recipient && u.EmailVerified && !u.IsDeleted, ct);
        if (!identity) return false;
        var approved = await db.AffiliateCommissionLedger.IgnoreQueryFilters().AsNoTracking().Where(e =>
            e.AffiliateAccountId == affiliateId && e.StoreTenantId == StoreId && e.EntryType == CommissionEntryType.Approved)
            .ToListAsync(ct);
        if (approved.Count != 1 || approved[0].CommissionAmount != 100m || approved[0].OrderId != OrderId) return false;
        var line = approved[0];
        return await db.OrderItems.IgnoreQueryFilters().AsNoTracking().AnyAsync(i => i.Id == line.OrderItemId &&
            i.OrderId == line.OrderId && i.Quantity == 1 && i.ProductId == ProductId &&
            db.Orders.IgnoreQueryFilters().Any(o => o.Id == i.OrderId && o.TenantId == StoreId) &&
            db.Products.IgnoreQueryFilters().Any(p => p.Id == i.ProductId && p.TenantId == StoreId && p.SKU == "P53D-20261023"), ct);
    }

    private static string Fingerprint(Guid requestId, Guid affiliateId) => Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes($"{Source}\n{StoreId:D}\n{requestId:D}\n{affiliateId:D}\n100\nEGP")));

    public async Task RegisterLocalFactAsync(Guid requestId, CancellationToken ct)
    {
        if (!IsEnabled || requestId != WithdrawalId) throw new InvalidOperationException("P53D simulation disabled or wrong fixed withdrawal");
        var request = await db.AffiliatePayoutRequests.IgnoreQueryFilters().AsNoTracking().SingleAsync(r => r.Id == requestId, ct);
        if (!await HasTaskAuthorityAsync(requestId, request.AffiliateAccountId, ct))
            throw new InvalidOperationException("P53D fixed-task authority mismatch");
        var reference = Reference(requestId);
        if (await db.AffiliatePayoutTransferEvidence.IgnoreQueryFilters().AnyAsync(e => e.NormalizedPaymentReference == reference.ToUpperInvariant(), ct)) return;
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var evidence = AffiliatePayoutTransferEvidence.Create(StoreId, "AFFILIATE_PAYOUT", reference, 100m, "EGP",
            DateTime.UtcNow, Source, Fingerprint(requestId, request.AffiliateAccountId));
        db.AffiliatePayoutTransferEvidence.Add(evidence);
        db.AuditLogs.Add(new AuditLog { TenantId = StoreId, UserId = request.ApprovedBy,
            ActionType = "P53D_SIMULATED_PAYOUT_FACT", EntityType = nameof(AffiliatePayoutTransferEvidence),
            EntityId = evidence.Id.ToString(), Source = Source, SuccessFlag = true,
            Description = $"DEVELOPMENT SIMULATION ONLY; NO BANK/WALLET/PROVIDER TRANSFER. Fixed withdrawal {requestId}; local explicit opt-in; canonical payout still required." });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }
}

/// <summary>One local opt-in startup request, not a command endpoint or previous P5 driver.</summary>
public sealed class AffiliateP53dEvidenceAdmission(IServiceScopeFactory scopes, IWebHostEnvironment environment,
    ILogger<AffiliateP53dEvidenceAdmission> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!environment.IsDevelopment()) throw new InvalidOperationException("Development only");
        var file = Path.Combine(AffiliateP53dMailSink.DirectoryPath(environment.ContentRootPath), "payout-evidence.requested");
        if (!File.Exists(file)) return;
        var requestId = Guid.Parse((await File.ReadAllTextAsync(file, stoppingToken)).Trim());
        await using var scope = scopes.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<AffiliateP53dPayoutEvidence>().RegisterLocalFactAsync(requestId, stoppingToken);
        File.Delete(file);
        logger.LogWarning("P53D SIMULATED external fact admitted for fixed withdrawal {RequestId}; NO transfer; canonical payout not yet executed", requestId);
    }
}
#endif
