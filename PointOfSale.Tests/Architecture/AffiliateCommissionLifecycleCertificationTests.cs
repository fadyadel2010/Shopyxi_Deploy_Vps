using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using PointOfSale.Core.Entities.Affiliate;
using PointOfSale.Core.Enums;
using PointOfSale.Infrastructure.Services.Affiliate;
using PointOfSale.Tests.Affiliate;

namespace PointOfSale.Tests.Architecture;

/// <summary>
/// PASS A3 — Affiliate Commission Lifecycle Authority Certification
/// Regression-freeze tests for the Earned → Approved → Paid lifecycle and delivery-gating enforcement.
/// DO NOT DELETE OR RELAX THESE TESTS — they are production financial authority guards.
/// </summary>
public sealed class AffiliateCommissionLifecycleCertificationTests
{
    [Fact]
    public void CommissionLifecycle_MustFollowEarnedApprovedPaidFlow()
    {
        var ledger = CertificationSource.ReadRepoFile("PointOfSale.Core/Entities/Affiliate/AffiliateCommissionLedger.cs");
        var service = CertificationSource.ReadRepoFile("PointOfSale.Infrastructure/Services/Affiliate/AffiliateService.cs");

        // All lifecycle states must exist in the entity
        Assert.Contains("Earned", ledger, StringComparison.Ordinal);
        Assert.Contains("Approved", ledger, StringComparison.Ordinal);
        Assert.Contains("Paid", ledger, StringComparison.Ordinal);
        Assert.Contains("Reversed", ledger, StringComparison.Ordinal);

        // Factory methods enforce correct transitions (append-only)
        Assert.Contains("public static AffiliateCommissionLedger CreateEarned(", ledger, StringComparison.Ordinal);
        Assert.Contains("public static AffiliateCommissionLedger CreateEarnedFromSnapshot(", ledger, StringComparison.Ordinal);
        Assert.Contains("public static AffiliateCommissionLedger CreateApproved(", ledger, StringComparison.Ordinal);
        Assert.Contains("public static AffiliateCommissionLedger CreatePaid(", ledger, StringComparison.Ordinal);
        Assert.Contains("public static AffiliateCommissionLedger CreatePaidNetOfReversals(", ledger, StringComparison.Ordinal);
        Assert.Contains("public static AffiliateCommissionLedger CreateReversal", ledger, StringComparison.Ordinal);

        // AffiliateService invokes these factories correctly
        Assert.Contains("AffiliateCommissionLedger.CreateEarnedFromSnapshot", service, StringComparison.Ordinal);
        Assert.Contains("AffiliateCommissionLedger.CreateApproved", service, StringComparison.Ordinal);
    }

    [Fact]
    public void CommissionLifecycle_AppendOnly_NoPublicSetters()
    {
        var ledger = CertificationSource.ReadRepoFile("PointOfSale.Core/Entities/Affiliate/AffiliateCommissionLedger.cs");

        // Entity is documented as append-only
        Assert.Contains("Append-only commission ledger entry", ledger, StringComparison.Ordinal);

        // No BaseEntity inheritance (would expose IsDeleted, UpdatedAt mutation paths)
        Assert.DoesNotContain(": BaseEntity", ledger, StringComparison.Ordinal);
    }

    [Fact]
    public void CommissionLifecycle_EarnedIdempotencyKey_MustBePerOrderItem()
    {
        var ledger = CertificationSource.ReadRepoFile("PointOfSale.Core/Entities/Affiliate/AffiliateCommissionLedger.cs");

        // Each lifecycle state produces a unique, stable idempotency key per OrderItemId
        Assert.Contains("IdempotencyKey = $\"AFFILIATE_COMM:EARNED:{orderItemId}\"", ledger, StringComparison.Ordinal);
        Assert.Contains("IdempotencyKey = $\"AFFILIATE_COMM:APPROVED:{earnedEntry.OrderItemId}\"", ledger, StringComparison.Ordinal);
        Assert.Contains("IdempotencyKey = $\"AFFILIATE_COMM:PAID:{approvedEntry.OrderItemId}\"", ledger, StringComparison.Ordinal);
    }

