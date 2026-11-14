namespace PointOfSale.Tests.Architecture;

public sealed class ServiceMarketplaceFrontendCompletionEligibilityAuthorityCertificationTests
{
    [Fact]
    public void CompletionEligibility_MustBindBackendLifecycleCapabilitiesToCurrentActorIdentity()
    {
        var controller = ReadRepoFile("PointOfSale.API/Controllers/MarketplaceController.cs");
        var eligibility = ReadRepoFile("PointOfSale.Web/src/marketplace/completionEligibility.ts");
        var myRequests = ReadRepoFile("PointOfSale.Web/src/marketplace/pages/tabs/MyRequestsTab.tsx");
        var incoming = ReadRepoFile("PointOfSale.Web/src/marketplace/pages/tabs/IncomingTab.tsx");
        var requestCard = ReadRepoFile("PointOfSale.Web/src/marketplace/components/RequestCard.tsx");
        var requestDetails = ReadRepoFile("PointOfSale.Web/src/marketplace/viewmodels/useRequestDetailsVM.ts");

        Assert.Contains("buyerUserId = r.BuyerUserId", controller, StringComparison.Ordinal);
        Assert.Contains("providerUserId = r.ProviderUserId", controller, StringComparison.Ordinal);
        Assert.Contains("deriveServiceRequestCompletionEligibility", eligibility, StringComparison.Ordinal);
        Assert.Contains("buyerUserId === providerUserId", eligibility, StringComparison.Ordinal);
        Assert.Contains("actorUserId === buyerUserId", eligibility, StringComparison.Ordinal);
        Assert.Contains("actorUserId === providerUserId", eligibility, StringComparison.Ordinal);
        Assert.Contains("capabilities.canMarkCompleted && actorRole !== 'provider'", eligibility, StringComparison.Ordinal);
        Assert.Contains("capabilities.canConfirmCompletion && actorRole !== 'buyer'", eligibility, StringComparison.Ordinal);
        Assert.Contains("lifecycle.statusKey === 'accepted'", eligibility, StringComparison.Ordinal);
        Assert.Contains("lifecycle.statusKey === 'completedByProvider'", eligibility, StringComparison.Ordinal);

        foreach (var source in new[] { myRequests, incoming, requestCard, requestDetails })
        {
            Assert.Contains("deriveServiceRequestCompletionEligibility", source, StringComparison.Ordinal);
        }

        Assert.Contains("'buyer'", myRequests, StringComparison.Ordinal);
        Assert.Contains("'provider'", incoming, StringComparison.Ordinal);
    }

    [Fact]
    public void CompletionMutationsAndRealtime_MustReenterScopedBackendAuthority()
    {
        var markCompleted = ReadRepoFile("PointOfSale.Web/src/marketplace/hooks/useMarkCompleted.ts");
        var confirmCompletion = ReadRepoFile("PointOfSale.Web/src/marketplace/hooks/useConfirmCompletion.ts");
        var incoming = ReadRepoFile("PointOfSale.Web/src/marketplace/pages/tabs/IncomingTab.tsx");
        var myRequests = ReadRepoFile("PointOfSale.Web/src/marketplace/pages/tabs/MyRequestsTab.tsx");
        var chat = ReadRepoFile("PointOfSale.Web/src/marketplace/hooks/useChat.ts");
        var workflow = ReadRepoFile("PointOfSale.Web/src/marketplace/completionWorkflow.ts");

        var incomingCommands = MarketplaceRequestCommandCompositionContract.IncomingCommands();
        var buyerCommands = MarketplaceRequestCommandCompositionContract.BuyerCommands();
        foreach (var source in new[] { markCompleted, confirmCompletion, incomingCommands, buyerCommands })
        {
            Assert.Contains("executeServiceRequestCompletionWorkflow", source, StringComparison.Ordinal);
            Assert.Contains("publishMarketplaceAuthorityInvalidation(authorityScope)", source, StringComparison.Ordinal);
            Assert.Contains("marketplaceKeys.scope(authorityScope)", source, StringComparison.Ordinal);
            Assert.DoesNotContain("setQueryData", source, StringComparison.Ordinal);
            Assert.DoesNotContain("onMutate", source, StringComparison.Ordinal);
        }

        Assert.Contains("authorityScope !== getCurrentAuthorityScope()", workflow, StringComparison.Ordinal);
        Assert.Contains("deriveServiceRequestLifecycleMutationAuthority", workflow, StringComparison.Ordinal);
        Assert.Contains("await revalidate()", workflow, StringComparison.Ordinal);
        Assert.Contains("completionExecutions", workflow, StringComparison.Ordinal);
        Assert.Contains("queryClient.invalidateQueries({ queryKey: marketplaceKeys.scope(authorityScope) })", chat, StringComparison.Ordinal);
        Assert.DoesNotContain("setQueryData", chat, StringComparison.Ordinal);
    }

    [Fact]
    public void CompletionSurfaces_MustWithholdCachedActionsDuringCurrentRefresh()
    {
        var incoming = ReadRepoFile("PointOfSale.Web/src/marketplace/pages/tabs/IncomingTab.tsx");
        var myRequests = ReadRepoFile("PointOfSale.Web/src/marketplace/pages/tabs/MyRequestsTab.tsx");
        var requestDetails = ReadRepoFile("PointOfSale.Web/src/marketplace/hooks/useRequestDetails.ts");
        var boundary = ReadRepoFile("PointOfSale.Web/src/marketplace/components/MarketplaceAuthorityBoundary.tsx");

        Assert.Contains("if (isLoading || isFetching)", incoming, StringComparison.Ordinal);
        Assert.Contains("if (isLoading || isFetching)", myRequests, StringComparison.Ordinal);
        Assert.Contains("refetchInterval: 15_000", requestDetails, StringComparison.Ordinal);
        Assert.Contains("queryClient.removeQueries", boundary, StringComparison.Ordinal);
        Assert.Contains("AUTHORITY_REVALIDATION_INTERVAL_MS", boundary, StringComparison.Ordinal);
    }

    [Fact]
    public Task CertificationDocumentation_MustRecordPassFs4A() =>
        // Execute real frontend actor/lifecycle capability binding for provider
        // completion,buyer confirmation,and malformed/foreign/contradictory negatives.
        MarketplaceFrontendBehaviorContract.AssertAsync("src/marketplace/completionEligibility.test.ts", 3);

    private static string ReadRepoFile(string relativePath) => CertificationSource.ReadRepoFile(relativePath);
}
