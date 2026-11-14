namespace PointOfSale.Tests.Architecture;

public sealed class ServiceMarketplaceFrontendRealtimeAuthorityCertificationTests
{
    [Fact]
    public void SignalR_MustRemainCurrentRequestInvalidationTransportOnly()
    {
        var chatHook = ReadRepoFile("PointOfSale.Web/src/marketplace/hooks/useChat.ts");

        Assert.Contains("if (!identity?.canSendChat || historyAuthorityFailed) return", chatHook, StringComparison.Ordinal);
        Assert.Contains("isCurrentChatRealtimeEvent(message, identity)", chatHook, StringComparison.Ordinal);
        Assert.Contains("marketplaceKeys.chat(requestId, authorityScope)", chatHook, StringComparison.Ordinal);
        Assert.Contains("acceptsRealtimeEvents = false", chatHook, StringComparison.Ordinal);
        Assert.Contains("connectionAuthorityRef.current = null", chatHook, StringComparison.Ordinal);
        Assert.Contains("connectionAuthority.connection.state !== signalR.HubConnectionState.Connected", chatHook, StringComparison.Ordinal);
        Assert.DoesNotContain("setQueryData<ChatMessage[]>", chatHook, StringComparison.Ordinal);
        Assert.DoesNotContain("(old = []) => [...old, message]", chatHook, StringComparison.Ordinal);
    }

    [Fact]
    public void Reconnect_MustRetireRecoveredTransportAndReenterBackendAuthority()
    {
        var chatHook = ReadRepoFile("PointOfSale.Web/src/marketplace/hooks/useChat.ts");
        var reconnectStart = chatHook.IndexOf("newConnection.onreconnected", StringComparison.Ordinal);
        var reconnectEnd = chatHook.IndexOf("newConnection.onclose", reconnectStart, StringComparison.Ordinal);
        var reconnectBlock = chatHook[reconnectStart..reconnectEnd];

        Assert.Contains("acceptsRealtimeEvents = false", reconnectBlock, StringComparison.Ordinal);
        Assert.Contains("newConnection.stop()", reconnectBlock, StringComparison.Ordinal);
        Assert.Contains("marketplaceKeys.request(requestId, authorityScope)", reconnectBlock, StringComparison.Ordinal);
        Assert.Contains("marketplaceKeys.chat(requestId, authorityScope)", reconnectBlock, StringComparison.Ordinal);
        Assert.DoesNotContain("JoinConversation", reconnectBlock, StringComparison.Ordinal);
        Assert.DoesNotContain("setIsConnected(true)", reconnectBlock, StringComparison.Ordinal);
    }

    [Fact]
    public void ConnectionStartup_MustNotInvalidateRequestAuthorityAndLoopTeardown()
    {
        var chatHook = ReadRepoFile("PointOfSale.Web/src/marketplace/hooks/useChat.ts");
        var startupStart = chatHook.IndexOf("void (async () =>", StringComparison.Ordinal);
        var startupEnd = chatHook.IndexOf("return () =>", startupStart, StringComparison.Ordinal);
        var startupBlock = chatHook[startupStart..startupEnd];

        Assert.Contains("await newConnection.invoke('JoinConversation', requestId)", startupBlock, StringComparison.Ordinal);
        Assert.Contains("marketplaceKeys.chat(requestId, authorityScope)", startupBlock, StringComparison.Ordinal);
        Assert.DoesNotContain("marketplaceKeys.request(requestId, authorityScope)", startupBlock, StringComparison.Ordinal);
        Assert.Contains("if (!acceptsRealtimeEvents)", startupBlock, StringComparison.Ordinal);
    }

    [Fact]
    public void BroadcastChannel_MustBeScopedValidatedAndDerivative()
    {
        var authority = ReadRepoFile("PointOfSale.Web/src/marketplace/authority.ts");
        var boundary = ReadRepoFile("PointOfSale.Web/src/marketplace/components/MarketplaceAuthorityBoundary.tsx");

        Assert.Contains("isMarketplaceAuthorityInvalidation", authority, StringComparison.Ordinal);
        Assert.Contains("event.authorityScope === authorityScope", authority, StringComparison.Ordinal);
        Assert.Contains("authorityScope !== getMarketplaceAuthorityScope()", authority, StringComparison.Ordinal);
        Assert.Contains("channel.postMessage({ type: 'invalidate', authorityScope, serviceId })", authority, StringComparison.Ordinal);
        Assert.Contains("try {", authority, StringComparison.Ordinal);
        Assert.Contains("isMarketplaceAuthorityInvalidation(event.data, scope)", boundary, StringComparison.Ordinal);
        Assert.DoesNotContain("setQueryData", boundary, StringComparison.Ordinal);
    }

    [Fact]
    public void LifecycleCrossTabSignals_MustPublishOnlyScopedInvalidation()
    {
        foreach (var source in new[]
        {
            ReadRepoFile("PointOfSale.Web/src/marketplace/hooks/useAcceptRequest.ts"),
            ReadRepoFile("PointOfSale.Web/src/marketplace/hooks/useCancelRequest.ts"),
            ReadRepoFile("PointOfSale.Web/src/marketplace/hooks/useConfirmCompletion.ts"),
            ReadRepoFile("PointOfSale.Web/src/marketplace/hooks/useMarkCompleted.ts"),
            MarketplaceRequestCommandCompositionContract.IncomingCommands(),
            MarketplaceRequestCommandCompositionContract.BuyerCommands()
        })
        {
            Assert.Contains("publishMarketplaceAuthorityInvalidation(authorityScope)", source, StringComparison.Ordinal);
            Assert.Contains("marketplaceKeys.scope(authorityScope)", source, StringComparison.Ordinal);
            Assert.DoesNotContain("setQueryData", source, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task CertificationDocumentation_MustRecordPassFs3D()
    {
        // Actual cross-tab scope validation and current-request participant/event
        // validation. The separate connection/reconnect source contracts remain.
        await MarketplaceFrontendBehaviorContract.AssertAsync("src/marketplace/realtimeAuthority.test.ts", 2);
    }

    private static string ReadRepoFile(string relativePath) => CertificationSource.ReadRepoFile(relativePath);
}