    [Fact]
    public void DeliveryGating_MustGateCommissionApproval()
    {
        var service = CertificationSource.ReadRepoFile("PointOfSale.Infrastructure/Services/Affiliate/AffiliateService.cs");

        // The authority guard function must exist and be called on every approval path
        Assert.Contains("EnsureAffiliateDeliveryApprovalAuthority", service, StringComparison.Ordinal);

        // Gate 1: Physical delivery must be confirmed
        Assert.Contains("!order.IsPhysicallyDelivered", service, StringComparison.Ordinal);
        Assert.Contains("AFFILIATE_DELIVERY_AUTHORITY_NOT_CONFIRMED", service, StringComparison.Ordinal);

        // Gate 1b: Manual fulfillment must provide delivery evidence before affiliate approval
        Assert.Contains("order.FulfillmentMode == FulfillmentMode.Manual", service, StringComparison.Ordinal);
        Assert.Contains("AFFILIATE_MANUAL_DELIVERY_EVIDENCE_REQUIRED", service, StringComparison.Ordinal);
        Assert.Contains("RequireVerifiedManualDeliveryEvidence", service, StringComparison.Ordinal);
        Assert.Contains("AllowManualDeliveryEvidenceOverride", service, StringComparison.Ordinal);
        Assert.Contains("AFFILIATE_MANUAL_DELIVERY_VERIFIED_EVIDENCE_REQUIRED", service, StringComparison.Ordinal);
        Assert.Contains("Affiliate:Delivery:BreakGlass", service, StringComparison.Ordinal);
        Assert.Contains("deliveryReference", service, StringComparison.Ordinal);
        Assert.Contains("proofOfDeliveryUrl", service, StringComparison.Ordinal);

        // Gate 2: Payment must not be in a pending/failed state
        Assert.Contains("AFFILIATE_DELIVERY_PAYMENT_AUTHORITY_NOT_CONFIRMED", service, StringComparison.Ordinal);
        Assert.Contains("order.PaymentStatus", service, StringComparison.Ordinal);

        // Gate 3: Financial accounting must be posted
        Assert.Contains("AFFILIATE_DELIVERY_ACCOUNTING_AUTHORITY_NOT_CONFIRMED", service, StringComparison.Ordinal);
        Assert.Contains("order.FinancialPostingStatus", service, StringComparison.Ordinal);
    }

    [Fact]
    public void DeliveryGating_TenantMismatch_MustBlockApprovalBatch()
    {
        var service = CertificationSource.ReadRepoFile("PointOfSale.Infrastructure/Services/Affiliate/AffiliateService.cs");

        // AF-19B: All entries in the batch must have StoreTenantId matching the order tenant
        Assert.Contains("mismatchedEntries", service, StringComparison.Ordinal);
        Assert.Contains("StoreTenantId != order.TenantId", service, StringComparison.Ordinal);
    }

    [Fact]
    public void DeliveryGating_EventVersionRequired_PositiveGuard()
    {
        var service = CertificationSource.ReadRepoFile("PointOfSale.Infrastructure/Services/Affiliate/AffiliateService.cs");

        // Event version must be > 0 to prevent replay from phantom/corrupted events
        Assert.Contains("eventVersion <= 0", service, StringComparison.Ordinal);
        Assert.Contains("Affiliate delivery approval requires a positive event version", service, StringComparison.Ordinal);
    }

    [Fact]
    public void DeliveryGating_DeliveryConsumer_MustConsumeOrderAndFulfillmentEvents()
    {
        var consumer = CertificationSource.ReadRepoFile("PointOfSale.Infrastructure/Consumers/Affiliate/AffiliateSettlementConsumers.cs");

        // Must handle both delivery signal types
        Assert.Contains("IConsumer<OrderDeliveredEvent>", consumer, StringComparison.Ordinal);
        Assert.Contains("IConsumer<FulfillmentDeliveredEvent>", consumer, StringComparison.Ordinal);

        // Both must route to the authoritative approval service
        Assert.Contains("ApproveCommissionForDeliveredOrderAsync", consumer, StringComparison.Ordinal);
        Assert.Contains("evt.DeliveryReference", consumer, StringComparison.Ordinal);
        Assert.Contains("evt.ProofOfDeliveryUrl", consumer, StringComparison.Ordinal);
    }

