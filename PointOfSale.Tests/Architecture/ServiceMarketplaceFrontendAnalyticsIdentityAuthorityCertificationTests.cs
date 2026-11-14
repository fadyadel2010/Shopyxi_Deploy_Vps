namespace PointOfSale.Tests.Architecture;

public sealed class ServiceMarketplaceFrontendAnalyticsIdentityAuthorityCertificationTests
{
    [Fact]
    public void RuntimeAnalyticsIdentity_MustBindToCurrentBackendActorProviderAndMetrics()
    {
        var controller = CertificationSource.ReadRepoFile(
            "PointOfSale.API/Controllers/MarketplaceController.cs");
        var authority = CertificationSource.ReadRepoFile(
            "PointOfSale.Web/src/marketplace/analyticsIdentityAuthority.ts");
        var profileTab = CertificationSource.ReadRepoFile(
            "PointOfSale.Web/src/marketplace/pages/tabs/ProfileTab.tsx");
        var listingAuthority = CertificationSource.ReadRepoFile(
            "PointOfSale.Web/src/marketplace/listingCardAuthority.ts");
        var queryKeys = CertificationSource.ReadRepoFile(
            "PointOfSale.Web/src/marketplace/queryKeys.ts");

        Assert.Contains("profile.TenantId != tenantId", controller, StringComparison.Ordinal);
        Assert.Contains("MARKETPLACE_PROFILE_NOT_FOUND", controller, StringComparison.Ordinal);
        Assert.Contains("MARKETPLACE_PROVIDER_NOT_VISIBLE", controller, StringComparison.Ordinal);
        Assert.Contains("tenant.Status == TenantStatus.Active", controller, StringComparison.Ordinal);
        Assert.Contains("governance.Status == UserGovernanceStatus.Suspended", controller, StringComparison.Ordinal);
        Assert.Contains("governance.Status == UserGovernanceStatus.Banned", controller, StringComparison.Ordinal);
        Assert.Contains("providerUserId = userId", controller, StringComparison.Ordinal);
        Assert.Contains("providerProfileId = profile.Id", controller, StringComparison.Ordinal);
        Assert.Contains("r.Status == Core.Enums.ServiceRequestStatus.CompletedByBuyer", controller, StringComparison.Ordinal);

        Assert.Contains("expectedScope !== `${tenantId}:${providerUserId}`", authority, StringComparison.Ordinal);
        Assert.Contains("providerProfileId !== expectedProfileId", authority, StringComparison.Ordinal);
        Assert.Contains("deriveProviderReputationStatsPresentation(stats)", authority, StringComparison.Ordinal);
        Assert.Contains("Math.abs(completionRate - expectedRate) > 1e-12", authority, StringComparison.Ordinal);
        Assert.Contains("deriveServiceAnalyticsPresentation(service)", listingAuthority, StringComparison.Ordinal);

        MarketplaceFrontendAnalyticsCompositionContract.AssertScopedStatsQuery();
        Assert.Contains("profileAuthorityRefreshing", profileTab, StringComparison.Ordinal);
        Assert.Contains("profileAuthorityRefreshFailed", profileTab, StringComparison.Ordinal);
        MarketplaceFrontendAnalyticsCompositionContract.AssertDashboardDelegatesIdentityAndMetrics();
        Assert.Contains("'my-profile-stats'", queryKeys, StringComparison.Ordinal);
    }

    [Fact]
    public void RuntimeAnalyticsCacheRealtimeHistoryAndFailures_MustRemainDerivative()
    {
        var authority = CertificationSource.ReadRepoFile(
            "PointOfSale.Web/src/marketplace/authority.ts");
        var boundary = CertificationSource.ReadRepoFile(
            "PointOfSale.Web/src/marketplace/components/MarketplaceAuthorityBoundary.tsx");
        var profileTab = CertificationSource.ReadRepoFile(
            "PointOfSale.Web/src/marketplace/pages/tabs/ProfileTab.tsx");
        var serviceCard = CertificationSource.ReadRepoFile(
            "PointOfSale.Web/src/marketplace/components/ServiceCard.tsx");

        Assert.Contains("staleTime: 0", authority, StringComparison.Ordinal);
        Assert.Contains("retry: false", authority, StringComparison.Ordinal);
        Assert.Contains("refetchOnReconnect: 'always'", authority, StringComparison.Ordinal);
        Assert.Contains("queryClient.removeQueries", boundary, StringComparison.Ordinal);
        Assert.Contains("invalidateQueries", boundary, StringComparison.Ordinal);
        Assert.DoesNotContain("setQueryData", boundary, StringComparison.Ordinal);
        Assert.DoesNotContain("setQueryData", profileTab, StringComparison.Ordinal);
        Assert.DoesNotContain("state={{", serviceCard, StringComparison.Ordinal);
    }

    [Fact]
    public Task CertificationDocumentation_MustRecordPassFs6A() =>
        // Execute the actual frontend function with actor/profile/metric replay
        // negatives and valid controls; retained method is not a historical PASS claim.
        MarketplaceFrontendBehaviorContract.AssertAsync("src/marketplace/analyticsIdentityAuthority.test.ts", 3);
}
