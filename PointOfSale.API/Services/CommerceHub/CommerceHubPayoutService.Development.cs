using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using PointOfSale.API.DTOs.CommerceHub;
using PointOfSale.Core.Entities;
using PointOfSale.Core.Enums;

namespace PointOfSale.API.Services.CommerceHub;

/// <summary>Synthetic evidence is never external-transfer certification, including in Development.</summary>
public static class PayoutSyntheticEvidencePolicy
{
    public static bool IsSynthetic(string? reference) => reference?.Trim().StartsWith("DEV-SIM:", StringComparison.OrdinalIgnoreCase) == true;

    public static void RejectExternalEvidence(params string?[] references)
    {
        if (references.Any(IsSynthetic))
            throw new CommerceHubCheckoutException("PAYOUT_DEVELOPMENT_SIMULATION_NOT_TRANSFERABLE",
                "Synthetic Development settlement evidence is not proof of an external transfer.", StatusCodes.Status409Conflict);
    }
}

public partial class CommerceHubPayoutService
{
    private static void ValidatePayoutEvidenceMode(CommerceHubPayout payout, CommerceHubUpdatePayoutRequestDto request, bool developmentSettlement)
    {
        if (!developmentSettlement)
        {
            PayoutSyntheticEvidencePolicy.RejectExternalEvidence(payout.Reference, payout.ExternalReferenceId,
                payout.BankTransactionId, request.Reference, request.ExternalReferenceId, request.BankTransactionId);
            return;
        }

        if (!PayoutSyntheticEvidencePolicy.IsSynthetic(payout.Reference) || request.Reference != payout.Reference ||
            request.ExternalTransferConfirmedExecuted || !string.IsNullOrWhiteSpace(request.ExternalReferenceId) ||
            !string.IsNullOrWhiteSpace(request.BankTransactionId) || !string.IsNullOrWhiteSpace(payout.ExternalReferenceId) ||
            !string.IsNullOrWhiteSpace(payout.BankTransactionId))
            throw new CommerceHubCheckoutException("DEVELOPMENT_SETTLEMENT_PROVENANCE_INVALID",
                "Development settlement requires exact synthetic provenance and cannot certify an external transfer.", StatusCodes.Status409Conflict);
    }

    // Only the Development-only Super Admin controller calls this method; the external payout interface has no simulation flag.
    internal async Task<CommerceHubPayoutDto> ExecuteDevelopmentSettlementAsync(
        IWebHostEnvironment environment, IConfiguration configuration, Guid tenantId,
        CommerceHubCreatePayoutRequestDto request, string actorId, string correlationId, CancellationToken cancellationToken)
    {
        if (!environment.IsDevelopment() || !configuration.GetValue<bool>("CommerceHub:DevelopmentAcceptance:PaymentSessions"))
            throw new CommerceHubCheckoutException("DEVELOPMENT_SETTLEMENT_DISABLED", "Development settlement is disabled.", StatusCodes.Status404NotFound);
        if (request.EarningIds.Count == 0 || !request.IdempotencyKey.StartsWith("P520D-20261028-E2E01-", StringComparison.Ordinal))
            throw new CommerceHubCheckoutException("DEVELOPMENT_SETTLEMENT_RUN_REQUIRED", "Exact test run ownership is required.", StatusCodes.Status400BadRequest);
        var ownedVendor = await _dbContext.Tenants.IgnoreQueryFilters().AnyAsync(tenant => tenant.Id == tenantId &&
            tenant.IsActive && tenant.Name.StartsWith("P520D-20261028-E2E01-"), cancellationToken);
        var ownedEarnings = await _dbContext.CommerceHubVendorEarnings.IgnoreQueryFilters().AsNoTracking()
            .Include(earning => earning.SubOrder).ThenInclude(suborder => suborder.MarketplaceOrder)
            .Where(earning => request.EarningIds.Contains(earning.Id) && earning.TenantId == tenantId && !earning.IsDeleted)
            .ToListAsync(cancellationToken);
        var paymentIds = ownedEarnings.Where(earning => earning.PaymentId.HasValue).Select(earning => earning.PaymentId!.Value).Distinct().ToList();
        var syntheticPayments = await _dbContext.CommerceHubPayments.IgnoreQueryFilters().AsNoTracking()
            .Where(payment => paymentIds.Contains(payment.Id) && !payment.IsDeleted && payment.Status == CommerceHubPaymentRecordStatus.Paid &&
                payment.ProviderTransactionId != null && payment.ProviderTransactionId.StartsWith("DEV-SIM:ONLINE:"))
            .Select(payment => payment.Id).ToListAsync(cancellationToken);
        if (!ownedVendor || ownedEarnings.Count != request.EarningIds.Distinct().Count() ||
            ownedEarnings.Any(earning => !earning.PaymentId.HasValue || !syntheticPayments.Contains(earning.PaymentId.Value) ||
                earning.SubOrder.Status != OrderStatus.Delivered || earning.SubOrder.Currency != "EGP"))
            throw new CommerceHubCheckoutException("DEVELOPMENT_SETTLEMENT_OWNERSHIP_REQUIRED",
                "Only delivered run-owned earnings backed by explicit synthetic payment evidence can be test-settled.", StatusCodes.Status409Conflict);

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(request.IdempotencyKey)))[..24];
        request.Reference = $"DEV-SIM:SETTLEMENT:P520D-20261028-E2E01:{tenantId:N}:{hash}";
        var payout = await CreatePayoutCoreAsync(tenantId, request, correlationId, true, cancellationToken);
        var update = new CommerceHubUpdatePayoutRequestDto { Reference = request.Reference, ExternalTransferIdempotencyKey = payout.ExternalTransferIdempotencyKey };
        payout = await MarkProcessingCoreAsync(tenantId, payout.PayoutId, update, correlationId, true, cancellationToken, actorId);
        update.ExternalTransferHandoffToken = payout.ExternalTransferHandoffToken;
        return await MarkCompletedCoreAsync(tenantId, payout.PayoutId, update, correlationId, true, cancellationToken, actorId);
    }
}
