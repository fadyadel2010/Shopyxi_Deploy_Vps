using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PointOfSale.Core.Entities;
using PointOfSale.Core.Enums;
using PointOfSale.Infrastructure.Data;
using PointOfSale.API.Services.Payments.Paymob;
using System.Security.Cryptography;

namespace PointOfSale.API.Services.CommerceHub;

/// <summary>
/// Read-only classification of committed refund authority. The original payment
/// provider does not decide how the customer was refunded. Manual payout requires
/// its complete operator-attested SQL execution and audit lineage, never a status
/// string promoted into provider confirmation. This grants no mutation authority.
/// </summary>
public static class RefundCompletionEvidenceAuthority
{
    public static bool IsProviderConfirmed(string? status) => status != null &&
        new[] { "confirmed", "succeeded", "success", "completed", "complete", "refunded" }
            .Contains(status, StringComparer.OrdinalIgnoreCase);

    public static async Task<bool> HasManualPayoutEvidenceAsync(ApplicationDbContext db,
        CommerceHubRefund refund, CommerceHubPayment payment, CancellationToken ct = default)
        => await HasExecutionEvidenceAsync(db, refund, payment, false, ct);

    // Classification of an already committed, explicitly opted-in simulation, not
    // an execution switch or external attestation. Production always fails closed.
    public static async Task<bool> HasDevelopmentSimulationEvidenceAsync(ApplicationDbContext db,
        CommerceHubRefund refund, CommerceHubPayment payment, string? environmentName, CancellationToken ct = default,
        IPaymobSensitiveArtifactProtector? protector = null)
    {
        if (environmentName != Environments.Development ||
            payment.IsDeleted || payment.Status != CommerceHubPaymentRecordStatus.Paid ||
            payment.ProviderTransactionId?.StartsWith($"DEV-SIM:ONLINE:{payment.Id:N}:", StringComparison.Ordinal) != true ||
            refund.Reference?.StartsWith("DEV-SIM:ONLINE-REFUND:", StringComparison.Ordinal) != true)
            return false;
        var logs = await db.CommerceHubPaymentLogs.IgnoreQueryFilters().AsNoTracking()
            .Where(log => log.PaymentId == payment.Id && !log.IsDeleted &&
                log.EventType == "DevelopmentOnlinePaymentSuccessSimulation").ToListAsync(ct);
        if (!logs.Any(log => log.ProviderEventId == payment.ProviderTransactionId && MatchesSyntheticCapture(log.RawResponse, payment, protector))) return false;
        return await HasExecutionEvidenceAsync(db, refund, payment, true, ct);
    }

    private static bool MatchesSyntheticCapture(string json, CommerceHubPayment payment, IPaymobSensitiveArtifactProtector? protector)
    {
        try
        {
            if (json.StartsWith(PaymobSensitiveArtifactProtector.WebhookPayloadPrefix, StringComparison.Ordinal))
            {
                if (protector == null) return false;
                json = protector.UnprotectWebhookPayload(json);
            }
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            return root.GetProperty("EvidenceType").GetString() == "DevelopmentOnlinePaymentSuccessSimulation" &&
                root.GetProperty("IsDevelopmentSimulation").GetBoolean() &&
                !root.GetProperty("ExternalMoneyMovementConfirmed").GetBoolean() &&
                root.GetProperty("PaymentId").GetGuid() == payment.Id &&
                root.GetProperty("OrderId").GetGuid() == payment.OrderId &&
                root.GetProperty("SimulationReference").GetString() == payment.ProviderTransactionId &&
                root.GetProperty("ActorId").GetGuid() != Guid.Empty;
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException or CryptographicException or ArgumentException)
        { return false; }
    }

    internal static async Task<bool> HasSyntheticCaptureAsync(ApplicationDbContext db, CommerceHubPayment payment,
        IPaymobSensitiveArtifactProtector protector, CancellationToken ct)
    {
        if (payment.IsDeleted || payment.Status != CommerceHubPaymentRecordStatus.Paid ||
            payment.ProviderTransactionId?.StartsWith($"DEV-SIM:ONLINE:{payment.Id:N}:", StringComparison.Ordinal) != true) return false;
        var logs = await db.CommerceHubPaymentLogs.IgnoreQueryFilters().AsNoTracking().Where(l => l.PaymentId == payment.Id &&
            !l.IsDeleted && l.EventType == "DevelopmentOnlinePaymentSuccessSimulation").ToListAsync(ct);
        return logs.Any(l => l.ProviderEventId == payment.ProviderTransactionId && MatchesSyntheticCapture(l.RawResponse, payment, protector));
    }

