namespace PointOfSale.Tests.Marketplace;

public sealed class MarketplaceGenerationFrontendContractTests
{
    [Fact]
    public Task ActualSearchHookPreservesGenerationAndAuthorityAcrossPages() =>
        PointOfSale.Tests.Architecture.MarketplaceFrontendBehaviorContract.AssertAsync(
            "src/marketplace/hooks/useMarketplaceSearch.test.tsx", 9);
}
