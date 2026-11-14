namespace PointOfSale.Tests.Architecture;

public sealed class ServiceMarketplaceFrontendRatingSubmissionWorkflowAuthorityCertificationTests
{
    [Fact]
    public void RatingSubmission_MustUseFreshBackendBoundPayloadAndAuthoritativeSuccessProof()
    {
        var workflow = ReadRepoFile("PointOfSale.Web/src/marketplace/ratingWorkflow.ts");
        var api = ReadRepoFile("PointOfSale.Web/src/marketplace/api/marketplaceApi.ts");

        Assert.Contains("deriveServiceReviewSubmissionAuthority", workflow, StringComparison.Ordinal);
        Assert.Contains("requestId !== authoritativeRequestId", workflow, StringComparison.Ordinal);
        Assert.Contains("isMarketing !== (review.evidence !== undefined)", workflow, StringComparison.Ordinal);
        Assert.Contains("const preflightRequest = await revalidate()", workflow, StringComparison.Ordinal);
        Assert.Contains("mutationResponse = await mutate(submissionAuthority)", workflow, StringComparison.Ordinal);
        Assert.Contains("deriveServiceReviewCreationAuthority", workflow, StringComparison.Ordinal);
        Assert.Contains("const settledRequest = await revalidate()", workflow, StringComparison.Ordinal);
        Assert.Contains("settledEligibility.existingReviewId", workflow, StringComparison.Ordinal);
        Assert.Contains("apiClient.post('/marketplace/service-reviews'", api, StringComparison.Ordinal);
    }

    [Fact]
    public void RatingMutationAndDialog_MustFenceDuplicatesAndPreserveFailureAsFailure()
    {
        var workflow = ReadRepoFile("PointOfSale.Web/src/marketplace/ratingWorkflow.ts");
        var modal = ReadRepoFile("PointOfSale.Web/src/marketplace/components/ReviewModal.tsx");
        var myRequests = ReadRepoFile("PointOfSale.Web/src/marketplace/pages/tabs/MyRequestsTab.tsx");
        var hook = ReadRepoFile("PointOfSale.Web/src/marketplace/hooks/useCreateReview.ts");

        Assert.Contains("ratingExecutions.has(executionKey)", workflow, StringComparison.Ordinal);
        Assert.Contains("await revalidate().catch(() => null)", workflow, StringComparison.Ordinal);
        Assert.Contains("submitPendingRef.current", modal, StringComparison.Ordinal);
        Assert.Contains("await onSubmit(review)", modal, StringComparison.Ordinal);
        Assert.Contains("onClose()", modal, StringComparison.Ordinal);
        Assert.Contains("key={reviewingRequest.requestId}", myRequests, StringComparison.Ordinal);
        Assert.DoesNotContain("Failed to submit review:", myRequests, StringComparison.Ordinal);

        var buyerCommands = MarketplaceRequestCommandCompositionContract.BuyerCommands();
        foreach (var source in new[] { buyerCommands, hook })
        {
            Assert.Contains("retry: false", source, StringComparison.Ordinal);
            Assert.Contains("networkMode: 'online'", source, StringComparison.Ordinal);
            Assert.Contains("publishMarketplaceAuthorityInvalidation(authorityScope)", source, StringComparison.Ordinal);
            Assert.Contains("marketplaceKeys.scope(authorityScope)", source, StringComparison.Ordinal);
            Assert.DoesNotContain("setQueryData", source, StringComparison.Ordinal);
            Assert.DoesNotContain("onMutate", source, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void RatingWorkflow_MustRemainActorTenantCacheRealtimeAndHistoryDerivative()
    {
        var workflow = ReadRepoFile("PointOfSale.Web/src/marketplace/ratingWorkflow.ts");
        var authority = ReadRepoFile("PointOfSale.Web/src/marketplace/authority.ts");
        var boundary = ReadRepoFile("PointOfSale.Web/src/marketplace/components/MarketplaceAuthorityBoundary.tsx");
        var myRequests = ReadRepoFile("PointOfSale.Web/src/marketplace/pages/tabs/MyRequestsTab.tsx");

        Assert.Contains("authorityScope !== getCurrentAuthorityScope()", workflow, StringComparison.Ordinal);
        Assert.Contains("marketplaceAuthorityChannelName", authority, StringComparison.Ordinal);
        Assert.Contains("event.authorityScope === authorityScope", authority, StringComparison.Ordinal);
        Assert.Contains("queryClient.removeQueries", boundary, StringComparison.Ordinal);
        Assert.Contains("isMarketplaceAuthorityInvalidation", boundary, StringComparison.Ordinal);
        Assert.Contains("if (isLoading || isFetching)", myRequests, StringComparison.Ordinal);
        Assert.DoesNotContain("localStorage", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("sessionStorage", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("history.", workflow, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CertificationDocumentation_MustRecordPassFs5B()
    {
        // Execute fresh preflight/settlement,review lineage,category evidence,
        // ambiguous outcome and duplicate arbitration in actual workflow and hooks.
        await MarketplaceFrontendBehaviorContract.AssertAsync("src/marketplace/ratingWorkflow.test.ts", 8);
        await MarketplaceFrontendBehaviorContract.AssertAsync("src/marketplace/hooks/ratingCommandHooks.test.tsx", 12);
    }

    private static string ReadRepoFile(string relativePath) => CertificationSource.ReadRepoFile(relativePath);
}
