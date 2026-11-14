namespace PointOfSale.Tests.Architecture;

public sealed class ServiceMarketplaceFrontendAnalyticsDashboardTrendAuthorityCertificationTests
{
    [Fact]
    public void RuntimeDashboardTrendHistoryAndCharts_MustRemainBackendDerivedAndFailClosed()
    {
        var dashboardAuthority = CertificationSource.ReadRepoFile(
            "PointOfSale.Web/src/marketplace/dashboardTrendAuthority.ts");
        var analyticsAuthority = CertificationSource.ReadRepoFile(
            "PointOfSale.Web/src/marketplace/analyticsIdentityAuthority.ts");
        var profileTab = CertificationSource.ReadRepoFile(
            "PointOfSale.Web/src/marketplace/pages/tabs/ProfileTab.tsx");

        Assert.Contains("deriveProviderAnalyticsPresentation(stats, expected)", dashboardAuthority, StringComparison.Ordinal);
        Assert.Contains("unsupportedDashboardTruthKeys", dashboardAuthority, StringComparison.Ordinal);
        Assert.Contains("trendMetrics", dashboardAuthority, StringComparison.Ordinal);
        Assert.Contains("historicalMetrics", dashboardAuthority, StringComparison.Ordinal);
        Assert.Contains("growthMetrics", dashboardAuthority, StringComparison.Ordinal);
        Assert.Contains("comparisonMetrics", dashboardAuthority, StringComparison.Ordinal);
        Assert.Contains("performanceSummary", dashboardAuthority, StringComparison.Ordinal);
        Assert.Contains("charts: readonly never[]", dashboardAuthority, StringComparison.Ordinal);
        Assert.Contains("trends: []", dashboardAuthority, StringComparison.Ordinal);
        Assert.Contains("historicalMetrics: []", dashboardAuthority, StringComparison.Ordinal);
        Assert.Contains("charts: []", dashboardAuthority, StringComparison.Ordinal);
        Assert.Contains("completedRequests > totalRequests", analyticsAuthority, StringComparison.Ordinal);
        Assert.Contains("deriveProviderAnalyticsDashboardPresentation(statsData", profileTab, StringComparison.Ordinal);
        Assert.Contains("const stats = dashboard?.cards ?? null", profileTab, StringComparison.Ordinal);
    }

    [Fact]
    public void RuntimeCacheRealtimeHistoryVisibilityAndFailurePaths_MustRevalidateOrWithholdOnly()
    {
        var authority = CertificationSource.ReadRepoFile(
            "PointOfSale.Web/src/marketplace/authority.ts");
        var boundary = CertificationSource.ReadRepoFile(
            "PointOfSale.Web/src/marketplace/components/MarketplaceAuthorityBoundary.tsx");
        var queryKeys = CertificationSource.ReadRepoFile(
            "PointOfSale.Web/src/marketplace/queryKeys.ts");
        var profileTab = CertificationSource.ReadRepoFile(
            "PointOfSale.Web/src/marketplace/pages/tabs/ProfileTab.tsx");
        var dashboardAuthority = CertificationSource.ReadRepoFile(
            "PointOfSale.Web/src/marketplace/dashboardTrendAuthority.ts");

        Assert.Contains("staleTime: 0", authority, StringComparison.Ordinal);
        Assert.Contains("retry: false", authority, StringComparison.Ordinal);
        Assert.Contains("refetchOnReconnect: 'always'", authority, StringComparison.Ordinal);
        Assert.Contains("refetchOnWindowFocus: 'always'", authority, StringComparison.Ordinal);
        Assert.Contains("queryClient.removeQueries", boundary, StringComparison.Ordinal);
        Assert.Contains("invalidateQueries", boundary, StringComparison.Ordinal);
        Assert.DoesNotContain("setQueryData", boundary, StringComparison.Ordinal);
        Assert.DoesNotContain("setQueryData", dashboardAuthority, StringComparison.Ordinal);
        Assert.Contains("'my-profile-stats'", queryKeys, StringComparison.Ordinal);
        MarketplaceFrontendAnalyticsCompositionContract.AssertScopedStatsQuery();
        Assert.Contains("!statsAuthorityRefreshing", profileTab, StringComparison.Ordinal);
        Assert.Contains("!statsAuthorityRefreshFailed", profileTab, StringComparison.Ordinal);
        Assert.Contains("statsAuthorityFailed", profileTab, StringComparison.Ordinal);
    }

    [Fact]
    public Task CertificationDocumentation_MustRecordPassFs6D() =>
        // Actual presentation rejects invented trends/charts/history and mismatched
        // identities. No backend substitute for this frontend behavior.
        MarketplaceFrontendBehaviorContract.AssertAsync("src/marketplace/dashboardTrendAuthority.test.ts", 3);
}
