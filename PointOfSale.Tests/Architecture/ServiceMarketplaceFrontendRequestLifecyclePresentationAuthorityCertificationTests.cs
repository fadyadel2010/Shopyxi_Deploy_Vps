namespace PointOfSale.Tests.Architecture;

public sealed class ServiceMarketplaceFrontendRequestLifecyclePresentationAuthorityCertificationTests
{
    [Fact]
    public void StatusBadgesAndActions_MustUseLifecyclePresentationBoundary()
    {
        var status = ReadRepoFile("PointOfSale.Web/src/marketplace/status.ts");
        var card = ReadRepoFile("PointOfSale.Web/src/marketplace/components/RequestCard.tsx");
        var myRequests = ReadRepoFile("PointOfSale.Web/src/marketplace/pages/tabs/MyRequestsTab.tsx");
        var incoming = ReadRepoFile("PointOfSale.Web/src/marketplace/pages/tabs/IncomingTab.tsx");
        var chatPage = ReadRepoFile("PointOfSale.Web/src/marketplace/pages/ChatPage.tsx");
        var detailsVm = ReadRepoFile("PointOfSale.Web/src/marketplace/viewmodels/useRequestDetailsVM.ts");

        Assert.Contains("deriveServiceRequestLifecyclePresentation", status, StringComparison.Ordinal);
        Assert.Contains("deriveServiceRequestDetailsLifecyclePresentation", status, StringComparison.Ordinal);
        Assert.Contains("return null", status, StringComparison.Ordinal);
        Assert.Contains("serviceRequestStatusColors", status, StringComparison.Ordinal);
        Assert.Contains("allowsLifecycleAction(statusKey, 'canAccept')", status, StringComparison.Ordinal);
        Assert.Contains("allowsLifecycleAction(statusKey, 'canReview')", status, StringComparison.Ordinal);

        foreach (var source in new[] { card, myRequests, incoming, chatPage, detailsVm })
        {
            Assert.Contains("deriveServiceRequest", source, StringComparison.Ordinal);
        }

        Assert.DoesNotContain("defaultValue: request.status", card, StringComparison.Ordinal);
        Assert.DoesNotContain("defaultValue: request.status", myRequests, StringComparison.Ordinal);
        Assert.DoesNotContain("defaultValue: request.status", incoming, StringComparison.Ordinal);
        Assert.Contains("return null", card, StringComparison.Ordinal);
        Assert.Contains("lifecycle.canAccept", card, StringComparison.Ordinal);
        Assert.Contains("lifecycle.canReject", incoming, StringComparison.Ordinal);
        // Rating uses the stricter buyer/final-state/existing-review boundary.
        Assert.Contains("ratingEligibility?.canRate", myRequests, StringComparison.Ordinal);
        Assert.Contains("deriveServiceRequestRatingEligibility(", myRequests, StringComparison.Ordinal);
        MarketplaceRequestCommandCompositionContract.AssertRatingStateDelegation();
    }

    [Fact]
    public void ListsDetailsAndRealtime_MustNotDisplayCachedLifecycleDuringCurrentRefresh()
    {
        var myRequests = ReadRepoFile("PointOfSale.Web/src/marketplace/pages/tabs/MyRequestsTab.tsx");
        var incoming = ReadRepoFile("PointOfSale.Web/src/marketplace/pages/tabs/IncomingTab.tsx");
        var chatPage = ReadRepoFile("PointOfSale.Web/src/marketplace/pages/ChatPage.tsx");
        var chatHook = ReadRepoFile("PointOfSale.Web/src/marketplace/hooks/useChat.ts");
        var boundary = ReadRepoFile("PointOfSale.Web/src/marketplace/components/MarketplaceAuthorityBoundary.tsx");

        Assert.Contains("isLoading, isFetching, isError", myRequests, StringComparison.Ordinal);
        Assert.Contains("if (isLoading || isFetching)", myRequests, StringComparison.Ordinal);
        Assert.Contains("isLoading, isFetching, isError", incoming, StringComparison.Ordinal);
        Assert.Contains("if (isLoading || isFetching)", incoming, StringComparison.Ordinal);
        Assert.Contains("isFetching: isFetchingRequest", chatPage, StringComparison.Ordinal);
        Assert.Contains("if (isLoadingRequest || isFetchingRequest)", chatPage, StringComparison.Ordinal);
        Assert.Contains("isCurrentChatRealtimeEvent(message, identity)", chatHook, StringComparison.Ordinal);
        Assert.Contains("queryClient.invalidateQueries({ queryKey: marketplaceKeys.request(requestId, authorityScope) })", chatHook, StringComparison.Ordinal);
        Assert.Contains("isMarketplaceAuthorityInvalidation(event.data, scope)", boundary, StringComparison.Ordinal);
    }

    [Fact]
    public void ProviderRejection_MustUseRejectedLifecycleEndpointNotCancellationEndpoint()
    {
        var api = ReadRepoFile("PointOfSale.Web/src/marketplace/api/marketplaceApi.ts");
        var incoming = ReadRepoFile("PointOfSale.Web/src/marketplace/pages/tabs/IncomingTab.tsx");
        var controller = ReadRepoFile("PointOfSale.API/Controllers/MarketplaceController.cs");
        var entity = ReadRepoFile("PointOfSale.Core/Entities/ServiceRequest.cs");
        var commands = ReadRepoFile("PointOfSale.Web/src/marketplace/hooks/useIncomingRequestCommands.ts");

        Assert.Contains("rejectRequest", api, StringComparison.Ordinal);
        Assert.Contains("/marketplace/requests/${requestId}/reject", api, StringComparison.Ordinal);
        Assert.Contains("useIncomingRequestCommands(currentUserId)", incoming, StringComparison.Ordinal);
        Assert.Contains("rejectMutation.mutateAsync(requestId)", incoming, StringComparison.Ordinal);
        Assert.Contains("marketplaceApi.rejectRequest(requestId)", commands, StringComparison.Ordinal);
        Assert.Contains("getMarketplaceAuthorityScope() !== authorityScope", commands, StringComparison.Ordinal);
        Assert.Contains("deriveServiceRequestLifecycleMutationAuthority(response, 'reject')", commands, StringComparison.Ordinal);
        Assert.DoesNotContain("marketplaceApi.cancelRequest", commands, StringComparison.Ordinal);
        Assert.DoesNotContain("marketplaceApi.cancelRequest(requestId, reason)", incoming, StringComparison.Ordinal);
        Assert.Contains("[HttpPost(\"requests/{requestId}/reject\")]", controller, StringComparison.Ordinal);
        Assert.Contains("RejectRequestAsync(requestId, userId)", controller, StringComparison.Ordinal);
        Assert.Contains("Status = ServiceRequestStatus.Rejected", entity, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CertificationDocumentation_MustRecordPassFs2B()
    {
        // Actual status/capability contradictions,explicit chat capabilities and
        // expected transition targets;unknown lifecycle presentation rejects.
        await MarketplaceFrontendBehaviorContract.AssertAsync("src/marketplace/status.test.ts", 5);
    }

    private static string ReadRepoFile(string relativePath) => CertificationSource.ReadRepoFile(relativePath);
}