    [Fact]
    public void ReversalBehavior_MustLinkToOriginalEntryId()
    {
        var ledger = CertificationSource.ReadRepoFile("PointOfSale.Core/Entities/Affiliate/AffiliateCommissionLedger.cs");
        var service = CertificationSource.ReadRepoFile("PointOfSale.Infrastructure/Services/Affiliate/AffiliateService.cs");

        // Reversal factories enforce OriginalEntryId link (DB constraint also enforces this)
        Assert.Contains("public static AffiliateCommissionLedger CreateReversal", ledger, StringComparison.Ordinal);
        Assert.Contains("public static AffiliateCommissionLedger CreateReversalWithCustomKey", ledger, StringComparison.Ordinal);
        Assert.Contains("OriginalEntryId = originalEntryId", ledger, StringComparison.Ordinal);

        // Service coordinates reversals on refund/return
        Assert.Contains("ReverseCommissionForOrderItemAsync", service, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReversalBehavior_MustUseRefundLineAmountsAndBoundPartialReversals()
    {
        var refundConsumer = CertificationSource.ReadRepoFile("PointOfSale.Infrastructure/Consumers/Affiliate/AffiliateRefundConsumers.cs");
        var paymentLifecycle = CertificationSource.ReadRepoFile("PointOfSale.Infrastructure/Payments/PaymentLifecycleService.cs");
        var itemRefundEvent = CertificationSource.ReadRepoFile("PointOfSale.Core/Events/Affiliate/OrderItemRefundedEvent.cs");

        Assert.Contains("RefundId", itemRefundEvent, StringComparison.Ordinal);
        Assert.Contains("RefundId = refundId", paymentLifecycle, StringComparison.Ordinal);

        Assert.Contains("refundedBaseAmount", refundConsumer, StringComparison.Ordinal);
        Assert.Contains("BuildCommissionRefundReversalIdempotencyKey", refundConsumer, StringComparison.Ordinal);
        Assert.Contains("AFFILIATE_COMM:REVERSED:{refundId}", refundConsumer, StringComparison.Ordinal);

        await using var f = new AffiliatePhase4CommissionLifecycleTests.Fixture(delivered: true);
        await f.Service.RecordCommissionForOrderAsync(f.Order.Id);
        var earned = Assert.Single((await f.Ledger()).Where(e => e.EntryType == CommissionEntryType.Earned));
        foreach (var (cumulative, expectedBase, expectedCommission) in new[]
                 { (25m, 25m, 2.5m), (60m, 60m, 6m), (160m, 100m, 10m) })
        {
            f.Item.RefundedAmount = cumulative;
            await f.Db.SaveChangesAsync();
            // The event is a hint, never refund authority. SQL cumulative amount wins.
            await f.Service.ReverseCommissionForOrderItemAsync(f.Order.TenantId, f.Order.Id, f.Item.Id,
                refundedBaseAmount: 999m, refundIdempotencyKey: $"refund-{cumulative}");
            var beforeReplay = (await f.Ledger()).Count;
            await f.Service.RecordCommissionForOrderAsync(f.Order.Id);
            await f.Service.ReverseCommissionForOrderItemAsync(f.Order.TenantId, f.Order.Id, f.Item.Id,
                refundedBaseAmount: 999m, refundIdempotencyKey: $"redelivery-{cumulative}");
            var rows = await f.Ledger();
            Assert.Equal(beforeReplay, rows.Count);
            var reversals = rows.Where(e => e.EntryType == CommissionEntryType.Reversed).ToList();
            Assert.All(reversals, e => Assert.Equal(earned.Id, e.OriginalEntryId));
            Assert.Equal(expectedBase, reversals.Sum(e => e.BaseAmount));
            Assert.Equal(expectedCommission, reversals.Sum(e => e.CommissionAmount));
            var balance = await new AffiliatePayoutService(f.Db, NullLogger<AffiliatePayoutService>.Instance)
                .GetAvailableBalanceAsync(f.Affiliate.Id);
            Assert.Equal(10m - expectedCommission, balance.RawAvailable);
        }
        Assert.Equal(new[] { 25m, 35m, 40m }, (await f.Ledger())
            .Where(e => e.EntryType == CommissionEntryType.Reversed).OrderBy(e => e.CreatedAt).Select(e => e.BaseAmount));
    }

    [Fact]
    public void ReversalBehavior_MustBlockApprovalOnlyForFullyReversedItems()
    {
        var service = CertificationSource.ReadRepoFile("PointOfSale.Infrastructure/Services/Affiliate/AffiliateService.cs");

        // A partial reversal leaves a payable remainder; only a full reversal blocks approval.
        Assert.Contains("IsFullyReversed(earned, reversalTotalsByOriginalEntryId)", service, StringComparison.Ordinal);
        Assert.Contains("OrderItem {OrderItemId} was fully reversed — skipping approval", service, StringComparison.Ordinal);
    }

    [Fact]
    public void PayoutExecution_MustDelegateToUnifiedOrchestratorOnly()
    {
        var orchestrator = CertificationSource.ReadRepoFile("PointOfSale.Infrastructure/Services/Affiliate/AffiliateSettlementOrchestrator.cs");
        var payoutService = CertificationSource.ReadRepoFile("PointOfSale.Infrastructure/Services/Affiliate/AffiliatePayoutService.cs");

        // The unified pipeline must be the single payout authority
        Assert.Contains("public partial class AffiliateSettlementOrchestrator : IAffiliateSettlementOrchestrator", orchestrator, StringComparison.Ordinal);
        var manual = CertificationSource.ReadRepoFile("PointOfSale.Infrastructure/Services/Affiliate/AffiliateSettlementOrchestrator.Manual.cs");
        Assert.Contains("ExecuteWithFreshRetryScopeAsync(o => o.ExecutePayoutCoreAsync(", manual, StringComparison.Ordinal);
        Assert.DoesNotContain("AffiliateQaPayoutSimulation", manual, StringComparison.Ordinal);
        Assert.DoesNotContain("AllowManualPayoutEvidenceOverride", manual, StringComparison.Ordinal);
        Assert.Contains("public async Task<PayoutExecutionResult> ExecuteAffiliatePayoutAsync", orchestrator, StringComparison.Ordinal);
        Assert.Contains("BeginTransactionAsync(IsolationLevel.Serializable", orchestrator, StringComparison.Ordinal);

        // Legacy path must be blocked
        Assert.Contains("NotSupportedException", payoutService, StringComparison.Ordinal);
        Assert.Contains("MarkPayoutPaidAsync is DEPRECATED", payoutService, StringComparison.Ordinal);
        Assert.Contains("[Obsolete", payoutService, StringComparison.Ordinal);
    }

    [Fact]
    public void PayoutExecution_MustUseMandatoryPipelineOrder()
    {
        var orchestrator = CertificationSource.ReadRepoFile("PointOfSale.Infrastructure/Services/Affiliate/AffiliateSettlementOrchestrator.cs");

        // All 7 pipeline steps must be present (verified by their unique markers)
        // Step 1: Serializable transaction
        Assert.Contains("STEP 1 — Begin SERIALIZABLE Transaction", orchestrator, StringComparison.Ordinal);
        // Step 2: Balance validation
        Assert.Contains("STEP 2 — Validate Available Balance", orchestrator, StringComparison.Ordinal);
        Assert.Contains("RawAvailable", orchestrator, StringComparison.Ordinal);
        // Step 3: BalanceLedger.Withdrawal
        Assert.Contains("STEP 3 — Create BalanceLedger.Withdrawal", orchestrator, StringComparison.Ordinal);
        // Step 4: CommissionLedger.Paid
        Assert.Contains("STEP 4 — Mark CommissionLedger Entries as Paid", orchestrator, StringComparison.Ordinal);
        // Step 5: Auto-reconcile withdrawal requests
        Assert.Contains("STEP 5 — Auto-Reconcile Withdrawal Requests", orchestrator, StringComparison.Ordinal);
        // Step 6: Mark PayoutRequest as Paid (inside transaction)
        Assert.Contains("STEP 6 — Mark PayoutRequest as Paid", orchestrator, StringComparison.Ordinal);
        // Step 7: Commit
        Assert.Contains("STEP 7 — Commit Transaction", orchestrator, StringComparison.Ordinal);
    }
}
