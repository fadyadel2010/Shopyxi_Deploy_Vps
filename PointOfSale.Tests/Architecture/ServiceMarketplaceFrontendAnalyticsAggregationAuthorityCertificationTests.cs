namespace PointOfSale.Tests.Architecture;

public sealed class ServiceMarketplaceFrontendAnalyticsAggregationAuthorityCertificationTests
{
    [Fact]
    public void RuntimeAggregatesRollupsAndSummaries_MustRemainBackendDerivedAndConsistent()
    {
        var analyticsAuthority = CertificationSource.ReadRepoFile(
            "PointOfSale.Web/src/marketplace/analyticsIdentityAuthority.ts");
        var reputationAuthority = CertificationSource.ReadRepoFile(
            "PointOfSale.Web/src/marketplace/reputationPresentationAuthority.ts");
        var listingAuthority = CertificationSource.ReadRepoFile(
            "PointOfSale.Web/src/marketplace/listingCardAuthority.ts");
        var profileTab = CertificationSource.ReadRepoFile(
            "PointOfSale.Web/src/marketplace/pages/tabs/ProfileTab.tsx");

        Assert.Contains("deriveProviderReputationStatsPresentation(stats)", analyticsAuthority, StringComparison.Ordinal);
        Assert.Contains("reputation.totalReviews > completedRequests", reputationAuthority, StringComparison.Ordinal);
        Assert.Contains("reputation.totalReviews <= analytics.completedRequests", listingAuthority, StringComparison.Ordinal);
        Assert.Contains("acceptedReputation", listingAuthority, StringComparison.Ordinal);
        Assert.Contains("acceptedAnalytics", listingAuthority, StringComparison.Ordinal);
        MarketplaceFrontendAnalyticsCompositionContract.AssertDashboardDelegatesIdentityAndMetrics();

        Assert.DoesNotContain("setQueryData", analyticsAuthority, StringComparison.Ordinal);
        Assert.DoesNotContain("setQueryData", listingAuthority, StringComparison.Ordinal);
        Assert.DoesNotContain("reviews.length", profileTab, StringComparison.Ordinal);
    }

    [Fact]
    public void RuntimeCacheRealtimeActorHistoryAndFailurePaths_MustRevalidateOrWithholdOnly()
    {
        var authority = CertificationSource.ReadRepoFile(
            "PointOfSale.Web/src/marketplace/authority.ts");
        var boundary = CertificationSource.ReadRepoFile(
            "PointOfSale.Web/src/marketplace/components/MarketplaceAuthorityBoundary.tsx");
        var queryKeys = CertificationSource.ReadRepoFile(
            "PointOfSale.Web/src/marketplace/queryKeys.ts");
        var profileTab = CertificationSource.ReadRepoFile(
            "PointOfSale.Web/src/marketplace/pages/tabs/ProfileTab.tsx");

        Assert.Contains("staleTime: 0", authority, StringComparison.Ordinal);
        Assert.Contains("retry: false", authority, StringComparison.Ordinal);
        Assert.Contains("refetchOnReconnect: 'always'", authority, StringComparison.Ordinal);
        Assert.Contains("refetchOnWindowFocus: 'always'", authority, StringComparison.Ordinal);
        Assert.Contains("queryClient.removeQueries", boundary, StringComparison.Ordinal);
        Assert.Contains("invalidateQueries", boundary, StringComparison.Ordinal);
        Assert.DoesNotContain("setQueryData", boundary, StringComparison.Ordinal);
        MarketplaceFrontendAnalyticsCompositionContract.AssertScopedStatsQuery();
        Assert.Contains("!statsAuthorityRefreshing", profileTab, StringComparison.Ordinal);
        Assert.Contains("!statsAuthorityRefreshFailed", profileTab, StringComparison.Ordinal);
        Assert.Contains("'my-profile-stats'", queryKeys, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CertificationDocumentation_MustRecordPassFs6C()
    {
        // Actual card composition rejects contradictory aggregates as a bundle;
        // provider rollups reject reviews exceeding final completions. No invented history.
        await MarketplaceFrontendBehaviorContract.AssertAsync("src/marketplace/listingCardAuthority.test.ts", 6);
        await MarketplaceFrontendBehaviorContract.AssertAsync("src/marketplace/reputationPresentationAuthority.test.ts", 4);
    }
}
