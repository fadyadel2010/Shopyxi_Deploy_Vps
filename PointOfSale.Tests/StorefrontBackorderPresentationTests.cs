using PointOfSale.API.DTOs.Storefront;

namespace PointOfSale.Tests;

public class StorefrontBackorderPresentationTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    public void BackorderPolicy_IsNotAStockCap(int quantity)
    {
        var result=StorefrontAvailabilityPresentationMapper.Build(true,true,quantity,canPurchaseOverride:true,trackStock:true,allowBackorder:true);
        Assert.True(result.CanAddToCart);
        Assert.Null(result.MaximumQuantity);
        Assert.Equal(quantity==0 ? "Available on backorder" : "In Stock (3)", result.StockLabel);
    }

    [Fact]
    public void UntrackedStock_IsAvailableWithoutFabricatedPhysicalStock()
    {
        var result=StorefrontAvailabilityPresentationMapper.Build(true,true,0,canPurchaseOverride:true,trackStock:false);
        Assert.Null(result.MaximumQuantity);
        Assert.Equal("Available",result.StockLabel);
    }

    [Fact]
    public void TrackedStockAndUnavailableAuthority_ArePreserved()
    {
        Assert.Equal(3,StorefrontAvailabilityPresentationMapper.Build(true,true,3).MaximumQuantity);
        var unavailable=StorefrontAvailabilityPresentationMapper.Build(false,false,0,canPurchaseOverride:false,allowBackorder:true);
        Assert.False(unavailable.CanAddToCart);
        Assert.Equal("Unavailable",unavailable.StockLabel);
    }
}
