using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PointOfSale.API.Services.CommerceHub;
using PointOfSale.Core.Entities;
using PointOfSale.Core.Enums;
using PointOfSale.Infrastructure.Data;
using Microsoft.AspNetCore.DataProtection;
using PointOfSale.API.Services.Payments.Paymob;

namespace PointOfSale.Tests.CommerceHub;

public sealed class P520ERefundProvenanceTests
{
    [Theory]
    [InlineData("valid", "Development", true)]
    [InlineData("valid", "Production", false)]
    [InlineData("valid", "Staging", false)]
    [InlineData("valid", null, false)]
    [InlineData("external-capture", "Development", false)]
    [InlineData("missing-capture-log", "Development", false)]
    [InlineData("invalid-capture-log", "Development", false)]
    [InlineData("amount", "Development", false)]
    [InlineData("wrong-tenant", "Development", false)]
    [InlineData("missing-audit", "Development", false)]
    [InlineData("external-attestation", "Development", false)]
    [InlineData("wrong-refund", "Development", false)]
    [InlineData("failed-execution", "Development", false)]
    [InlineData("bare-status", "Development", false)]
    [InlineData("duplicate-execution", "Development", false)]
    [InlineData("protected", "Development", true)]
    [InlineData("protected", "Production", false)]
    [InlineData("protected-no-key", "Development", false)]
    [InlineData("protected-tampered", "Development", false)]
    [InlineData("wrong-capture-event", "Development", false)]
    [InlineData("wrong-capture-order", "Development", false)]
    public async Task CommittedSimulationRequiresCompleteLineageAndCannotCertifyProduction(string defect, string? environment, bool expected)
    {
        await using var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var actor = Guid.NewGuid(); var tenant = Guid.NewGuid(); var now = DateTime.UtcNow;
        var order = new CommerceHubMarketplaceOrder { Id = Guid.NewGuid() };
        var sub = new CommerceHubSubOrder { Id = Guid.NewGuid(), StoreId = tenant, MarketplaceOrderId = order.Id, MarketplaceOrder = order };
        var item = new CommerceHubSubOrderItem { Id = Guid.NewGuid(), SubOrderId = sub.Id, SubOrder = sub };
        var payment = new CommerceHubPayment { Id = Guid.NewGuid(), OrderId = order.Id, Order = order,
            Status = CommerceHubPaymentRecordStatus.Paid, Amount = 100, Currency = "EGP" };
        payment.ProviderTransactionId = defect == "external-capture" ? "provider-real-reference" : $"DEV-SIM:ONLINE:{payment.Id:N}:test";
        var request = new CommerceHubRefundRequest { Id = Guid.NewGuid(), VendorId = tenant, OrderId = order.Id, Order = order,
            OrderItemId = item.Id, OrderItem = item, Quantity = 1, TotalAmount = 100, Status = CommerceHubRefundRequestStatus.Paid,
            CompletedAtUtc = now, PayoutMethodSnapshot = RefundPayoutMethod.VodafoneCash, PayoutAccountSnapshot = "synthetic-not-a-destination" };
        var refund = new CommerceHubRefund { Id = Guid.NewGuid(), TenantId = tenant, PaymentId = payment.Id, SubOrderId = sub.Id,
            OrderItemId = item.Id, Quantity = 1, Amount = 100, Currency = "EGP", RefundRequestId = request.Id,
            Status = CommerceHubRefundStatus.Completed, Reference = "DEV-SIM:ONLINE-REFUND:test", GatewayStatus = "development-simulated-online-refund", ProcessedAtUtc = now };
        var execution = new CommerceHubRefundExecution { Id = Guid.NewGuid(), RefundId = request.Id, Refund = request,
            Amount = defect == "amount" ? 99 : 100, Currency = "EGP", Method = RefundPayoutMethod.VodafoneCash,
            AccountSnapshot = request.PayoutAccountSnapshot, TransactionReference = refund.Reference,
            ExecutedBy = actor, ExecutedAt = now, IsSuccessful = defect != "failed-execution" };
        var capture = new CommerceHubPaymentLog { Id = Guid.NewGuid(), PaymentId = payment.Id, ProviderEventId = payment.ProviderTransactionId, EventType = "DevelopmentOnlinePaymentSuccessSimulation",
            RawResponse = defect == "invalid-capture-log" ? "{}" : JsonSerializer.Serialize(new { EvidenceType = "DevelopmentOnlinePaymentSuccessSimulation",
                IsDevelopmentSimulation = true, ExternalMoneyMovementConfirmed = false, PaymentId = payment.Id, OrderId = defect == "wrong-capture-order" ? Guid.NewGuid() : order.Id,
                SimulationReference = payment.ProviderTransactionId, ActorId = actor }) };
        var protector = new PaymobSensitiveArtifactProtector(new EphemeralDataProtectionProvider());
        if (defect.StartsWith("protected", StringComparison.Ordinal)) capture.RawResponse = protector.ProtectWebhookPayload(capture.RawResponse);
        if (defect == "protected-tampered") capture.RawResponse += "tampered";
        if (defect == "wrong-capture-event") capture.ProviderEventId = "foreign-reference";
        object Details(bool completion) => completion
            ? new { RefundId = request.Id, FinancialRefundId = defect == "wrong-refund" ? Guid.NewGuid() : refund.Id, ExecutionId = execution.Id,
                ActorId = actor, Amount = 100m, Currency = "EGP", Method = 0, TransactionReference = refund.Reference,
                IsDevelopmentSimulation = true, ExternalMoneyMovementConfirmed = defect == "external-attestation" }
            : new { RefundId = request.Id, ActorId = actor, Amount = 100m, Currency = "EGP", Method = 0, TransactionReference = refund.Reference,
                IsDevelopmentSimulation = true, ExternalMoneyMovementConfirmed = false, ConfirmMoneySent = false };
        var audits = new[] { false, true }.Select(completion => {
            var action = completion ? "REFUND_ONLINE_BEFORE_PAYOUT_SIMULATION_COMPLETED" : "REFUND_ONLINE_BEFORE_PAYOUT_SIMULATION_INITIATED";
            return new AuditLog { ActionType = action, EntityType = "CommerceHubRefundRequest", EntityId = request.Id.ToString(), UserId = actor,
                Source = "SuperAdminAPI", SuccessFlag = true, TimestampUtc = now,
                AfterDataJson = JsonSerializer.Serialize(new { ActorId = actor, EntityId = request.Id.ToString(), Action = action, Details = Details(completion) }) };
        }).ToArray();
        if (defect == "wrong-tenant") request.VendorId = Guid.NewGuid();
        db.CommerceHubMarketplaceOrders.Add(order); db.CommerceHubSubOrders.Add(sub); db.CommerceHubSubOrderItems.Add(item);
        db.CommerceHubRefundRequests.Add(request); db.CommerceHubRefunds.Add(refund); db.CommerceHubPayments.Add(payment);
        if (defect != "bare-status") db.CommerceHubRefundExecutions.Add(execution);
        if (defect == "duplicate-execution") db.CommerceHubRefundExecutions.Add(new CommerceHubRefundExecution { Id = Guid.NewGuid(), RefundId = request.Id });
        if (defect != "missing-capture-log") db.CommerceHubPaymentLogs.Add(capture);
        if (defect != "missing-audit") db.AuditLogs.AddRange(audits);
        using (CommerceHubBoundaryMutationScope.BeginOrderLifecycle())
        using (CommerceHubBoundaryMutationScope.BeginPayment())
        using (CommerceHubBoundaryMutationScope.BeginPaymentLifecycle())
        using (CommerceHubBoundaryMutationScope.BeginRefund())
        using (CommerceHubBoundaryMutationScope.BeginFinancialWorkflow()) await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        Assert.Equal(expected, await RefundCompletionEvidenceAuthority.HasDevelopmentSimulationEvidenceAsync(db, refund, payment, environment,
            protector: defect == "protected-no-key" ? null : protector));
        Assert.False(await RefundCompletionEvidenceAuthority.HasManualPayoutEvidenceAsync(db, refund, payment));
        Assert.False(RefundCompletionEvidenceAuthority.IsProviderConfirmed(refund.GatewayStatus));
        Assert.False(db.ChangeTracker.HasChanges());
        Assert.Equal("PAYOUT_DEVELOPMENT_SIMULATION_NOT_TRANSFERABLE", Assert.Throws<CommerceHubCheckoutException>(() =>
            PayoutSyntheticEvidencePolicy.RejectExternalEvidence(payment.ProviderTransactionId, refund.Reference)).Code);
    }
}
