using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PointOfSale.API.Development.CommerceHub;
using PointOfSale.API.Services.Payments.Paymob;
using PointOfSale.Core.Entities;
using PointOfSale.Core.Enums;
using PointOfSale.Infrastructure.Data;

namespace PointOfSale.API.Services.CommerceHub;

/// <summary>Read-only historical simulation classification, never transfer permission.</summary>
internal static class P520FPayoutEvidenceAuthority
{
    internal static async Task<bool> HasEvidenceAsync(ApplicationDbContext db, CommerceHubPayout payout, string? environment,
        IPaymobSensitiveArtifactProtector protector, CancellationToken ct)
    {
        if (environment != Environments.Development || payout.IsDeleted || payout.Status != CommerceHubPayoutStatus.SimulatedCompleted ||
            payout.Amount <= 0 || payout.Currency != "EGP" || payout.CompletedAtUtc == null ||
            !Guid.TryParse(payout.ProcessedBy, out var actor) || actor == Guid.Empty ||
            !string.IsNullOrEmpty(payout.ExternalReferenceId) || !string.IsNullOrEmpty(payout.BankTransactionId) ||
            string.IsNullOrEmpty(payout.CompletionEvidenceFingerprint) || payout.Reference?.StartsWith("DEV-SIM:SETTLEMENT:P520F-", StringComparison.Ordinal) != true ||
            payout.CompletionEvidenceFingerprint != CommerceHubPayoutService.BuildCompletionEvidenceFingerprint(payout))
            return false;
        var parts = payout.Reference.Split(':');
        if (parts.Length != 5) return false;
        var run = parts[2];
        if (!P520FRunClock.IsAdmitted(environment, run, "ALLOW-SYNTHETIC-TIME:" + run) ||
            parts[3] != payout.TenantId.ToString("N") || parts[4] != payout.IdempotencyKey || !parts[4].StartsWith(run + "-", StringComparison.Ordinal)) return false;
        var method = await db.CommerceHubVendorPayoutMethods.IgnoreQueryFilters().AsNoTracking().SingleOrDefaultAsync(m => m.Id == payout.PayoutMethodId, ct);
        if (method == null || method.IsDeleted || method.VendorId != payout.TenantId || method.MethodType != payout.Method ||
            method.DestinationValue != "DEV-SIM:" + run + ":NO-TRANSFER" || payout.DestinationValueSnapshot != method.DestinationValue) return false;
        var items = await db.CommerceHubPayoutItems.IgnoreQueryFilters().AsNoTracking().Include(i => i.Earning).ThenInclude(e => e.SubOrder)
            .Where(i => i.PayoutId == payout.Id && !i.IsDeleted).ToListAsync(ct);
        if (items.Count == 0 || items.Sum(i => i.Amount) != payout.Amount || items.Any(i => i.Amount <= 0 ||
            i.Earning.IsDeleted || i.Earning.TenantId != payout.TenantId || i.Earning.Status != CommerceHubVendorEarningStatus.PaidOut ||
            i.Earning.PaidOutAmount < i.Amount || i.Earning.PayoutReservedAmount != 0 || i.Earning.PaidOutAtUtc != payout.CompletedAtUtc)) return false;
        foreach (var item in items)
        {
            var payment = await db.CommerceHubPayments.IgnoreQueryFilters().AsNoTracking().SingleOrDefaultAsync(p => p.Id == item.Earning.PaymentId, ct);
            if (payment == null || payment.OrderId != item.Earning.SubOrder.MarketplaceOrderId ||
                !await RefundCompletionEvidenceAuthority.HasSyntheticCaptureAsync(db, payment, protector, ct)) return false;
            var enrollment = await db.AuditLogs.IgnoreQueryFilters().AsNoTracking().Where(a => a.EntityId == payment.OrderId.ToString() &&
                a.ActionType == "P520F_CLOCK_ORDER_ENROLLED" && a.Source == "DevelopmentAcceptance" && a.SuccessFlag).ToListAsync(ct);
            if (enrollment.Count != 1 || !MatchesClock(enrollment[0].AfterDataJson, run, payment.OrderId, null)) return false;
        }
        var audits = await db.AuditLogs.IgnoreQueryFilters().AsNoTracking().Where(a => a.EntityId == payout.Id.ToString() &&
            a.EntityType == "CommerceHubPayout" && a.Source == "DevelopmentAcceptance" && a.SuccessFlag && a.UserId == actor).ToListAsync(ct);
        return new[] { "RESERVE", "REVIEW", "COMPLETE" }.All(step => audits.Any(a => a.ActionType == "P520F_PAYOUT_" + step && MatchesPayout(a.AfterDataJson, payout, run)));
    }
    private static bool MatchesPayout(string? json, CommerceHubPayout payout, string run)
    {
        try { using var doc = JsonDocument.Parse(json ?? "{}");var r = doc.RootElement;
            return r.GetProperty("PayoutId").GetGuid() == payout.Id && r.GetProperty("VendorId").GetGuid() == payout.TenantId &&
                r.GetProperty("Amount").GetDecimal() == payout.Amount && r.GetProperty("Currency").GetString() == payout.Currency &&
                r.GetProperty("Reference").GetString() == payout.Reference && r.GetProperty("IsDevelopmentSimulation").GetBoolean() &&
                !r.GetProperty("ExternalMoneyMovementConfirmed").GetBoolean() && MatchesClock(r.GetProperty("Clock").GetRawText(), run, null, payout.CompletedAtUtc);
        } catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException) { return false; }
    }
    private static bool MatchesClock(string? json, string run, Guid? order, DateTime? terminal)
    {
        try { using var doc = JsonDocument.Parse(json ?? "{}");var r = doc.RootElement;
            return r.GetProperty("RunId").GetString() == run && r.GetProperty("IsSimulatedTime").GetBoolean() &&
                !r.GetProperty("ExternalMoneyMovementConfirmed").GetBoolean() &&
                (!terminal.HasValue || r.GetProperty("VirtualUtc").GetDateTime() == terminal.Value) &&
                (!order.HasValue || r.GetProperty("EnrolledOrderIds").EnumerateArray().Any(v => v.GetGuid() == order.Value));
        } catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException) { return false; }
    }
}
