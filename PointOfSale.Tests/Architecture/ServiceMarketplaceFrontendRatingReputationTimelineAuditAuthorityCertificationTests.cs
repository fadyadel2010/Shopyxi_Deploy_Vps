namespace PointOfSale.Tests.Architecture;

public sealed class ServiceMarketplaceFrontendRatingReputationTimelineAuditAuthorityCertificationTests
{
    [Fact]
    public void RuntimeReviewHistory_MustRemainBackendDerivedDeterministicAndFailClosed()
    {
        var controller = CertificationSource.ReadRepoFile(
            "PointOfSale.API/Controllers/MarketplaceController.cs");
        var reviewService = CertificationSource.ReadRepoFile(
            "PointOfSale.Infrastructure/Services/ServiceReviewService.cs");
        var authority = CertificationSource.ReadRepoFile(
            "PointOfSale.Web/src/marketplace/ratingTimelineAuthority.ts");
        var page = CertificationSource.ReadRepoFile(
            "PointOfSale.Web/src/marketplace/pages/ServiceDetailsPage.tsx");

        Assert.Contains(".ThenByDescending(r => r.Id)", reviewService, StringComparison.Ordinal);
        Assert.Contains("serviceRequestId = r.ServiceRequestId", controller, StringComparison.Ordinal);
        Assert.Contains("reviewerUserId = r.ReviewerUserId", controller, StringComparison.Ordinal);
        Assert.Contains("providerUserId = r.ProviderUserId", controller, StringComparison.Ordinal);
        Assert.Contains("updatedAt = r.UpdatedAt ?? r.CreatedAt", controller, StringComparison.Ordinal);
        Assert.Contains("totalReviews = items.Count", controller, StringComparison.Ordinal);
        Assert.DoesNotContain("\"Anonymous\"", controller[
            controller.IndexOf("GetServiceReviews", StringComparison.Ordinal)..controller.IndexOf(
                "Search marketplace services",
                StringComparison.Ordinal)]);

        Assert.Contains("deriveServiceReviewHistoryPresentation", authority, StringComparison.Ordinal);
        Assert.Contains("history.items.length !== history.totalReviews", authority, StringComparison.Ordinal);
        Assert.Contains("reviewIds.has(reviewId)", authority, StringComparison.Ordinal);
        Assert.Contains("requestIds.has(requestId)", authority, StringComparison.Ordinal);
        Assert.Contains("rowServiceId !== serviceId", authority, StringComparison.Ordinal);
        Assert.Contains("rowProviderUserId !== providerUserId", authority, StringComparison.Ordinal);
        Assert.Contains("review.updatedAt !== review.createdAt", authority, StringComparison.Ordinal);
        Assert.Contains("currentTime > previousTime", authority, StringComparison.Ordinal);
        Assert.DoesNotContain(".sort(", authority, StringComparison.Ordinal);

        // Unknown/refresh-failed service history is not a zero review count, and
        // a provider-wide trust aggregate is not this service's history count.
        Assert.Contains("serviceReviewCount = reviewsAvailable ? reviewHistory.totalReviews : null", page, StringComparison.Ordinal);
        Assert.Contains("serviceReviewCount === null", page, StringComparison.Ordinal);
        Assert.Contains("reviewsUnavailableTitle", page, StringComparison.Ordinal);
        Assert.Contains("count: formatNumber(serviceReviewCount)", page, StringComparison.Ordinal);
        Assert.DoesNotContain("serviceReviewCount = reviewHistory?.totalReviews ?? 0", page, StringComparison.Ordinal);
        Assert.Contains("service.trust.totalReviews", page, StringComparison.Ordinal);
        Assert.Contains("reviewsAvailable", page, StringComparison.Ordinal);
    }

    [Fact]
    public void RuntimeCacheRealtimeHistoryAndFailures_MustRevalidateOrWithholdOnly()
    {
        var authority = CertificationSource.ReadRepoFile(
            "PointOfSale.Web/src/marketplace/authority.ts");
        var boundary = CertificationSource.ReadRepoFile(
            "PointOfSale.Web/src/marketplace/components/MarketplaceAuthorityBoundary.tsx");
        var page = CertificationSource.ReadRepoFile(
            "PointOfSale.Web/src/marketplace/pages/ServiceDetailsPage.tsx");
        var reviewHook = CertificationSource.ReadRepoFile(
            "PointOfSale.Web/src/marketplace/hooks/useCreateReview.ts");

        Assert.Contains("staleTime: 0", authority, StringComparison.Ordinal);
        Assert.Contains("refetchOnMount: 'always'", authority, StringComparison.Ordinal);
        Assert.Contains("refetchOnReconnect: 'always'", authority, StringComparison.Ordinal);
        Assert.Contains("refetchOnWindowFocus: 'always'", authority, StringComparison.Ordinal);
        Assert.Contains("queryClient.removeQueries", boundary, StringComparison.Ordinal);
        Assert.Contains("invalidateQueries", boundary, StringComparison.Ordinal);
        Assert.Contains("reviewsAuthorityRefreshing", page, StringComparison.Ordinal);
        Assert.Contains("reviewsAuthorityRefreshFailed", page, StringComparison.Ordinal);
        Assert.DoesNotContain("setQueryData", reviewHook, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CertificationDocumentation_MustRecordPassFs5E()
    {
        // Actual immutable backend chronology/attribution/count lineage and replay
        // negatives,plus rendered current-history withholding instead of cached totals.
        await MarketplaceFrontendBehaviorContract.AssertAsync("src/marketplace/ratingTimelineAuthority.test.ts", 4);
        await MarketplaceFrontendBehaviorContract.AssertAsync("src/marketplace/pages/ServiceDetailsPage.test.tsx", 3);
    }
}
