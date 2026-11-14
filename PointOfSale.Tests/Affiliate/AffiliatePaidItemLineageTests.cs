using PointOfSale.Core.Events.Affiliate;
using PointOfSale.Infrastructure.Consumers.Affiliate;
using PointOfSale.Infrastructure.MongoDB.Documents.Affiliate;

namespace PointOfSale.Tests.Affiliate;

public sealed class AffiliatePaidItemLineageTests
{
    private static (AffiliateOrderDocument Order, AffiliateCommissionPaidEvent Event) PartialOrder()
    {
        var first = Guid.NewGuid();
        var order = new AffiliateOrderDocument
        {
            OrderId = Guid.NewGuid(), TenantId = Guid.NewGuid(), AffiliateAccountId = Guid.NewGuid(),
            Currency = "EGP", TotalCommission = 350, TotalApproved = 350,
            Items = [new() { OrderItemId = first, CommissionAmount = 150, SettlementStatus = "Approved" },
                new() { OrderItemId = Guid.NewGuid(), CommissionAmount = 200, SettlementStatus = "Approved" }]
        };
        return (order, new AffiliateCommissionPaidEvent
        {
            OrderId = order.OrderId, StoreTenantId = order.TenantId, AffiliateAccountId = order.AffiliateAccountId,
            Currency = "EGP", TotalPaid = 150, ItemCount = 1, PaidItems = [new(first, 150)]
        });
    }

    [Fact]
    public void PartialOrderPayout_SelectsOnlyCommittedItem_LeavingOtherApprovedItemAlone()
    {
        var (order, fact) = PartialOrder();
        AffiliateCommissionPaidProjectionConsumer.ValidatePaidUpstream(order, fact, fact.EventId.ToString());
        Assert.Equal([order.Items[0].OrderItemId], AffiliateCommissionPaidProjectionConsumer.ResolvePaidItemIds(order, fact));
        Assert.Equal("Approved", order.Items[1].SettlementStatus);
    }

    [Fact]
    public void LegacyAggregateCannotGuessPartialSelection()
    {
        var (order, fact) = PartialOrder(); fact.PaidItems.Clear();
        AssertInvalid(order, fact);
    }

    [Theory]
    [InlineData("duplicate")]
    [InlineData("unknown")]
    [InlineData("empty-id")]
    [InlineData("zero")]
    [InlineData("amount")]
    [InlineData("count")]
    [InlineData("unapproved")]
    [InlineData("substituted-item")]
    [InlineData("duplicate-projection-id")]
    [InlineData("null-selection")]
    public void CorruptSelectionIsRejectedBeforeProjectionEffects(string corruption)
    {
        var (order, fact) = PartialOrder();
        switch (corruption)
        {
            case "duplicate": fact.PaidItems.Add(fact.PaidItems[0]); fact.TotalPaid = 300; fact.ItemCount = 2; break;
            case "unknown": fact.PaidItems[0] = new(Guid.NewGuid(), 150); break;
            case "empty-id": fact.PaidItems[0] = new(Guid.Empty, 150); break;
            case "zero": fact.PaidItems[0] = new(order.Items[0].OrderItemId, 0); break;
            case "amount": fact.PaidItems[0] = new(order.Items[0].OrderItemId, 100); fact.TotalPaid = 100; break;
            case "count": fact.ItemCount = 2; break;
            case "unapproved": order.Items[0].SettlementStatus = "Earned"; break;
            case "substituted-item": fact.PaidItems[0] = new(order.Items[1].OrderItemId, 150); break;
            case "duplicate-projection-id": order.Items[1].OrderItemId = order.Items[0].OrderItemId; break;
            case "null-selection": fact.PaidItems = null!; break;
        }
        AssertInvalid(order, fact);
    }

    [Fact]
    public void NetOfPartialRefundSelectionMatchesExactApprovedRemainder()
    {
        var (order, fact) = PartialOrder(); order.Items[0].ReversedAmount = 25;
        fact.PaidItems[0] = new(order.Items[0].OrderItemId, 125); fact.TotalPaid = 125;
        AffiliateCommissionPaidProjectionConsumer.ValidatePaidUpstream(order, fact, fact.EventId.ToString());
    }

    [Fact]
    public void SeparateSameSchemaVersionPayoutsCanSelectDisjointItems()
    {
        var (order, first) = PartialOrder();
        order.Items[0].SettlementStatus = "Paid";
        order.Items[0].CommissionPaidEventId = first.EventId.ToString();
        order.TotalPaid = 150; order.ProcessedEventIds.Add(first.EventId.ToString());
        var second = new AffiliateCommissionPaidEvent
        {
            OrderId = order.OrderId, StoreTenantId = order.TenantId, AffiliateAccountId = order.AffiliateAccountId,
            Currency = "EGP", TotalPaid = 200, ItemCount = 1, EventVersion = first.EventVersion,
            PaidItems = [new(order.Items[1].OrderItemId, 200)]
        };
        AffiliateCommissionPaidProjectionConsumer.ValidatePaidUpstream(order, second, second.EventId.ToString());
        Assert.Equal([order.Items[1].OrderItemId], AffiliateCommissionPaidProjectionConsumer.ResolvePaidItemIds(order, second));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PaidReplayRequiresSelectedItemReceipt_NotAnotherItemsPaidState(bool receiptPresent)
    {
        var (order, fact) = PartialOrder();
        order.TotalPaid = 150; order.ProcessedEventIds.Add(fact.EventId.ToString());
        order.Items[0].SettlementStatus = "Paid";
        if (receiptPresent) order.Items[0].CommissionPaidEventId = fact.EventId.ToString();
        else order.Items[1].CommissionPaidEventId = fact.EventId.ToString();
        if (receiptPresent) AffiliateCommissionPaidProjectionConsumer.ValidatePaidUpstream(order, fact, fact.EventId.ToString());
        else AssertInvalid(order, fact);
    }

    private static void AssertInvalid(AffiliateOrderDocument order, AffiliateCommissionPaidEvent fact) =>
        Assert.Contains("AFFILIATE_PAID_PROJECTION_UPSTREAM_INVALID", Assert.Throws<InvalidOperationException>(() =>
            AffiliateCommissionPaidProjectionConsumer.ValidatePaidUpstream(order, fact, fact.EventId.ToString())).Message);
}
