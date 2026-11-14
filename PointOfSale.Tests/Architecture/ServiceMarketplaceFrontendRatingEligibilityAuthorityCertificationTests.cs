namespace PointOfSale.Tests.Architecture;

public sealed class ServiceMarketplaceFrontendRatingEligibilityAuthorityCertificationTests
{
    [Fact]
    public void RatingEligibility_MustBindFinalCompletionBuyerAndExistingReviewAuthority()
    {
        var controller = ReadRepoFile("PointOfSale.API/Controllers/MarketplaceController.cs");
        var eligibility = ReadRepoFile("PointOfSale.Web/src/marketplace/ratingEligibility.ts");
        var myRequests = ReadRepoFile("PointOfSale.Web/src/marketplace/pages/tabs/MyRequestsTab.tsx");
        var details = ReadRepoFile("PointOfSale.Web/src/marketplace/viewmodels/useRequestDetailsVM.ts");

        Assert.Contains("existingReviewId = existingReviews.GetValueOrDefault(r.Id)", controller, StringComparison.Ordinal);
        Assert.Contains("canReview = isBuyer && request.CanBeReviewed() && !existingReviewId.HasValue", controller, StringComparison.Ordinal);
        Assert.Contains("deriveServiceRequestRatingEligibility", eligibility, StringComparison.Ordinal);
        MarketplaceRequestCommandCompositionContract.AssertRatingStateDelegation();
        Assert.Contains("ratingEligibility?.canRate", myRequests, StringComparison.Ordinal);
        Assert.Contains("deriveServiceRequestRatingEligibility", details, StringComparison.Ordinal);
        Assert.DoesNotContain("{lifecycle.canReview ? (", myRequests, StringComparison.Ordinal);
    }

    [Fact]
    public void RatingWorkflow_MustUsePosEndpointAndFreshScopedBackendRevalidation()
    {
        var controller = ReadRepoFile("PointOfSale.API/Controllers/MarketplaceController.cs");
        var api = ReadRepoFile("PointOfSale.Web/src/marketplace/api/marketplaceApi.ts");
        var workflow = ReadRepoFile("PointOfSale.Web/src/marketplace/ratingWorkflow.ts");
        var myRequests = ReadRepoFile("PointOfSale.Web/src/marketplace/pages/tabs/MyRequestsTab.tsx");
        var hook = ReadRepoFile("PointOfSale.Web/src/marketplace/hooks/useCreateReview.ts");

        Assert.Contains("[HttpPost(\"service-reviews\")]", controller, StringComparison.Ordinal);
        Assert.Contains("_reviewService.CreateReviewAsync", controller, StringComparison.Ordinal);
        Assert.Contains("apiClient.post('/marketplace/service-reviews'", api, StringComparison.Ordinal);
        Assert.DoesNotContain("apiClient.post('/marketplace/reviews'", api, StringComparison.Ordinal);
        Assert.Contains("executeServiceRequestRatingWorkflow", workflow, StringComparison.Ordinal);
        Assert.Contains("const preflightRequest = await revalidate()", workflow, StringComparison.Ordinal);
        Assert.Contains("const settledRequest = await revalidate()", workflow, StringComparison.Ordinal);
        Assert.Contains("authorityScope !== getCurrentAuthorityScope()", workflow, StringComparison.Ordinal);
        Assert.Contains("ratingExecutions", workflow, StringComparison.Ordinal);

        var buyerCommands = MarketplaceRequestCommandCompositionContract.BuyerCommands();
        foreach (var source in new[] { buyerCommands, hook })
        {
            Assert.Contains("executeServiceRequestRatingWorkflow", source, StringComparison.Ordinal);
            Assert.Contains("retry: false", source, StringComparison.Ordinal);
            Assert.Contains("networkMode: 'online'", source, StringComparison.Ordinal);
            Assert.Contains("publishMarketplaceAuthorityInvalidation(authorityScope)", source, StringComparison.Ordinal);
            Assert.DoesNotContain("setQueryData", source, StringComparison.Ordinal);
            Assert.DoesNotContain("onMutate", source, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void RatingAuthority_MustRemainScopedAndWithheldDuringRefresh()
    {
        var myRequests = ReadRepoFile("PointOfSale.Web/src/marketplace/pages/tabs/MyRequestsTab.tsx");
        var requestDetails = ReadRepoFile("PointOfSale.Web/src/marketplace/hooks/useRequestDetails.ts");
        var boundary = ReadRepoFile("PointOfSale.Web/src/marketplace/components/MarketplaceAuthorityBoundary.tsx");
        var realtime = ReadRepoFile("PointOfSale.Web/src/marketplace/hooks/useChat.ts");

        Assert.Contains("if (isLoading || isFetching)", myRequests, StringComparison.Ordinal);
        Assert.Contains("refetchInterval: 15_000", requestDetails, StringComparison.Ordinal);
        Assert.Contains("queryClient.removeQueries", boundary, StringComparison.Ordinal);
        Assert.Contains("AUTHORITY_REVALIDATION_INTERVAL_MS", boundary, StringComparison.Ordinal);
        Assert.Contains("marketplaceKeys.scope(authorityScope)", realtime, StringComparison.Ordinal);
        Assert.DoesNotContain("setQueryData", realtime, StringComparison.Ordinal);
    }

    [Fact]
    public Task CertificationDocumentation_MustRecordPassFs5A() =>
        // Actual current-buyer/final-completion/review-identity positives plus
        // provider,foreign,self,malformed,contradictory and five wrong-state negatives.
        MarketplaceFrontendBehaviorContract.AssertAsync("src/marketplace/ratingEligibility.test.ts", 9);

    private static string ReadRepoFile(string relativePath) => CertificationSource.ReadRepoFile(relativePath);
}
