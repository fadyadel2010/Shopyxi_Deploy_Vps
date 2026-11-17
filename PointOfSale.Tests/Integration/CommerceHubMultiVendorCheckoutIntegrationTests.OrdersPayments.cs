using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using PointOfSale.API.Configuration;
using PointOfSale.API.DTOs.CommerceHub;
using PointOfSale.API.Instrumentation;
using PointOfSale.API.Services.CommerceHub;
using PointOfSale.API.Services.CommerceHub.Finance;
using PointOfSale.API.Services.CommerceHub.FinancialLedger;
using PointOfSale.API.Services.Payments.Paymob;
using PointOfSale.API.Services.Payments.ProviderContracts;
using PointOfSale.API.SuperAdmin.Services;
using PointOfSale.Core.Entities;
using PointOfSale.Core.Enums;
using PointOfSale.Infrastructure.Data;
using PointOfSale.Infrastructure.Data.Repositories;
using PointOfSale.Infrastructure.Redis.Services;
using PointOfSale.Tests.Infrastructure;

namespace PointOfSale.Tests.Integration;

public sealed partial class CommerceHubMultiVendorCheckoutIntegrationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OrdersForensic_OnlineCollectionRetryAndPartialFullRefunds_UseRealSqlFinancialAndInventoryServices(bool failFirstSession)
    {
        using var db = FinancePostgresTestContext.Create();
        await db.Database.ExecuteSqlRawAsync("""
            CREATE SEQUENCE public."CommerceHubMarketplaceOrderNumberSeq"
                START WITH 1 INCREMENT BY 1 MINVALUE 1 NO MAXVALUE CACHE 1 NO CYCLE
            """);
        var fixture = await SeedCheckoutAsync(db, 10m);
        var rule = new CommissionRule { Id = Guid.NewGuid(), ScopeType = CommissionRuleScopeType.Global, Rate = .1m, IsActive = true };
        db.CommissionRules.Add(rule);
        await db.SaveChangesAsync();
        var publisher = CreateEventPublisher();
        var commission = new CommissionService(db, Options.Create(new CommerceHubPaymentsOptions()),
            new CommissionServiceTelemetry(TimeSpan.FromSeconds(30)), NullLogger<CommissionService>.Instance, publisher.Object);
        var adapter = new Mock<IPaymentProviderAdapter>();
        adapter.SetupGet(a => a.ProviderCode).Returns(new ProviderCode(CommerceHubPaymentMethods.Paymob));
        adapter.SetupGet(a => a.Capabilities).Returns(new PaymentProviderCapabilities(true, true, true, true, true, true, true, true,
            new Dictionary<string, string>()));
        var sessions = 0;
        adapter.Setup(a => a.CreateSessionAsync(It.IsAny<PaymentProviderSessionRequest>(), It.IsAny<CancellationToken>()))
            .Returns<PaymentProviderSessionRequest, CancellationToken>((request, _) =>
            {
                sessions++;
                if (failFirstSession && sessions == 1)
                    throw new PaymentProviderAdapterException(CanonicalProviderError.Validation("PAYMOB", "FIXTURE_DEFINITIVE_DECLINE", "Isolated initiation failed; no external session exists."));
                return Task.FromResult(new PaymentProviderSessionResult(
                    $"https://payments.example.test/{request.PaymentId:N}", "fixture-only-session",
                    $"fixture-intent-{request.PaymentId:N}", "{}"));
            });
        var checkout = CreateService(db,
            new ProductVariantStockRepository(db, NullLogger<ProductVariantStockRepository>.Instance),
            publisher.Object, fixture, commissionAuthority: commission, providerAdapters: new[] { adapter.Object });
        var request = BuildRequest(fixture);
        request.PaymentMethod = CommerceHubPaymentMethods.Paymob;
        var response = await checkout.CheckoutAsync(request, fixture.CustomerId, "online-fixture", "online-fixture-checkout-key");
        var inventory = new InventoryConsistencyService(db, NullLogger<InventoryConsistencyService>.Instance);
        var ledger = new CommerceHubFinancialLedgerService(db, NullLogger<CommerceHubFinancialLedgerService>.Instance);
        var audit = new CommerceHubFinancialAuditService(db);
        var financial = new CommerceHubFinancialWorkflowService(db,
            Options.Create(new CommerceHubPaymentsOptions()), Options.Create(new CommerceHubPayoutSettings()),
            ledger, audit, NullLogger<CommerceHubFinancialWorkflowService>.Instance);
        var stops = new Mock<IOptionsMonitor<CommerceHubEmergencyStopOptions>>();
        stops.SetupGet(o => o.CurrentValue).Returns(new CommerceHubEmergencyStopOptions());
        var readiness = new CommerceHubFinancialReadinessGate(db, stops.Object, NullLogger<CommerceHubFinancialReadinessGate>.Instance);
        var locks = new Mock<IDistributedLockService>();
        locks.Setup(l => l.TryAcquireLockAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<TimeSpan>()))
            .ReturnsAsync(DistributedLockResult.Acquired());
        locks.Setup(l => l.ReleaseLockAsync(It.IsAny<string>(), It.IsAny<string>())).ReturnsAsync(true);
        var payments = new CommerceHubPaymentService(db, financial, inventory, readiness,
            Mock.Of<ICommerceHubOrderAccessService>(), publisher.Object, locks.Object, new[] { adapter.Object },
            Options.Create(new CommerceHubPaymentsOptions()), new CommerceHubPaymentOrchestrationTelemetry(),
            NullLogger<CommerceHubPaymentService>.Instance,
            new PaymobSensitiveArtifactProtector(new EphemeralDataProtectionProvider()));
        var initiation = new CommerceHubPaymentInitiateRequestDto
        {
            OrderId = response.MarketplaceOrderId.ToString(), Provider = CommerceHubPaymentMethods.Paymob
        };
        if (failFirstSession)
        {
            var decline = await Assert.ThrowsAsync<CommerceHubCheckoutException>(() =>
                payments.InitiateAsync(initiation, fixture.CustomerId, "failed-session"));
            Assert.Equal("FIXTURE_DEFINITIVE_DECLINE", decline.Code);
            Assert.Equal(CommerceHubPaymentRecordStatus.Failed, (await db.CommerceHubPayments.SingleAsync()).Status);
        }
        var session = await payments.InitiateAsync(initiation, fixture.CustomerId, "successful-session");
        var paid = await db.CommerceHubPayments.SingleAsync(p => p.Id == session.PaymentId);
        PaymentProviderWebhookResult callback = new()
        {
            ProviderReference = paid.ProviderReference, ProviderIntentId = paid.ProviderIntentId,
            TransactionId = $"fixture-transaction-{paid.Id:N}", EventType = "TRANSACTION", Currency = "EGP",
            AmountMinorUnits = 48000, Pending = true
        };
        adapter.Setup(a => a.ParseWebhookAsync(It.IsAny<PaymentProviderWebhookRequest>(), It.IsAny<CancellationToken>()))
            .Returns(() => Task.FromResult(callback));
        await payments.ProcessWebhookAsync("PAYMOB", "fixture-adapter-verified", "{}", "pending-callback");
        Assert.Equal(PaymentStatus.Pending, (await db.CommerceHubMarketplaceOrders.SingleAsync()).PaymentStatus);
        Assert.Equal(0, await db.CommerceHubFinancialTransactions.CountAsync());
        callback = callback with { Pending = false, Success = true };
        Assert.True((await payments.ProcessWebhookAsync("PAYMOB", "fixture-adapter-verified", "{}", "collected-callback")).Processed);
        var entries = await db.CommerceHubFinancialTransactions.CountAsync();
        Assert.True((await payments.ProcessWebhookAsync("PAYMOB", "fixture-adapter-verified", "{}", "duplicate-callback")).Processed);
        Assert.Equal(entries, await db.CommerceHubFinancialTransactions.CountAsync());
        Assert.Equal(2, await db.CommerceHubVendorEarnings.CountAsync());
        Assert.Single(await db.CommerceHubPayments.Where(p => p.Status == CommerceHubPaymentRecordStatus.Paid).ToListAsync());
        var parent = await db.CommerceHubMarketplaceOrders.Include(o => o.SubOrders).ThenInclude(s => s.Items).SingleAsync();
        Assert.Equal(PaymentStatus.Collected, parent.PaymentStatus);
        var sub = parent.SubOrders.Single(s => s.StoreId == fixture.First.StoreId);
        var snapshot = sub.CommissionSnapshotJson;
        var currentRule = await db.CommissionRules.SingleAsync();
        currentRule.Rate = .25m; await db.SaveChangesAsync();
        var evidence = new Mock<IRefundEvidenceService>();
        evidence.Setup(e => e.GetEvidenceLookupAsync(It.IsAny<IEnumerable<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<Guid, IReadOnlyList<RefundEvidenceMediaDto>>());
        var reader = new SuperAdminOrdersService(db, evidence.Object, NullLogger<SuperAdminOrdersService>.Instance);
        var detail = await reader.GetOrderDetailsAsync(parent.Id);
        Assert.NotNull(detail); Assert.Equal(PaymentStatus.Collected, detail.PaymentStatus);
        Assert.Equal(failFirstSession ? 2 : 1, detail.Payments.Count);
        Assert.Equal(0, (await reader.GetOrderStatsAsync()).FailedPayments);
        Assert.Equal(480m, (await reader.GetOrderStatsAsync()).CapturedSalesToday);
        Assert.Equal(snapshot, detail.SubOrders.Single(s => s.SubOrderId == sub.Id).CommissionSnapshotJson);
        Assert.Equal(20m, detail.SubOrders.Single(s => s.SubOrderId == sub.Id).PlatformCommissionAmount);
        var gateway = new Mock<IRefundGateway>();
        gateway.Setup(g => g.RefundAsync(It.IsAny<CanonicalRefundRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((CanonicalRefundRequest r, CancellationToken _) =>
                new CommerceHubGatewayRefundResult(true, true, "FixtureOnly", "fixture-refund-" + r.IdempotencyKey, "confirmed", null));
        var refunds = new CommerceHubRefundService(db, new CommerceHubRefundGatewayService(gateway.Object, db), ledger, audit, publisher.Object,
            new RefundCompletionValidator(db), new CommerceHubFinancialSafetyGuardService(db),
            readiness, locks.Object, NullLogger<CommerceHubRefundService>.Instance);
        var partialRequest = new CommerceHubCreateRefundRequestDto
        {
            SubOrderId = sub.Id, Amount = 50m, Reason = "Isolated partial refund", IdempotencyKey = "orders-fixture-partial"
        };
        var notPosted = await Assert.ThrowsAsync<CommerceHubCheckoutException>(() =>
            refunds.CreateRefundAsync(sub.StoreId, partialRequest, "unposted-refund-must-block"));
        Assert.Equal("COMMERCEHUB_LEDGER_ACCOUNTING_COVERAGE_REQUIRED", notPosted.Code);
        gateway.Verify(g => g.RefundAsync(It.IsAny<CanonicalRefundRequest>(), It.IsAny<CancellationToken>()), Times.Never);
        await PostAndReconcileOrdersFixtureAsync(db);
        var partial = await refunds.CreateRefundAsync(sub.StoreId, partialRequest, "partial-refund");
        Assert.Equal("Completed", partial.Status);
        var replay = await refunds.CreateRefundAsync(sub.StoreId, partialRequest, "partial-replay");
        Assert.Equal(partial.RefundId, replay.RefundId);
        await PostAndReconcileOrdersFixtureAsync(db);
        var excessive = await Assert.ThrowsAsync<CommerceHubCheckoutException>(() => refunds.CreateRefundAsync(sub.StoreId, new()
        {
            SubOrderId = sub.Id, Amount = 161m, Reason = "Must reject over-refund", IdempotencyKey = "orders-fixture-excess"
        }, "excess-refund"));
        Assert.Equal("REFUND_AMOUNT_EXCEEDED", excessive.Code);
        var full = await refunds.CreateRefundAsync(sub.StoreId, new()
        {
            SubOrderId = sub.Id, Amount = 160m, Reason = "Isolated remaining refund", IdempotencyKey = "orders-fixture-remaining"
        }, "remaining-refund");
        Assert.Equal("Completed", full.Status);
        detail = await reader.GetOrderDetailsAsync(parent.Id);
        Assert.NotNull(detail); Assert.Equal(2, detail.Refunds.Count);
        Assert.Equal(PaymentStatus.Refunded, detail.SubOrders.Single(s => s.SubOrderId == sub.Id).PaymentStatus);
        Assert.Equal(210m, detail.Refunds.Sum(r => r.Amount));
        Assert.Equal(20m, detail.Refunds.Sum(r => r.CommissionReversalAmount));
        Assert.Equal(snapshot, detail.SubOrders.Single(s => s.SubOrderId == sub.Id).CommissionSnapshotJson);
        await PostAndReconcileOrdersFixtureAsync(db);
    }
}
