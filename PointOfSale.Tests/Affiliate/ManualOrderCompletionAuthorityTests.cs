using PointOfSale.Core.Entities;
using PointOfSale.Core.Enums;
using PointOfSale.Infrastructure.Fulfillment;

namespace PointOfSale.Tests.Affiliate;

/// <summary>Pure authority regressions; no secondary database, test server or provider.</summary>
public sealed class ManualOrderCompletionAuthorityTests
{
    private static (Order Order, User Actor, AuditLog Audit) Completed()
    {
        var tenant = Guid.NewGuid();
        var order = new Order { Id = Guid.NewGuid(), TenantId = tenant, OrderNumber = "P52-UNIT-NO-MONEY",
            FulfillmentMode = FulfillmentMode.Manual, FulfillmentStatus = FulfillmentStatus.Completed, Status = OrderStatus.Completed,
            PaymentMethodCode = "COD", PaymentStatus = PaymentStatus.Collected, FinancialPostingStatus = FinancialPostingStatus.Posted,
            Total = 1500, Currency = "EGP", DeliveryReference = "P5_SIMULATED_NO_MONEY", ProofOfDeliveryUrl = "https://example.invalid/pod" };
        var actor = new User { Id = Guid.NewGuid(), TenantId = tenant, Role = UserRole.Admin, Username = "P52-UNIT" };
        return (order, actor, ManualOrderCompletionAuthority.CreateAudit(order, actor, DateTime.UtcNow, "P52-UNIT-COMPLETE"));
    }

    [Fact]
    public void AuthorizedCommandAuditBindsOrderTenantActorAndPersistedProof()
    {
        var (order, actor, audit) = Completed();
        Assert.True(ManualOrderCompletionAuthority.Matches(order, audit));
        Assert.Equal(actor.Id, audit.UserId);
        Assert.Equal(0, audit.TimestampUtc.Ticks % 10); // Survives PostgreSQL microsecond storage.
    }

    [Theory]
    [InlineData("unpaid")]
    [InlineData("accounting-pending")]
    [InlineData("accounting-failed")]
    [InlineData("raw-status")]
    [InlineData("delivered-only")]
    [InlineData("tenant")]
    [InlineData("order")]
    [InlineData("actor")]
    [InlineData("source")]
    [InlineData("action")]
    [InlineData("failed-audit")]
    [InlineData("proof")]
    [InlineData("reference")]
    [InlineData("missing-snapshot")]
    [InlineData("invalid-snapshot")]
    [InlineData("deleted")]
    [InlineData("carrier")]
    public void StatusTypedEvidenceOrMismatchedAuditCannotQualify(string kind)
    {
        var (order, _, audit) = Completed();
        switch (kind)
        {
            case "unpaid": order.PaymentStatus = PaymentStatus.Pending; break;
            case "accounting-pending": order.FinancialPostingStatus = FinancialPostingStatus.Pending; break;
            case "accounting-failed": order.FinancialPostingStatus = FinancialPostingStatus.PostingFailed; break;
            case "raw-status": order.FulfillmentStatus = FulfillmentStatus.OutForDelivery; break;
            case "delivered-only": order.Status = OrderStatus.Delivered; order.FulfillmentStatus = FulfillmentStatus.Delivered; break;
            case "tenant": audit.TenantId = Guid.NewGuid(); break;
            case "order": audit.EntityId = Guid.NewGuid().ToString(); break;
            case "actor": audit.UserId = Guid.NewGuid(); break;
            case "source": audit.Source = "generic-status-update"; break;
            case "action": audit.ActionType = "Update"; break;
            case "failed-audit": audit.SuccessFlag = false; break;
            case "proof": order.ProofOfDeliveryUrl = "https://example.invalid/substitution"; break;
            case "reference": order.DeliveryReference = "substitution"; break;
            case "missing-snapshot": audit.AfterDataJson = null; break;
            case "invalid-snapshot": audit.AfterDataJson = "not-json"; break;
            case "deleted": order.IsDeleted = true; break;
            case "carrier": order.FulfillmentMode = FulfillmentMode.Carrier; break;
        }
        Assert.False(ManualOrderCompletionAuthority.Matches(order, audit));
    }

    [Theory]
    [InlineData(UserRole.Owner, true)]
    [InlineData(UserRole.Admin, true)]
    [InlineData(UserRole.Manager, true)]
    [InlineData(UserRole.Cashier, false)]
    [InlineData(UserRole.Warehouse, false)]
    public void OnlyExistingPosManagementRolesQualify(UserRole role, bool allowed)
    {
        var (order, actor, _) = Completed(); actor.Role = role;
        var audit = ManualOrderCompletionAuthority.CreateAudit(order, actor, DateTime.UtcNow, null);
        Assert.Equal(allowed, ManualOrderCompletionAuthority.Matches(order, audit));
    }

    [Fact]
    public void AuditWriteCapabilityIsExactScopedAndDoesNotLeak()
    {
        var (_, _, first) = Completed(); var (_, _, other) = Completed();
        Assert.False(CompletionAuditWriteAuthority.Allows(first));
        using (CompletionAuditWriteAuthority.Begin(first))
        {
            Assert.True(CompletionAuditWriteAuthority.Allows(first));
            Assert.False(CompletionAuditWriteAuthority.Allows(other));
            using (CompletionAuditWriteAuthority.Begin(other)) Assert.True(CompletionAuditWriteAuthority.Allows(other));
            Assert.True(CompletionAuditWriteAuthority.Allows(first));
        }
        Assert.False(CompletionAuditWriteAuthority.Allows(first));
    }
}
