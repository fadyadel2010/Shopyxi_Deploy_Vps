namespace PointOfSale.Tests.Architecture;

public sealed class ServiceMarketplaceFrontendKpiMetricsPresentationAuthorityCertificationTests
{
    [Fact]
    public void RuntimeKpiMetricsPresentation_MustRemainBackendDerivedAndCrossMetricConsistent()
    {
        var controller = CertificationSource.ReadRepoFile(
            "PointOfSale.API/Controllers/MarketplaceController.cs");
        var analyticsAuthority = CertificationSource.ReadRepoFile(
            "PointOfSale.Web/src/marketplace/analyticsIdentityAuthority.ts");
        var reputationAuthority = CertificationSource.ReadRepoFile(
            "PointOfSale.Web/src/marketplace/reputationPresentationAuthority.ts");
        var detailPage = CertificationSource.ReadRepoFile(
            "PointOfSale.Web/src/marketplace/pages/ServiceDetailsPage.tsx");
        var profileTab = CertificationSource.ReadRepoFile(
            "PointOfSale.Web/src/marketplace/pages/tabs/ProfileTab.tsx");
        var serviceCard = CertificationSource.ReadRepoFile(
            "PointOfSale.Web/src/marketplace/components/ServiceCard.tsx");

        Assert.Contains("BuildRatingPercentages", controller, StringComparison.Ordinal);
        Assert.Contains("MidpointRounding.AwayFromZero", controller, StringComparison.Ordinal);
        Assert.Contains("ratingPercentages", controller, StringComparison.Ordinal);

        Assert.Contains("completedRequests > totalRequests", analyticsAuthority, StringComparison.Ordinal);
        Assert.Contains("Math.abs(completionRate - expectedRate) > 1e-12", analyticsAuthority, StringComparison.Ordinal);
        Assert.Contains("totalRatings !== reputation.totalReviews", reputationAuthority, StringComparison.Ordinal);
        Assert.Contains("distributionAverage !== reputation.averageRating", reputationAuthority, StringComparison.Ordinal);
        Assert.Contains("Math.abs(percentage - expectedPercentage) > 1e-9", reputationAuthority, StringComparison.Ordinal);

        Assert.Contains("style={{ width: `${percentage}%` }}", detailPage, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "(count / service.trust.totalReviews) * 100",
            detailPage,
            StringComparison.Ordinal);
        Assert.Contains("serviceReviewCount === null", detailPage, StringComparison.Ordinal);
        MarketplaceFrontendAnalyticsCompositionContract.AssertDashboardDelegatesIdentityAndMetrics();
        Assert.Contains("deriveListingCardPresentation(service)", serviceCard, StringComparison.Ordinal);
    }

    [Fact]
    public void RuntimeKpiCachesRealtimeHistoryVisibilityAndFailures_MustRemainDerivative()
    {
        var authority = CertificationSource.ReadRepoFile(
            "PointOfSale.Web/src/marketplace/authority.ts");
        var boundary = CertificationSource.ReadRepoFile(
            "PointOfSale.Web/src/marketplace/components/MarketplaceAuthorityBoundary.tsx");
        var profileTab = CertificationSource.ReadRepoFile(
            "PointOfSale.Web/src/marketplace/pages/tabs/ProfileTab.tsx");
        var discoverTab = CertificationSource.ReadRepoFile(
            "PointOfSale.Web/src/marketplace/pages/tabs/DiscoverTab.tsx");

        Assert.Contains("staleTime: 0", authority, StringComparison.Ordinal);
        Assert.Contains("retry: false", authority, StringComparison.Ordinal);
        Assert.Contains("refetchOnReconnect: 'always'", authority, StringComparison.Ordinal);
        Assert.Contains("refetchOnWindowFocus: 'always'", authority, StringComparison.Ordinal);
        Assert.Contains("queryClient.removeQueries", boundary, StringComparison.Ordinal);
        Assert.Contains("invalidateQueries", boundary, StringComparison.Ordinal);
        Assert.DoesNotContain("setQueryData", boundary, StringComparison.Ordinal);
        Assert.Contains("!statsAuthorityRefreshing", profileTab, StringComparison.Ordinal);
        Assert.Contains("!statsAuthorityRefreshFailed", profileTab, StringComparison.Ordinal);
        Assert.Contains("authorityFailed || isValidating ? undefined : searchData", discoverTab, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CertificationDocumentation_MustRecordPassFs6B()
    {
        // Execute exact count/rate arithmetic and rating-distribution consistency,
        // including malformed metrics and accepted backend values, in real TypeScript.
        await MarketplaceFrontendBehaviorContract.AssertAsync("src/marketplace/analyticsIdentityAuthority.test.ts", 3);
        await MarketplaceFrontendBehaviorContract.AssertAsync("src/marketplace/reputationPresentationAuthority.test.ts", 4);
    }
}
