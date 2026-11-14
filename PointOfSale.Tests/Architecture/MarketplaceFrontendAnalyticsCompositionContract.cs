namespace PointOfSale.Tests.Architecture;

// Source-level wiring guard only. Executable React/Query behavior is covered by
// ProfileTab.test.tsx (using the actual query hooks), not inferred here.
internal static class MarketplaceFrontendAnalyticsCompositionContract
{
    internal static void AssertScopedStatsQuery()
    {
        var hook = CertificationSource.ReadRepoFile("PointOfSale.Web/src/marketplace/hooks/useMarketplaceProfileEditor.ts");
        var profileTab = CertificationSource.ReadRepoFile("PointOfSale.Web/src/marketplace/pages/tabs/ProfileTab.tsx");
        Assert.Contains("export function useMarketplaceProfileStats(enabled: boolean)", hook, StringComparison.Ordinal);
        Assert.Contains("const authorityScope = useMarketplaceAuthorityScope()", hook, StringComparison.Ordinal);
        Assert.Contains("queryKey: marketplaceKeys.myProfileStats(authorityScope)", hook, StringComparison.Ordinal);
        Assert.Contains("queryFn: marketplaceApi.getMyProfileStats", hook, StringComparison.Ordinal);
        Assert.Contains("enabled, ...marketplaceAuthorityQueryOptions", hook, StringComparison.Ordinal);
        Assert.Contains("useMarketplaceProfileStats(!profileAuthorityFailed && profile?.hasMarketplaceProfile === true && profile.isActive === true)", profileTab, StringComparison.Ordinal);
        Assert.DoesNotContain("setQueryData", hook, StringComparison.Ordinal);
        Assert.DoesNotContain("setQueryData", profileTab, StringComparison.Ordinal);
    }

    internal static void AssertDashboardDelegatesIdentityAndMetrics()
    {
        var profileTab = CertificationSource.ReadRepoFile("PointOfSale.Web/src/marketplace/pages/tabs/ProfileTab.tsx");
        var dashboard = CertificationSource.ReadRepoFile("PointOfSale.Web/src/marketplace/dashboardTrendAuthority.ts");
        // The tab now uses the stricter dashboard wrapper, which delegates to the
        // identity/metric validator. Requiring the old direct call misses this edge.
        Assert.Contains("deriveProviderAnalyticsDashboardPresentation(statsData, {", profileTab, StringComparison.Ordinal);
        Assert.Contains("providerProfileId: profile?.profileId", profileTab, StringComparison.Ordinal);
        Assert.Contains("authorityScope,", profileTab, StringComparison.Ordinal);
        Assert.Contains("const stats = dashboard?.cards ?? null", profileTab, StringComparison.Ordinal);
        Assert.Contains("deriveProviderAnalyticsPresentation(stats, expected)", dashboard, StringComparison.Ordinal);
        Assert.Contains("if (!cards)", dashboard, StringComparison.Ordinal);
        Assert.Contains("return null", dashboard, StringComparison.Ordinal);
    }
}
