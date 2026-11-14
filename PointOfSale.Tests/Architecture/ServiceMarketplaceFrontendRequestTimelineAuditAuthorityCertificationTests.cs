namespace PointOfSale.Tests.Architecture;

public sealed class ServiceMarketplaceFrontendRequestTimelineAuditAuthorityCertificationTests
{
    [Fact]
    public void RequestTimelineAudit_MustUseBackendAuditTimelineBoundaryOnly()
    {
        var controller = ReadRepoFile("PointOfSale.API/Controllers/MarketplaceController.cs");
        var types = ReadRepoFile("PointOfSale.Web/src/marketplace/types/index.ts");
        var timelineAuthority = ReadRepoFile("PointOfSale.Web/src/marketplace/timelineAuthority.ts");
        var chatPage = ReadRepoFile("PointOfSale.Web/src/marketplace/pages/ChatPage.tsx");

        Assert.Contains("auditTimeline = BuildRequestAuditTimeline(request)", controller, StringComparison.Ordinal);
        Assert.Contains("request.AcceptedAt", controller, StringComparison.Ordinal);
        Assert.Contains("request.RejectedAt", controller, StringComparison.Ordinal);
        Assert.Contains("request.CompletedByProviderAt", controller, StringComparison.Ordinal);
        Assert.Contains("request.CompletedByBuyerAt", controller, StringComparison.Ordinal);
        Assert.Contains("request.CancelledAt", controller, StringComparison.Ordinal);

        Assert.Contains("ServiceRequestAuditEvent", types, StringComparison.Ordinal);
        Assert.Contains("deriveRequestAuditTimelinePresentation", timelineAuthority, StringComparison.Ordinal);
        Assert.Contains("seenIds.has(eventId)", timelineAuthority, StringComparison.Ordinal);
        Assert.Contains("timestamp < lastTimestamp", timelineAuthority, StringComparison.Ordinal);
        Assert.Contains("requiredEventsByStatus[currentStatus]", timelineAuthority, StringComparison.Ordinal);
        Assert.Contains("allowedEventsByStatus[currentStatus]", timelineAuthority, StringComparison.Ordinal);

        Assert.Contains("deriveCompletionTimelineAuditPresentation(request, completionState, currentUserId)", chatPage, StringComparison.Ordinal);
        Assert.DoesNotContain("new Date(request.createdAt)", chatPage, StringComparison.Ordinal);
    }

    [Fact]
    public void RealtimeTimelineAudit_MustInvalidateAndRefetchInsteadOfAppendingTimelineTruth()
    {
        var chatHook = ReadRepoFile("PointOfSale.Web/src/marketplace/hooks/useChat.ts");

        Assert.Contains("Realtime is an invalidation trigger only", chatHook, StringComparison.Ordinal);
        Assert.Contains("queryClient.invalidateQueries({ queryKey: marketplaceKeys.scope(authorityScope) })", chatHook, StringComparison.Ordinal);
        Assert.Contains("queryClient.resetQueries({ queryKey: marketplaceKeys.chat(requestId, authorityScope)", chatHook, StringComparison.Ordinal);
        Assert.Contains("queryClient.invalidateQueries({ queryKey: marketplaceKeys.request(requestId, authorityScope) })", chatHook, StringComparison.Ordinal);
        Assert.DoesNotContain("setQueryData<ChatMessage[]>", chatHook, StringComparison.Ordinal);
        Assert.DoesNotContain("(old = []) => [...old, message]", chatHook, StringComparison.Ordinal);
    }

    [Fact]
    public Task CertificationDocumentation_MustRecordPassFs2E() =>
        // Actual backend-consistent timeline plus fabricated,duplicate,reordered,
        // foreign-request and current-lifecycle contradiction negatives.
        MarketplaceFrontendBehaviorContract.AssertAsync("src/marketplace/timelineAuthority.test.ts", 3);

    private static string ReadRepoFile(string relativePath) => CertificationSource.ReadRepoFile(relativePath);
}
