using Microsoft.EntityFrameworkCore;
using PointOfSale.API.Services.Payments.Paymob;
using PointOfSale.Core.Enums;
using PointOfSale.Core.Enums.CommerceHubFinance;
using PointOfSale.Core.Interfaces.Accounting;
using PointOfSale.Infrastructure.Data;

namespace PointOfSale.API.Services.CommerceHub;

public sealed class P520FPayoutPostingAuthority(ApplicationDbContext db, IWebHostEnvironment environment,
    IPaymobSensitiveArtifactProtector protector) : IDevelopmentPayoutPostingAuthority
{
    public async Task<bool> IsAuthorizedAsync(Guid transactionId, Guid tenantId, string currency, decimal amount, CancellationToken ct)
    {
        if (!environment.IsDevelopment() || currency != "EGP" || amount <= 0) return false;
        var transaction = await db.CommerceHubFinancialTransactions.AsNoTracking().SingleOrDefaultAsync(t =>
            t.Id == transactionId && t.EntityType == CommerceHubFinancialEntityType.Platform && t.EntityId == null &&
            t.TransactionType == CommerceHubFinancialTransactionType.Settlement && t.Direction == CommerceHubFinancialTransactionDirection.Debit &&
            t.FinancialBucket == CommerceHubFinancialBucket.Cash && t.ReferenceType == CommerceHubFinancialReferenceType.Payout &&
            t.Amount == amount && t.Currency == currency, ct);
        if (transaction == null) return false;
        var payout = await db.CommerceHubPayouts.AsNoTracking().SingleOrDefaultAsync(p => p.Id == transaction.ReferenceId &&
            p.TenantId == tenantId && p.Amount == amount && p.Currency == currency && !p.IsDeleted, ct);
        return payout != null && await P520FPayoutEvidenceAuthority.HasEvidenceAsync(db, payout, environment.EnvironmentName, protector, ct);
    }
}
