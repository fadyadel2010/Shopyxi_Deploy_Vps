namespace PointOfSale.Tests.Architecture;

public sealed class ServiceMarketplaceFrontendAttachmentAuthorityCertificationTests
{
    [Fact]
    public void AttachmentIdentityMetadataAndUploads_MustRemainBackendDerived()
    {
        var attachmentAuthority = ReadRepoFile("PointOfSale.Web/src/marketplace/attachmentAuthority.ts");
        var chatAuthority = ReadRepoFile("PointOfSale.Web/src/marketplace/chatAuthority.ts");
        var chatPage = ReadRepoFile("PointOfSale.Web/src/marketplace/pages/ChatPage.tsx");
        var uploadHook = ReadRepoFile("PointOfSale.Web/src/marketplace/hooks/useChatAttachmentUpload.ts");

        Assert.Contains("attachmentId !== undefined && attachmentId !== id", chatAuthority, StringComparison.Ordinal);
        Assert.Contains("normalizeAttachmentFileName(row.attachmentFileName)", chatAuthority, StringComparison.Ordinal);
        Assert.Contains("deriveChatAttachmentUploadAuthority", attachmentAuthority, StringComparison.Ordinal);
        Assert.Contains("normalizeGuid(segments[3]) !== identity.requestId", attachmentAuthority, StringComparison.Ordinal);
        Assert.Contains("normalizeGuid(segments[4]) !== identity.currentUserId", attachmentAuthority, StringComparison.Ordinal);
        Assert.Contains("segments[5] !== fileName", attachmentAuthority, StringComparison.Ordinal);

        Assert.Contains("import { useChatAttachmentUpload }", chatPage, StringComparison.Ordinal);
        Assert.Contains("const uploadChatAttachment = useChatAttachmentUpload(chatIdentity)", chatPage, StringComparison.Ordinal);
        Assert.Contains("await uploadChatAttachment(sendIdentity.requestId, selectedFile)", chatPage, StringComparison.Ordinal);
        Assert.Contains("marketplaceApi.uploadChatAttachment(requestId, file)", uploadHook, StringComparison.Ordinal);
        Assert.Contains("context.identity?.requestId !== requestId", uploadHook, StringComparison.Ordinal);
        Assert.Contains("getMarketplaceAuthorityScope() !== context.authorityScope", uploadHook, StringComparison.Ordinal);
        Assert.Contains("binding(current.current.identity, current.current.authorityScope) !== key", uploadHook, StringComparison.Ordinal);
        Assert.DoesNotContain("sendMessage", uploadHook, StringComparison.Ordinal);
        Assert.Contains("deriveChatAttachmentUploadAuthority(payload, sendIdentity)", chatPage, StringComparison.Ordinal);
        Assert.DoesNotContain("response.data?.objectKey", chatPage, StringComparison.Ordinal);
    }

    [Fact]
    public void AttachmentVisibilityPreviewAndDownloads_MustReenterCurrentAuthority()
    {
        var chatMessage = ReadRepoFile("PointOfSale.Web/src/marketplace/components/ChatMessage.tsx");
        var chatPage = ReadRepoFile("PointOfSale.Web/src/marketplace/pages/ChatPage.tsx");
        var controller = ReadRepoFile("PointOfSale.API/Controllers/MarketplaceController.cs");

        Assert.Contains("buildAttachmentDownloadPath(message.attachmentId)", chatMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("const isImage =", chatMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("<img", chatMessage, StringComparison.Ordinal);
        Assert.Contains("CanUserViewConversationAsync", controller, StringComparison.Ordinal);
        Assert.Contains("Response.Headers.CacheControl = \"no-store, private, max-age=0\"", controller, StringComparison.Ordinal);

        Assert.Contains("attachmentIntentScopeRef", chatPage, StringComparison.Ordinal);
        Assert.Contains("setSelectedFile(null)", chatPage, StringComparison.Ordinal);
        Assert.Contains("sendOperationIdRef.current += 1", chatPage, StringComparison.Ordinal);
        Assert.Contains("sendOperationIdRef.current === sendOperationId", chatPage, StringComparison.Ordinal);
    }

    [Fact]
    public void AttachmentCachesRealtimeAndActorTransitions_MustNotCreateTruth()
    {
        var chatHook = ReadRepoFile("PointOfSale.Web/src/marketplace/hooks/useChat.ts");
        var queryKeys = ReadRepoFile("PointOfSale.Web/src/marketplace/queryKeys.ts");
        var authorityBoundary = ReadRepoFile("PointOfSale.Web/src/marketplace/components/MarketplaceAuthorityBoundary.tsx");

        Assert.Contains("Realtime is an invalidation trigger only", chatHook, StringComparison.Ordinal);
        Assert.DoesNotContain("setQueryData<ChatMessage[]>", chatHook, StringComparison.Ordinal);
        Assert.Contains("chat: (requestId: string, scope", queryKeys, StringComparison.Ordinal);
        Assert.Contains("queryClient.removeQueries", authorityBoundary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CertificationDocumentation_MustRecordPassFs3C()
    {
        // Actual upload response/key/filename/download-route validation and the
        // real hook's duplicate,actor/scope-change and late-unmount result fences.
        await MarketplaceFrontendBehaviorContract.AssertAsync("src/marketplace/attachmentAuthority.test.ts", 3);
        await MarketplaceFrontendBehaviorContract.AssertAsync("src/marketplace/hooks/useChatAttachmentUpload.test.tsx", 3);
    }

    private static string ReadRepoFile(string relativePath) => CertificationSource.ReadRepoFile(relativePath);
}
