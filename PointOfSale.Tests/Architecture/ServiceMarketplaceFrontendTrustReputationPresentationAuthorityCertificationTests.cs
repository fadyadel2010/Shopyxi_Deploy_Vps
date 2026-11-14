namespace PointOfSale.Tests.Architecture;

public sealed class ServiceMarketplaceFrontendTrustReputationPresentationAuthorityCertificationTests
{
    [Fact]
    public void RuntimeTrustPresentation_MustRemainBackendDerived()
    {
        var detailAuthority = ReadRepoFile(
            "PointOfSale.Web/src/marketplace/serviceDetailAuthority.ts");
        var listingAuthority = ReadRepoFile(
            "PointOfSale.Web/src/marketplace/listingCardAuthority.ts");
        var reputationAuthority = ReadRepoFile(
            "PointOfSale.Web/src/marketplace/reputationPresentationAuthority.ts");
        var detailPage = ReadRepoFile(
            "PointOfSale.Web/src/marketplace/pages/ServiceDetailsPage.tsx");

        Assert.Contains("deriveReputationDistributionPresentation(trust)", detailAuthority, StringComparison.Ordinal);
        Assert.Contains("trust: parseTrust(service.trust)", detailAuthority, StringComparison.Ordinal);
        Assert.Contains("deriveServiceReviewPresentation", detailAuthority, StringComparison.Ordinal);
        Assert.Contains("evidenceTypes.has(evidenceType)", detailAuthority, StringComparison.Ordinal);
        Assert.DoesNotContain("revenueAmount:", detailAuthority, StringComparison.Ordinal);

        Assert.Contains("deriveReputationSummaryPresentation(service)", listingAuthority, StringComparison.Ordinal);
        Assert.DoesNotContain("Math.round(service.averageRating", listingAuthority, StringComparison.Ordinal);

        Assert.Contains("isBadgeConsistent", reputationAuthority, StringComparison.Ordinal);
        Assert.Contains("totalRatings !== reputation.totalReviews", reputationAuthority, StringComparison.Ordinal);
        Assert.Contains("distributionAverage !== reputation.averageRating", reputationAuthority, StringComparison.Ordinal);

        Assert.Contains("formatNumber(service.trust.totalReviews)", detailPage, StringComparison.Ordinal);
        Assert.Contains("serviceReviewCount = reviewsAvailable ? reviewHistory.totalReviews : null", detailPage, StringComparison.Ordinal);
        Assert.Contains("count: formatNumber(serviceReviewCount)", detailPage, StringComparison.Ordinal);
        Assert.Contains("reviewsUnavailableTitle", detailPage, StringComparison.Ordinal);
        Assert.DoesNotContain("const authoritativeReviewCount = service.trust?.totalReviews ?? 0", detailPage, StringComparison.Ordinal);
        Assert.DoesNotContain("formatNumber(reviewsAvailable ? reviews.length", detailPage, StringComparison.Ordinal);
    }

    [Fact]
    public void RuntimeTrustCachesAndRealtime_MustInvalidateNotPublishAuthority()
    {
        var serviceHook = ReadRepoFile(
            "PointOfSale.Web/src/marketplace/hooks/useServiceDetails.ts");
        var searchHook = ReadRepoFile(
            "PointOfSale.Web/src/marketplace/hooks/useMarketplaceSearch.ts");
        var boundary = ReadRepoFile(
            "PointOfSale.Web/src/marketplace/components/MarketplaceAuthorityBoundary.tsx");
        var queryKeys = ReadRepoFile(
            "PointOfSale.Web/src/marketplace/queryKeys.ts");

        Assert.Contains("marketplaceKeys.service(serviceId, authorityScope)", serviceHook, StringComparison.Ordinal);
        Assert.Contains("marketplaceAuthorityQueryOptions", serviceHook, StringComparison.Ordinal);
        Assert.Contains("marketplaceKeys.search", searchHook, StringComparison.Ordinal);
        Assert.Contains("marketplaceAuthorityQueryOptions", searchHook, StringComparison.Ordinal);
        Assert.Contains("queryClient.removeQueries", boundary, StringComparison.Ordinal);
        Assert.Contains("invalidateQueries", boundary, StringComparison.Ordinal);
        Assert.Contains("scope: (scope = getMarketplaceAuthorityScope())", queryKeys, StringComparison.Ordinal);
        Assert.Contains("service: (serviceId: string, scope = getMarketplaceAuthorityScope())", queryKeys, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CertificationDocumentation_MustRecordPassFs1D()
    {
        // Actual coherent summaries/distributions and badge/count/average negatives,
        // plus detail evidence/identity validation without inventing revenue currency.
        await MarketplaceFrontendBehaviorContract.AssertAsync("src/marketplace/reputationPresentationAuthority.test.ts", 4);
        await MarketplaceFrontendBehaviorContract.AssertAsync("src/marketplace/serviceDetailAuthority.test.ts", 8);
    }

    private static string ReadRepoFile(string relativePath) =>
        CertificationSource.ReadRepoFile(relativePath);
}
