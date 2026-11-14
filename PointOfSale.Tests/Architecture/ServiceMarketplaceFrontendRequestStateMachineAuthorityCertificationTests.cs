namespace PointOfSale.Tests.Architecture;

public sealed class ServiceMarketplaceFrontendRequestStateMachineAuthorityCertificationTests
{
    [Fact]
    public void LifecycleMutations_MustValidateExpectedBackendStateMachineTargetAndNeverPatchCache()
    {
        var status = ReadRepoFile("PointOfSale.Web/src/marketplace/status.ts");
        var incoming = ReadRepoFile("PointOfSale.Web/src/marketplace/pages/tabs/IncomingTab.tsx");
        var myRequests = ReadRepoFile("PointOfSale.Web/src/marketplace/pages/tabs/MyRequestsTab.tsx");
        var acceptHook = ReadRepoFile("PointOfSale.Web/src/marketplace/hooks/useAcceptRequest.ts");
        var cancelHook = ReadRepoFile("PointOfSale.Web/src/marketplace/hooks/useCancelRequest.ts");
        var markCompletedHook = ReadRepoFile("PointOfSale.Web/src/marketplace/hooks/useMarkCompleted.ts");
        var confirmHook = ReadRepoFile("PointOfSale.Web/src/marketplace/hooks/useConfirmCompletion.ts");
        var completionWorkflow = ReadRepoFile("PointOfSale.Web/src/marketplace/completionWorkflow.ts");

        Assert.Contains("deriveServiceRequestLifecycleMutationAuthority", status, StringComparison.Ordinal);
        Assert.Contains("accept: 'accepted'", status, StringComparison.Ordinal);
        Assert.Contains("reject: 'rejected'", status, StringComparison.Ordinal);
        Assert.Contains("cancel: 'cancelled'", status, StringComparison.Ordinal);
        Assert.Contains("markCompleted: 'completedByProvider'", status, StringComparison.Ordinal);
        Assert.Contains("confirmCompletion: 'completedByBuyer'", status, StringComparison.Ordinal);

        var incomingCommands = MarketplaceRequestCommandCompositionContract.IncomingCommands();
        var buyerCommands = MarketplaceRequestCommandCompositionContract.BuyerCommands();
        foreach (var source in new[] { incomingCommands, acceptHook, cancelHook })
        {
            Assert.Contains("deriveServiceRequestLifecycleMutationAuthority", source, StringComparison.Ordinal);
            Assert.Contains("throw new Error", source, StringComparison.Ordinal);
            Assert.Contains("invalidateQueries({ queryKey: marketplaceKeys.scope(authorityScope) })", source, StringComparison.Ordinal);
            Assert.DoesNotContain("setQueryData", source, StringComparison.Ordinal);
            Assert.DoesNotContain("onMutate", source, StringComparison.Ordinal);
        }

        foreach (var source in new[] { incomingCommands, buyerCommands, markCompletedHook, confirmHook })
        {
            Assert.Contains("executeServiceRequestCompletionWorkflow", source, StringComparison.Ordinal);
            Assert.Contains("invalidateQueries({ queryKey: marketplaceKeys.scope(authorityScope) })", source, StringComparison.Ordinal);
            Assert.DoesNotContain("setQueryData", source, StringComparison.Ordinal);
            Assert.DoesNotContain("onMutate", source, StringComparison.Ordinal);
        }

        Assert.Contains("deriveServiceRequestLifecycleMutationAuthority", completionWorkflow, StringComparison.Ordinal);
        Assert.Contains("await revalidate()", completionWorkflow, StringComparison.Ordinal);
    }

