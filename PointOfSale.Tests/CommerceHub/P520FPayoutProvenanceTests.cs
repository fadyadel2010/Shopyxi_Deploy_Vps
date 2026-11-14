using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using PointOfSale.API.Services.CommerceHub;
using PointOfSale.API.Services.Payments.Paymob;
using PointOfSale.Core.Entities;
using PointOfSale.Core.Entities.CommerceHubFinance;
using PointOfSale.Core.Enums.CommerceHubFinance;
using Moq;
using PointOfSale.Core.Enums;
using PointOfSale.Infrastructure.Data;

namespace PointOfSale.Tests.CommerceHub;

public class P520FPayoutProvenanceTests
{
    [Theory]
    [InlineData("valid", "Development", true)]
    [InlineData("valid", "Production", false)]
    [InlineData("valid", "Staging", false)]
    [InlineData("valid", null, false)]
    [InlineData("bare-status", "Development", false)]
    [InlineData("missing-review", "Development", false)]
    [InlineData("external-attestation", "Development", false)]
    [InlineData("bank-reference", "Development", false)]
    [InlineData("bad-fingerprint", "Development", false)]
    [InlineData("foreign-method", "Development", false)]
    [InlineData("foreign-order", "Development", false)]
    [InlineData("real-capture", "Development", false)]
    [InlineData("tampered-capture", "Development", false)]
    [InlineData("reserved-earning", "Development", false)]
    [InlineData("amount", "Development", false)]
    [InlineData("clock-time", "Development", false)]
    public async Task Historical_classification_requires_complete_lineage_without_execution_opt_in(string defect, string? environment, bool expected)
    {
        const string run = "P520F-20261028-TERMINAL01";
        await using var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var now = DateTime.UtcNow;var actor = Guid.NewGuid();var vendor = Guid.NewGuid();
        var order = new CommerceHubMarketplaceOrder { Id = Guid.NewGuid() };
        var sub = new CommerceHubSubOrder { Id = Guid.NewGuid(), MarketplaceOrderId = order.Id, MarketplaceOrder = order, StoreId = vendor };
        var payment = new CommerceHubPayment { Id = Guid.NewGuid(), OrderId = order.Id, Order = order, Status = CommerceHubPaymentRecordStatus.Paid, Currency = "EGP", Amount = 100 };
        payment.ProviderTransactionId = defect == "real-capture" ? "bank-reference" : $"DEV-SIM:ONLINE:{payment.Id:N}:test";
        var method = new CommerceHubVendorPayoutMethod { Id = Guid.NewGuid(), VendorId = defect == "foreign-method" ? Guid.NewGuid() : vendor,
            MethodType = CommerceHubPayoutMethod.BankTransfer, DestinationValue = "DEV-SIM:" + run + ":NO-TRANSFER" };
        var payout = new CommerceHubPayout { Id = Guid.NewGuid(), TenantId = vendor, Amount = 90, Currency = "EGP", Status = CommerceHubPayoutStatus.SimulatedCompleted,
            PayoutMethodId = method.Id, Method = method.MethodType, DestinationValueSnapshot = method.DestinationValue, IdempotencyKey = run + "-payout1",
            ProcessedBy = actor.ToString(), ProcessedAtUtc = now, CompletedAtUtc = now, BankTransactionId = defect == "bank-reference" ? "fabricated" : null };
        payout.Reference = $"DEV-SIM:SETTLEMENT:{run}:{vendor:N}:{payout.IdempotencyKey}";
        payout.CompletionEvidenceFingerprint = defect == "bad-fingerprint" ? "bad" : CommerceHubPayoutService.BuildCompletionEvidenceFingerprint(payout);
        var earning = new CommerceHubVendorEarning { Id = Guid.NewGuid(), TenantId = vendor, SubOrderId = sub.Id, SubOrder = sub, PaymentId = payment.Id,
            Amount = 90, PaidOutAmount = 90, PayoutReservedAmount = defect == "reserved-earning" ? 90 : 0, PaidOutAtUtc = now, Status = CommerceHubVendorEarningStatus.PaidOut };
        var item = new CommerceHubPayoutItem { Id = Guid.NewGuid(), PayoutId = payout.Id, Payout = payout, EarningId = earning.Id, Earning = earning, Amount = defect == "amount" ? 89 : 90 };
        var protector = new PaymobSensitiveArtifactProtector(new EphemeralDataProtectionProvider());
        var payload = protector.ProtectWebhookPayload(JsonSerializer.Serialize(new { EvidenceType = "DevelopmentOnlinePaymentSuccessSimulation", IsDevelopmentSimulation = true,
            ExternalMoneyMovementConfirmed = false, PaymentId = payment.Id, OrderId = order.Id, SimulationReference = payment.ProviderTransactionId, ActorId = actor }));
        var log = new CommerceHubPaymentLog { Id = Guid.NewGuid(), PaymentId = payment.Id, ProviderEventId = payment.ProviderTransactionId,
            EventType = "DevelopmentOnlinePaymentSuccessSimulation", RawResponse = defect == "tampered-capture" ? payload + "tampered" : payload };
        object Clock(bool terminal) => new { RunId = run, IsSimulatedTime = true, VirtualUtc = terminal && defect == "clock-time" ? now.AddDays(1) : now,
            ExternalMoneyMovementConfirmed = false, EnrolledOrderIds = new[] { defect == "foreign-order" ? Guid.NewGuid() : order.Id } };
        db.AddRange(order, sub, payment, method, payout, earning, item, log);
        var cash = CommerceHubFinancialTransaction.Create(CommerceHubFinancialEntityType.Platform, null,
            CommerceHubFinancialTransactionType.Settlement, CommerceHubFinancialTransactionDirection.Debit, 90, "EGP",
            CommerceHubFinancialReferenceType.Payout, payout.Id, "P520F internal synthetic payout cash; no external transfer");
        db.CommerceHubFinancialTransactions.Add(cash);
        if (defect != "bare-status")
        {
            db.AuditLogs.Add(new AuditLog { ActionType = "P520F_CLOCK_ORDER_ENROLLED", EntityId = order.Id.ToString(), Source = "DevelopmentAcceptance", SuccessFlag = true, AfterDataJson = JsonSerializer.Serialize(Clock(false)) });
            foreach (var step in new[] { "RESERVE", "REVIEW", "COMPLETE" }.Where(s => defect != "missing-review" || s != "REVIEW"))
                db.AuditLogs.Add(new AuditLog { ActionType = "P520F_PAYOUT_" + step, EntityType = "CommerceHubPayout", EntityId = payout.Id.ToString(), Source = "DevelopmentAcceptance", SuccessFlag = true, UserId = actor,
                    AfterDataJson = JsonSerializer.Serialize(new { Clock = Clock(true), PayoutId = payout.Id, VendorId = vendor, Amount = payout.Amount, Currency = "EGP", Reference = payout.Reference,
                        IsDevelopmentSimulation = true, ExternalMoneyMovementConfirmed = defect == "external-attestation" }) });
        }
        using (CommerceHubBoundaryMutationScope.BeginOrderLifecycle())
        using (CommerceHubBoundaryMutationScope.BeginPayment())
        using (CommerceHubBoundaryMutationScope.BeginPaymentLifecycle())
        using (CommerceHubBoundaryMutationScope.BeginPayout())
        using (CommerceHubBoundaryMutationScope.BeginFinancialWorkflow()) await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        Assert.Equal(expected, await P520FPayoutEvidenceAuthority.HasEvidenceAsync(db, payout, environment, protector, default));
        var env = new Mock<Microsoft.AspNetCore.Hosting.IWebHostEnvironment>();env.SetupGet(e => e.EnvironmentName).Returns(environment!);
        var posting = new P520FPayoutPostingAuthority(db, env.Object, protector);
        Assert.Equal(expected, await posting.IsAuthorizedAsync(cash.Id, vendor, "EGP", 90, default));
        Assert.False(await posting.IsAuthorizedAsync(cash.Id, Guid.NewGuid(), "EGP", 90, default));
        Assert.False(await posting.IsAuthorizedAsync(cash.Id, vendor, "EGP", 91, default));
        Assert.False(await posting.IsAuthorizedAsync(cash.Id, vendor, "USD", 90, default));
        Assert.False(db.ChangeTracker.HasChanges());
        Assert.Throws<CommerceHubCheckoutException>(() => PayoutSyntheticEvidencePolicy.RejectExternalEvidence(payout.Reference));
    }
}
