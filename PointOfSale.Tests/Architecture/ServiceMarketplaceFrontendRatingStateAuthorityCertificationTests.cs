namespace PointOfSale.Tests.Architecture;

public sealed class ServiceMarketplaceFrontendRatingStateAuthorityCertificationTests
{
    [Fact]
    public void RatingState_MustBeBuyerBoundFinalAndNonContradictory()
    {
        var state = ReadRepoFile("PointOfSale.Web/src/marketplace/ratingStateAuthority.ts");
        var eligibility = ReadRepoFile("PointOfSale.Web/src/marketplace/ratingEligibility.ts");

        Assert.Contains("lifecycle.statusKey !== 'completedByBuyer'", state, StringComparison.Ordinal);
        Assert.Contains("actorUserId !== buyerUserId", state, StringComparison.Ordinal);
        Assert.Contains("buyerUserId === providerUserId", state, StringComparison.Ordinal);
        Assert.Contains("typeof authority.canReview !== 'boolean'", state, StringComparison.Ordinal);
        Assert.Contains("state: 'unrated'", state, StringComparison.Ordinal);
        Assert.Contains("state: 'rated'", state, StringComparison.Ordinal);
        Assert.Contains("reviewId === null", state, StringComparison.Ordinal);
        Assert.Contains("deriveServiceRequestRatingStatePresentation", eligibility, StringComparison.Ordinal);
    }

    [Fact]
    public void RatingState_SurfacesMustConsumeOneSharedBoundary()
    {
        var card = ReadRepoFile("PointOfSale.Web/src/marketplace/components/RequestCard.tsx");
        var myRequests = ReadRepoFile("PointOfSale.Web/src/marketplace/pages/tabs/MyRequestsTab.tsx");
        var incoming = ReadRepoFile("PointOfSale.Web/src/marketplace/pages/tabs/IncomingTab.tsx");
        var chat = ReadRepoFile("PointOfSale.Web/src/marketplace/pages/ChatPage.tsx");
        var details = ReadRepoFile("PointOfSale.Web/src/marketplace/viewmodels/useRequestDetailsVM.ts");
        var badge = ReadRepoFile("PointOfSale.Web/src/marketplace/components/RatingStateBadge.tsx");

        foreach (var source in new[] { card, myRequests, incoming, chat, details })
        {
            Assert.Contains("deriveServiceRequestRatingStatePresentation", source, StringComparison.Ordinal);
        }

        Assert.Contains("RatingStateBadge", card, StringComparison.Ordinal);
        Assert.Contains("RatingStateBadge", myRequests, StringComparison.Ordinal);
        Assert.Contains("RatingStateBadge", chat, StringComparison.Ordinal);
        Assert.Contains("ratingEligibility?.canRate", myRequests, StringComparison.Ordinal);
        Assert.DoesNotContain("data-review-id", badge, StringComparison.Ordinal);
        Assert.DoesNotContain("navigate(ratingState.reviewId", myRequests, StringComparison.Ordinal);
    }

    [Fact]
    public void RatingState_CacheRealtimeActorAndHistoryMustRemainDerivative()
    {
        var authority = ReadRepoFile("PointOfSale.Web/src/marketplace/authority.ts");
        var boundary = ReadRepoFile("PointOfSale.Web/src/marketplace/components/MarketplaceAuthorityBoundary.tsx");
        var myRequests = ReadRepoFile("PointOfSale.Web/src/marketplace/pages/tabs/MyRequestsTab.tsx");
        var requestDetails = ReadRepoFile("PointOfSale.Web/src/marketplace/hooks/useRequestDetails.ts");
        var workflow = ReadRepoFile("PointOfSale.Web/src/marketplace/ratingWorkflow.ts");

        Assert.Contains("event.authorityScope === authorityScope", authority, StringComparison.Ordinal);
        Assert.Contains("queryClient.removeQueries", boundary, StringComparison.Ordinal);
        Assert.Contains("if (isLoading || isFetching)", myRequests, StringComparison.Ordinal);
        Assert.Contains("refetchInterval: 15_000", requestDetails, StringComparison.Ordinal);
        Assert.Contains("authorityScope !== getCurrentAuthorityScope()", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("setQueryData", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("localStorage", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("sessionStorage", workflow, StringComparison.Ordinal);
    }

    [Fact]
    public Task CertificationDocumentation_MustRecordPassFs5C() =>
        // Actual rated/unrated review-identity consistency,missing/contradictory
        // state,pre-completion and provider/foreign/self-owned negatives.
        MarketplaceFrontendBehaviorContract.AssertAsync("src/marketplace/ratingStateAuthority.test.ts", 4);

    private static string ReadRepoFile(string relativePath) => CertificationSource.ReadRepoFile(relativePath);
}
