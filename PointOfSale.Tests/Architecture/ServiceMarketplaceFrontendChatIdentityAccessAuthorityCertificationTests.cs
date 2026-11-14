namespace PointOfSale.Tests.Architecture;

public sealed class ServiceMarketplaceFrontendChatIdentityAccessAuthorityCertificationTests
{
    [Fact]
    public void ChatRouteAndMessages_MustBindToCurrentBackendParticipantAuthority()
    {
        var chatAuthority = ReadRepoFile("PointOfSale.Web/src/marketplace/chatAuthority.ts");
        var chatPage = ReadRepoFile("PointOfSale.Web/src/marketplace/pages/ChatPage.tsx");
        var chatHook = ReadRepoFile("PointOfSale.Web/src/marketplace/hooks/useChat.ts");
        var status = ReadRepoFile("PointOfSale.Web/src/marketplace/status.ts");

        Assert.Contains("deriveChatIdentityAuthority", chatAuthority, StringComparison.Ordinal);
        Assert.Contains("requestId !== routeId", chatAuthority, StringComparison.Ordinal);
        Assert.Contains("buyerUserId === providerUserId", chatAuthority, StringComparison.Ordinal);
        Assert.Contains("actorUserId === buyerUserId", chatAuthority, StringComparison.Ordinal);
        Assert.Contains("actorUserId === providerUserId", chatAuthority, StringComparison.Ordinal);
        Assert.Contains("serviceRequestId !== identity.requestId", chatAuthority, StringComparison.Ordinal);
        Assert.Contains("senderRole !== expectedSenderRole", chatAuthority, StringComparison.Ordinal);
        Assert.Contains("messageIds.has(id)", chatAuthority, StringComparison.Ordinal);
        Assert.Contains("createdTimestamp < previousTimestamp", chatAuthority, StringComparison.Ordinal);

        Assert.Contains("deriveChatIdentityAuthority(request, requestId, currentUserId, lifecycle)", chatPage, StringComparison.Ordinal);
        Assert.Contains("!isFetchingRequest", chatPage, StringComparison.Ordinal);
        Assert.Contains("!requestAuthorityFailed", chatPage, StringComparison.Ordinal);
        Assert.Contains("message.senderUserId === chatIdentity.currentUserId", chatPage, StringComparison.Ordinal);

        Assert.Contains("deriveChatHistoryPageAuthority(payload, identity, pageParam)", chatHook, StringComparison.Ordinal);
        Assert.Contains("mergeChatHistoryPagesAuthority", chatHook, StringComparison.Ordinal);
        Assert.Contains("historyAuthorityFailed", chatHook, StringComparison.Ordinal);
        Assert.Contains("throw new Error('Invalid chat history page authority response')", chatHook, StringComparison.Ordinal);
        Assert.DoesNotContain("request.canViewChatHistory ?? request.canChat", status, StringComparison.Ordinal);
        Assert.DoesNotContain("request.canSendChat ?? request.canChat", status, StringComparison.Ordinal);
    }

    [Fact]
    public void ChatRealtime_MustStopAndFenceAllWorkWhenAuthorityIsLost()
    {
        var chatHook = ReadRepoFile("PointOfSale.Web/src/marketplace/hooks/useChat.ts");

        Assert.Contains("if (!identity?.canSendChat || historyAuthorityFailed) return", chatHook, StringComparison.Ordinal);
        Assert.Contains("acceptsRealtimeEvents = false", chatHook, StringComparison.Ordinal);
        Assert.Contains("if (!acceptsRealtimeEvents)", chatHook, StringComparison.Ordinal);
        Assert.Contains("connectionAuthorityRef.current = null", chatHook, StringComparison.Ordinal);
        Assert.Contains("void newConnection.stop()", chatHook, StringComparison.Ordinal);
        Assert.Contains("connectionAuthority.connection.state !== signalR.HubConnectionState.Connected", chatHook, StringComparison.Ordinal);
        Assert.Contains("isCurrentChatRealtimeEvent(message, identity)", chatHook, StringComparison.Ordinal);
        Assert.DoesNotContain("setQueryData<ChatMessage[]>", chatHook, StringComparison.Ordinal);
    }

    [Fact]
    public void ChatNavigation_MustUseBackendHistoryCapabilityOnly()
    {
        foreach (var relativePath in new[]
        {
            "PointOfSale.Web/src/marketplace/components/RequestCard.tsx",
            "PointOfSale.Web/src/marketplace/pages/tabs/MyRequestsTab.tsx",
            "PointOfSale.Web/src/marketplace/pages/tabs/IncomingTab.tsx"
        })
        {
            var source = ReadRepoFile(relativePath);
            Assert.Contains("canViewChatHistory", source, StringComparison.Ordinal);
            Assert.DoesNotContain("canChat &&", source, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task CertificationDocumentation_MustRecordPassFs3A()
    {
        // Execute actual route/participant/lifecycle authority plus foreign role,
        // request, duplicate chronology and malformed attachment/event negatives.
        await MarketplaceFrontendBehaviorContract.AssertAsync("src/marketplace/chatAuthority.test.ts", 3);
    }

    private static string ReadRepoFile(string relativePath) => CertificationSource.ReadRepoFile(relativePath);
}
