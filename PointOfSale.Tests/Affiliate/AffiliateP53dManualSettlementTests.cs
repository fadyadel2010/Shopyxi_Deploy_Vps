using PointOfSale.Core.Enums;
using PointOfSale.Infrastructure.Accounting;

namespace PointOfSale.Tests.Affiliate;

public class AffiliateP53dManualSettlementTests
{
    [Theory]
    [InlineData(FulfillmentStatus.Created, false)]
    [InlineData(FulfillmentStatus.Confirmed, false)]
    [InlineData(FulfillmentStatus.Packed, false)]
    [InlineData(FulfillmentStatus.OutForDelivery, false)]
    [InlineData(FulfillmentStatus.Delivered, true)]
    [InlineData(FulfillmentStatus.Completed, true)]
    [InlineData(FulfillmentStatus.Returned, false)]
    [InlineData(FulfillmentStatus.Cancelled, false)]
    [InlineData(FulfillmentStatus.FailedDelivery, false)]
    public void SettlementDoesNotLoseDeliveryAuthorityAfterCompletion(FulfillmentStatus status, bool eligible) =>
        Assert.Equal(eligible, ManualCodSettlementAuthority.HasDeliveredFulfillment(status));
}
