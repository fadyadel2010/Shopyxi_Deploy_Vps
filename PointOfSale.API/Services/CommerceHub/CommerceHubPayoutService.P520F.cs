using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PointOfSale.API.Configuration;
using PointOfSale.API.DTOs.CommerceHub;
using PointOfSale.API.Development.CommerceHub;
using PointOfSale.API.Services.Payments.Paymob;
using PointOfSale.Core.Entities;
using PointOfSale.Core.Enums;

namespace PointOfSale.API.Services.CommerceHub;

public partial class CommerceHubPayoutService
{
    // Separate staged internal lifecycle. Never reachable through the normal transfer interface.
    internal async Task<CommerceHubPayoutDto> StepP520FAsync(string run, string step, Guid tenantId,
        CommerceHubCreatePayoutRequestDto request, string actor, IPaymobSensitiveArtifactProtector protector,
        RefundSettings refundSettings, CancellationToken ct)
    {
        if (_acceptanceClock is not { Enabled: true } clock || clock.RunId != run)
            throw new CommerceHubCheckoutException("P520F_DISABLED", "Exact Development clock opt-in required.", 404);
        if (!Guid.TryParse(actor, out var actorId) || actorId == Guid.Empty ||
            request.EarningIds.Count == 0 || request.EarningIds.Distinct().Count() != request.EarningIds.Count ||
            !request.IdempotencyKey.StartsWith(run + "-", StringComparison.Ordinal) || !request.PayoutMethodId.HasValue ||
            step is not ("reserve" or "review" or "complete"))
            throw new CommerceHubCheckoutException("P520F_PROVENANCE_INVALID", "Run, actor, method, unique earnings and lifecycle step required.", 400);
        var earnings = await _dbContext.CommerceHubVendorEarnings.AsNoTracking()
            .Include(e => e.SubOrder).ThenInclude(s => s.Items)
            .Where(e => request.EarningIds.Contains(e.Id) && e.TenantId == tenantId && !e.IsDeleted).ToListAsync(ct);
        if (earnings.Count != request.EarningIds.Count || earnings.Any(e => !clock.Owns(e.SubOrder.MarketplaceOrderId) ||
                e.Amount <= 0 || e.SubOrder.PaymentMethod != CommerceHubPaymentMethod.Online || e.SubOrder.Status != OrderStatus.Delivered))
            throw new CommerceHubCheckoutException("P520F_OWNERSHIP_REQUIRED", "Only new clock-enrolled delivered online earnings are admitted.", 409);
        foreach (var earning in earnings)
        {
            var payment = await _dbContext.CommerceHubPayments.AsNoTracking().SingleOrDefaultAsync(p => p.Id == earning.PaymentId && !p.IsDeleted, ct);
            if (payment == null || payment.OrderId != earning.SubOrder.MarketplaceOrderId ||
                !await RefundCompletionEvidenceAuthority.HasSyntheticCaptureAsync(_dbContext, payment, protector, ct))
                throw new CommerceHubCheckoutException("P520F_CAPTURE_REQUIRED", "Complete synthetic capture lineage required.", 409);
            var now = clock.ForOrder(payment.OrderId);
            if (earning.AvailableAtUtc is not { } available || now < available)
                throw new CommerceHubCheckoutException("PAYOUT_DELAY_NOT_ELAPSED", "The configured delay has not elapsed in simulated time.", 409);
            if (earning.SubOrder.Items.Any(i => CustomerRefundRequestPolicy.IsAnyClaimWindowOpen(i.DeliveredAt, refundSettings, now)))
                throw new CommerceHubCheckoutException("PAYOUT_REFUND_WINDOW_OPEN", "The configured refund window is still open in simulated time.", 409);
        }
        var method = await _dbContext.CommerceHubVendorPayoutMethods.AsNoTracking().SingleOrDefaultAsync(m =>
            m.Id == request.PayoutMethodId && m.VendorId == tenantId && !m.IsDeleted, ct);
        if (method == null || method.DestinationValue != "DEV-SIM:" + run + ":NO-TRANSFER")
            throw new CommerceHubCheckoutException("P520F_METHOD_REQUIRED", "Exact run-owned synthetic destination required.", 409);
        request.Reference = $"DEV-SIM:SETTLEMENT:{run}:{tenantId:N}:{request.IdempotencyKey}";
        var payout = await FindPayoutByIdempotencyAsync(tenantId, request.IdempotencyKey, ct);
        if (payout != null && (payout.Reference != request.Reference || payout.PayoutMethodId != request.PayoutMethodId ||
            !payout.Items.Select(i => i.EarningId).Order().SequenceEqual(request.EarningIds.Order())))
            throw new CommerceHubCheckoutException("P520F_REPLAY_CONFLICT", "Existing payout lineage differs from this replay.", 409);
        CommerceHubPayoutDto result;
        if (step == "reserve") result = await CreatePayoutCoreAsync(tenantId, request, run, true, ct);
        else
        {
            if (payout == null) throw new CommerceHubCheckoutException("PAYOUT_NOT_FOUND", "Reserve before review.", 404);
            var update = new CommerceHubUpdatePayoutRequestDto { Reference = request.Reference,
                ExternalTransferIdempotencyKey = payout.ExternalTransferIdempotencyKey, ExternalTransferHandoffToken = payout.ExternalTransferHandoffToken };
            result = step == "review"
                ? await MarkProcessingCoreAsync(tenantId, payout.Id, update, run, true, ct, actor)
                : await MarkCompletedCoreAsync(tenantId, payout.Id, update, run, true, ct, actor);
        }
        _dbContext.AuditLogs.Add(new AuditLog { ActionType = "P520F_PAYOUT_" + step.ToUpperInvariant(), EntityType = "CommerceHubPayout",
            EntityId = result.PayoutId.ToString(), TenantId = tenantId, UserId = actorId, Source = "DevelopmentAcceptance", SuccessFlag = true,
            Description = "Internal synthetic payout lifecycle; simulated time, no transfer or external finality.",
            AfterDataJson = JsonSerializer.Serialize(new { Clock = clock.Provenance(), result.PayoutId, VendorId = tenantId,
                result.Amount, result.Currency, result.Reference, result.Status, request.PayoutMethodId, request.EarningIds,
                IsDevelopmentSimulation = true, ExternalMoneyMovementConfirmed = false }) });
        await _dbContext.SaveChangesAsync(ct);
        return result;
    }
}