    [Fact]
    public void ActionSurfaces_MustBeStatusBoundRefreshWithheldAndSameTabFenced()
    {
        var status = ReadRepoFile("PointOfSale.Web/src/marketplace/status.ts");
        var incoming = ReadRepoFile("PointOfSale.Web/src/marketplace/pages/tabs/IncomingTab.tsx");
        var myRequests = ReadRepoFile("PointOfSale.Web/src/marketplace/pages/tabs/MyRequestsTab.tsx");
        var chatPage = ReadRepoFile("PointOfSale.Web/src/marketplace/pages/ChatPage.tsx");

        Assert.Contains("allowsLifecycleAction(statusKey, 'canAccept')", status, StringComparison.Ordinal);
        Assert.Contains("allowsLifecycleAction(statusKey, 'canReject')", status, StringComparison.Ordinal);
        Assert.Contains("allowsLifecycleAction(statusKey, 'canCancel')", status, StringComparison.Ordinal);
        Assert.Contains("allowsLifecycleAction(statusKey, 'canMarkCompleted')", status, StringComparison.Ordinal);
        Assert.Contains("allowsLifecycleAction(statusKey, 'canConfirmCompletion')", status, StringComparison.Ordinal);

        foreach (var source in new[] { incoming, myRequests })
        {
            Assert.Contains("pendingRequestIdsRef", source, StringComparison.Ordinal);
            Assert.Contains("beginPendingRequest", source, StringComparison.Ordinal);
            Assert.Contains("disabled={requestActionsPending}", source, StringComparison.Ordinal);
            Assert.Contains("if (isLoading || isFetching)", source, StringComparison.Ordinal);
        }

        Assert.Contains("if (isLoadingRequest || isFetchingRequest)", chatPage, StringComparison.Ordinal);
        Assert.Contains("deriveServiceRequestDetailsLifecyclePresentation", chatPage, StringComparison.Ordinal);
    }

    [Fact]
    public void RealtimeHistoryActorAndTenantTransitions_MustInvalidateOrReenterBackendAuthorityOnly()
    {
        var boundary = ReadRepoFile("PointOfSale.Web/src/marketplace/components/MarketplaceAuthorityBoundary.tsx");
        var chatHook = ReadRepoFile("PointOfSale.Web/src/marketplace/hooks/useChat.ts");
        var queryKeys = ReadRepoFile("PointOfSale.Web/src/marketplace/queryKeys.ts");
        var authority = ReadRepoFile("PointOfSale.Web/src/marketplace/authority.ts");

        Assert.Contains("scope: (scope = getMarketplaceAuthorityScope())", queryKeys, StringComparison.Ordinal);
        Assert.Contains("tenantId", authority, StringComparison.Ordinal);
        Assert.Contains("userId", authority, StringComparison.Ordinal);
        Assert.Contains("role !== 'superadmin'", authority, StringComparison.Ordinal);
        Assert.Contains("queryClient.removeQueries", boundary, StringComparison.Ordinal);
        Assert.Contains("isMarketplaceAuthorityInvalidation(event.data, scope)", boundary, StringComparison.Ordinal);
        Assert.Contains("queryClient.invalidateQueries({ queryKey: marketplaceKeys.scope(scope) })", boundary, StringComparison.Ordinal);

        Assert.Contains("isCurrentChatRealtimeEvent(message, identity)", chatHook, StringComparison.Ordinal);
        Assert.Contains("queryClient.invalidateQueries({ queryKey: marketplaceKeys.scope(authorityScope) })", chatHook, StringComparison.Ordinal);
        Assert.Contains("queryClient.resetQueries({ queryKey: marketplaceKeys.chat(requestId, authorityScope), exact: true })", chatHook, StringComparison.Ordinal);
        Assert.DoesNotContain("marketplaceKeys.request(requestId, authorityScope),\r\n        (old", chatHook, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CertificationDocumentation_MustRecordPassFs2D()
    {
        // Actual mutation target admission and the actual incoming hooks' rejection
        // endpoint,scoped invalidation,no retry and late actor-change fences.
        await MarketplaceFrontendBehaviorContract.AssertAsync("src/marketplace/status.test.ts", 5);
        await MarketplaceFrontendBehaviorContract.AssertAsync("src/marketplace/hooks/useIncomingRequestCommands.test.tsx", 5);
    }

    private static string ReadRepoFile(string relativePath) => CertificationSource.ReadRepoFile(relativePath);
}
