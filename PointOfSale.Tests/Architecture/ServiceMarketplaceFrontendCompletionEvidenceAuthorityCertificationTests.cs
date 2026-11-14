namespace PointOfSale.Tests.Architecture;

public sealed class ServiceMarketplaceFrontendCompletionEvidenceAuthorityCertificationTests
{
    [Fact]
    public void CompletionEvidence_MustBeBackendComposedAndFailClosed()
    {
        var controller = ReadRepoFile("PointOfSale.API/Controllers/MarketplaceController.cs");
        var types = ReadRepoFile("PointOfSale.Web/src/marketplace/types/index.ts");
        var authority = ReadRepoFile("PointOfSale.Web/src/marketplace/completionEvidenceAuthority.ts");
        var stateAuthority = ReadRepoFile("PointOfSale.Web/src/marketplace/completionStateAuthority.ts");
        var chatPage = ReadRepoFile("PointOfSale.Web/src/marketplace/pages/ChatPage.tsx");
        var chatMessage = ReadRepoFile("PointOfSale.Web/src/marketplace/components/ChatMessage.tsx");
        var requestViewModel = ReadRepoFile("PointOfSale.Web/src/marketplace/viewmodels/useRequestDetailsVM.ts");

        Assert.Contains("completionEvidence = BuildCompletionEvidence(request)", controller, StringComparison.Ordinal);
        Assert.Contains("request.CompletedByProviderAt", controller, StringComparison.Ordinal);
        Assert.Contains("request.CompletedByBuyerAt", controller, StringComparison.Ordinal);
        Assert.Contains("notes = Array.Empty<object>()", controller, StringComparison.Ordinal);
        Assert.Contains("attachments = Array.Empty<object>()", controller, StringComparison.Ordinal);
        Assert.Contains("deliverables = Array.Empty<object>()", controller, StringComparison.Ordinal);

        Assert.Contains("completionEvidence?: ServiceRequestCompletionEvidence", types, StringComparison.Ordinal);
        Assert.Contains("deriveCompletionEvidencePresentation", authority, StringComparison.Ordinal);
        Assert.Contains("actorUserId !== buyerUserId && actorUserId !== providerUserId", authority, StringComparison.Ordinal);
        Assert.Contains("evidence.attachments.length !== 0", authority, StringComparison.Ordinal);
        Assert.Contains("evidence.deliverables.length !== 0", authority, StringComparison.Ordinal);
        Assert.Contains("Date.parse(buyerConfirmation.occurredAt) < Date.parse(providerCompletion.occurredAt)", authority, StringComparison.Ordinal);

        Assert.Contains("deriveCompletionEvidencePresentation", stateAuthority, StringComparison.Ordinal);
        Assert.Contains("deriveServiceRequestCompletionStatePresentation", chatPage, StringComparison.Ordinal);
        Assert.Contains("request && !isLoading && !isFetching && !isError", requestViewModel, StringComparison.Ordinal);
        Assert.DoesNotContain("completionEvidence", chatMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("localStorage", authority, StringComparison.Ordinal);
        Assert.DoesNotContain("sessionStorage", authority, StringComparison.Ordinal);
        Assert.DoesNotContain("buildAttachmentDownloadPath", authority, StringComparison.Ordinal);
    }

    [Fact]
    public void CompletionEvidence_CacheRealtimeDownloadAndPreviewMustRemainDerivative()
    {
        var requestHook = ReadRepoFile("PointOfSale.Web/src/marketplace/hooks/useRequestDetails.ts");
        var queryKeys = ReadRepoFile("PointOfSale.Web/src/marketplace/queryKeys.ts");
        var chat = ReadRepoFile("PointOfSale.Web/src/marketplace/hooks/useChat.ts");
        var attachmentAuthority = ReadRepoFile("PointOfSale.Web/src/marketplace/attachmentAuthority.ts");

        Assert.Contains("marketplaceKeys.request(requestId, authorityScope)", requestHook, StringComparison.Ordinal);
        Assert.Contains("refetchInterval: 15_000", requestHook, StringComparison.Ordinal);
        Assert.Contains("staleTime: 0", ReadRepoFile("PointOfSale.Web/src/marketplace/authority.ts"), StringComparison.Ordinal);
        Assert.Contains("scope: (scope = getMarketplaceAuthorityScope())", queryKeys, StringComparison.Ordinal);
        Assert.Contains("invalidateQueries({ queryKey: marketplaceKeys.scope(authorityScope) })", chat, StringComparison.Ordinal);
        Assert.DoesNotContain("setQueryData", chat, StringComparison.Ordinal);
        Assert.Contains("/api/marketplace/attachments/", attachmentAuthority, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CertificationDocumentation_MustRecordPassFs4C()
    {
        // Actual participant/phase chronology,required metadata and injected
        // notes/attachment/deliverable refusal; no invented evidence or PASS prose.
        await MarketplaceFrontendBehaviorContract.AssertAsync("src/marketplace/completionEvidenceAuthority.test.ts", 4);
    }

    private static string ReadRepoFile(string relativePath) => CertificationSource.ReadRepoFile(relativePath);
}