    private static async Task<bool> HasExecutionEvidenceAsync(ApplicationDbContext db,
        CommerceHubRefund refund, CommerceHubPayment payment, bool simulation, CancellationToken ct)
    {
        if (refund.IsDeleted || refund.Status != CommerceHubRefundStatus.Completed ||
            refund.GatewayStatus != (simulation ? "development-simulated-online-refund" : "manual-confirmed") || refund.RefundRequestId is not { } requestId ||
            refund.PaymentId != payment.Id || refund.Amount <= 0 || refund.Currency != payment.Currency ||
            string.IsNullOrWhiteSpace(refund.Reference) || !refund.ProcessedAtUtc.HasValue)
            return false;

        var request = await db.CommerceHubRefundRequests.IgnoreQueryFilters().AsNoTracking()
            .SingleOrDefaultAsync(r => r.Id == requestId && r.VendorId == refund.TenantId && !r.IsDeleted, ct);
        if (request == null || request.Status != CommerceHubRefundRequestStatus.Paid ||
            request.OrderId != payment.OrderId || request.OrderItemId != refund.OrderItemId ||
            request.Quantity != refund.Quantity || request.TotalAmount != refund.Amount ||
            request.CompletedAtUtc != refund.ProcessedAtUtc || !request.PayoutMethodSnapshot.HasValue ||
            string.IsNullOrWhiteSpace(request.PayoutAccountSnapshot)) return false;
        if (!await db.CommerceHubSubOrderItems.IgnoreQueryFilters().AsNoTracking().AnyAsync(i =>
                i.Id == request.OrderItemId && i.SubOrderId == refund.SubOrderId && !i.IsDeleted &&
                i.SubOrder.StoreId == refund.TenantId && i.SubOrder.MarketplaceOrderId == payment.OrderId && !i.SubOrder.IsDeleted, ct)) return false;

        var executions = await db.CommerceHubRefundExecutions.IgnoreQueryFilters().AsNoTracking()
            .Where(e => e.RefundId == requestId).ToListAsync(ct);
        if (executions.Count != 1) return false;
        var execution = executions[0];
        if (execution.IsDeleted || !execution.IsSuccessful || !string.IsNullOrWhiteSpace(execution.FailureReason) ||
            execution.ExecutedBy == Guid.Empty || execution.Amount != refund.Amount || execution.Currency != refund.Currency ||
            !Enum.IsDefined(execution.Method) || execution.TransactionReference != refund.Reference ||
            execution.AccountSnapshot != request.PayoutAccountSnapshot || execution.ExecutedAt != refund.ProcessedAtUtc) return false;

        var initiatedAction = simulation ? "REFUND_ONLINE_BEFORE_PAYOUT_SIMULATION_INITIATED" : "REFUND_PAYOUT_INITIATED";
        var completedAction = simulation ? "REFUND_ONLINE_BEFORE_PAYOUT_SIMULATION_COMPLETED" : "REFUND_PAYOUT_CONFIRMED";
        var logs = await db.AuditLogs.IgnoreQueryFilters().AsNoTracking().Where(log =>
            log.EntityType == "CommerceHubRefundRequest" && log.EntityId == requestId.ToString() &&
            log.UserId == execution.ExecutedBy && log.Source == "SuperAdminAPI" && log.SuccessFlag &&
            (log.ActionType == initiatedAction || log.ActionType == completedAction) &&
            log.TimestampUtc >= execution.ExecutedAt.AddMinutes(-5) && log.TimestampUtc <= execution.ExecutedAt.AddMinutes(5))
            .ToListAsync(ct);
        return logs.Any(log => MatchesAudit(log, requestId, refund, execution, false, simulation, initiatedAction)) &&
               logs.Any(log => MatchesAudit(log, requestId, refund, execution, true, simulation, completedAction));
    }

    private static bool MatchesAudit(AuditLog log, Guid requestId, CommerceHubRefund refund,
        CommerceHubRefundExecution execution, bool completion, bool simulation, string action)
    {
        if (log.ActionType != action) return false;
        try
        {
            using var document = JsonDocument.Parse(log.AfterDataJson ?? "{}");
            var root = document.RootElement;
            if (root.GetProperty("ActorId").GetGuid() != execution.ExecutedBy ||
                root.GetProperty("EntityId").GetString() != requestId.ToString() ||
                root.GetProperty("Action").GetString() != log.ActionType) return false;
            var details = root.GetProperty("Details");
            if (details.GetProperty("RefundId").GetGuid() != requestId ||
                details.GetProperty("ActorId").GetGuid() != execution.ExecutedBy ||
                details.GetProperty("Amount").GetDecimal() != refund.Amount ||
                details.GetProperty("Currency").GetString() != refund.Currency ||
                details.GetProperty("Method").GetInt32() != (int)execution.Method ||
                details.GetProperty("TransactionReference").GetString() != refund.Reference ||
                details.GetProperty("IsDevelopmentSimulation").GetBoolean() != simulation ||
                details.GetProperty("ExternalMoneyMovementConfirmed").GetBoolean() == simulation) return false;
            return completion
                ? details.GetProperty("FinancialRefundId").GetGuid() == refund.Id && details.GetProperty("ExecutionId").GetGuid() == execution.Id
                : details.GetProperty("ConfirmMoneySent").GetBoolean() != simulation;
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        { return false; }
    }
}
