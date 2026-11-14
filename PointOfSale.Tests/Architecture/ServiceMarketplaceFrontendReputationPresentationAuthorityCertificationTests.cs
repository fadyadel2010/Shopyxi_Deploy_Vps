namespace PointOfSale.Tests.Architecture;

public sealed class ServiceMarketplaceFrontendReputationPresentationAuthorityCertificationTests
{
    [Fact]
    public void RuntimeReputationPresentation_MustRemainBackendDerivedAndFailClosed()
    {
        var authority = CertificationSource.ReadRepoFile(
            "PointOfSale.Web/src/marketplace/reputationPresentationAuthority.ts");
        var analyticsAuthority = CertificationSource.ReadRepoFile(
            "PointOfSale.Web/src/marketplace/analyticsIdentityAuthority.ts");
        var listingAuthority = CertificationSource.ReadRepoFile(
            "PointOfSale.Web/src/marketplace/listingCardAuthority.ts");
        var detailAuthority = CertificationSource.ReadRepoFile(
            "PointOfSale.Web/src/marketplace/serviceDetailAuthority.ts");
        var profileTab = CertificationSource.ReadRepoFile(
            "PointOfSale.Web/src/marketplace/pages/tabs/ProfileTab.tsx");
        var serviceCard = CertificationSource.ReadRepoFile(
            "PointOfSale.Web/src/marketplace/components/ServiceCard.tsx");
        var controller = CertificationSource.ReadRepoFile(
            "PointOfSale.API/Controllers/MarketplaceController.cs");

        Assert.Contains("deriveReputationSummaryPresentation", authority, StringComparison.Ordinal);
        Assert.Contains("deriveReputationDistributionPresentation", authority, StringComparison.Ordinal);
        Assert.Contains("deriveProviderReputationStatsPresentation", authority, StringComparison.Ordinal);
        Assert.Contains("totalReviews === 0 && averageRating !== 0", authority, StringComparison.Ordinal);
        Assert.Contains("totalReviews > 0 && averageRating < 1", authority, StringComparison.Ordinal);
        Assert.Contains("isBadgeConsistent", authority, StringComparison.Ordinal);
        Assert.Contains("totalRatings !== reputation.totalReviews", authority, StringComparison.Ordinal);
        Assert.Contains("distributionAverage !== reputation.averageRating", authority, StringComparison.Ordinal);

        Assert.Contains("deriveReputationSummaryPresentation(service)", listingAuthority, StringComparison.Ordinal);
        Assert.Contains("deriveReputationDistributionPresentation(trust)", detailAuthority, StringComparison.Ordinal);
        MarketplaceFrontendAnalyticsCompositionContract.AssertDashboardDelegatesIdentityAndMetrics();
        Assert.Contains("deriveProviderReputationStatsPresentation(stats)", analyticsAuthority, StringComparison.Ordinal);
        Assert.Contains("!statsAuthorityRefreshing", profileTab, StringComparison.Ordinal);
        Assert.DoesNotContain("stats?.averageRating || 0", profileTab, StringComparison.Ordinal);
        Assert.Contains("Standard: 'servicesMarketplace.badges.standard'", serviceCard, StringComparison.Ordinal);

        Assert.Contains("totalReviews = trust?.TotalReviews ?? 0", controller, StringComparison.Ordinal);
        Assert.Contains("trustBadge = trust?.ComputeBadge().ToString() ?? TrustBadge.New.ToString()", controller, StringComparison.Ordinal);
    }

    [Fact]
    public void RuntimeReputationCachesRealtimeAndHistory_MustRevalidateOnly()
    {
        var authority = CertificationSource.ReadRepoFile(
            "PointOfSale.Web/src/marketplace/authority.ts");
        var boundary = CertificationSource.ReadRepoFile(
            "PointOfSale.Web/src/marketplace/components/MarketplaceAuthorityBoundary.tsx");
        var queryKeys = CertificationSource.ReadRepoFile(
            "PointOfSale.Web/src/marketplace/queryKeys.ts");
        var reviewHook = CertificationSource.ReadRepoFile(
            "PointOfSale.Web/src/marketplace/hooks/useCreateReview.ts");

        Assert.Contains("staleTime: 0", authority, StringComparison.Ordinal);
        Assert.Contains("refetchOnMount: 'always'", authority, StringComparison.Ordinal);
        Assert.Contains("refetchOnReconnect: 'always'", authority, StringComparison.Ordinal);
        Assert.Contains("refetchOnWindowFocus: 'always'", authority, StringComparison.Ordinal);
        Assert.Contains("queryClient.removeQueries", boundary, StringComparison.Ordinal);
        Assert.Contains("invalidateQueries", boundary, StringComparison.Ordinal);
        Assert.Contains("getMarketplaceAuthorityScope", queryKeys, StringComparison.Ordinal);
        Assert.Contains("invalidateQueries", reviewHook, StringComparison.Ordinal);
        Assert.DoesNotContain("setQueryData", reviewHook, StringComparison.Ordinal);
    }

    [Fact]
    public Task CertificationDocumentation_MustRecordPassFs5D() =>
        // Coherent backend summary/distribution controls plus impossible badge,
        // average,count,percentage and provider-stat negatives; not historical prose.
        MarketplaceFrontendBehaviorContract.AssertAsync("src/marketplace/reputationPresentationAuthority.test.ts", 4);
}
