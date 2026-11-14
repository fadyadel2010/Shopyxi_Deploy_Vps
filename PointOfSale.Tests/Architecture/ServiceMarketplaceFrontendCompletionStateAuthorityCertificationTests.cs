namespace PointOfSale.Tests.Architecture;

public sealed class ServiceMarketplaceFrontendCompletionStateAuthorityCertificationTests
{
    [Fact]
    public void CompletionState_MustRequireCanonicalLifecycleAndBackendEvidence()
    {
        var controller = ReadRepoFile("PointOfSale.API/Controllers/MarketplaceController.cs");
        var status = ReadRepoFile("PointOfSale.Web/src/marketplace/status.ts");
        var state = ReadRepoFile("PointOfSale.Web/src/marketplace/completionStateAuthority.ts");
        var workflow = ReadRepoFile("PointOfSale.Web/src/marketplace/completionWorkflow.ts");

        Assert.Contains("completionEvidence = BuildCompletionEvidence(r)", controller, StringComparison.Ordinal);
        Assert.Contains("lifecycleStatusByBackendValue", status, StringComparison.Ordinal);
        Assert.DoesNotContain("expired:", status, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("replace(/[-_\\s]/g", status, StringComparison.Ordinal);
        Assert.Contains("deriveServiceRequestCompletionStatePresentation", state, StringComparison.Ordinal);
        Assert.Contains("evidence.status !== lifecycle.statusLabelFallback", state, StringComparison.Ordinal);
        Assert.Contains("evidence.providerCompletion === null && evidence.buyerConfirmation === null", state, StringComparison.Ordinal);
        Assert.Contains("deriveCompletionEvidencePresentation", state, StringComparison.Ordinal);
        Assert.Contains("deriveServiceRequestCompletionStatePresentation", workflow, StringComparison.Ordinal);
        Assert.Contains("|| !completionState", workflow, StringComparison.Ordinal);
    }

    [Fact]
    public void CompletionStateSurfaces_MustUseCurrentScopedBackendAuthority()
    {
        var sources = new[]
        {
            ReadRepoFile("PointOfSale.Web/src/marketplace/components/RequestCard.tsx"),
            ReadRepoFile("PointOfSale.Web/src/marketplace/pages/tabs/MyRequestsTab.tsx"),
            ReadRepoFile("PointOfSale.Web/src/marketplace/pages/tabs/IncomingTab.tsx"),
            ReadRepoFile("PointOfSale.Web/src/marketplace/pages/ChatPage.tsx"),
            ReadRepoFile("PointOfSale.Web/src/marketplace/viewmodels/useRequestDetailsVM.ts")
        };
        var authority = ReadRepoFile("PointOfSale.Web/src/marketplace/authority.ts");
        var boundary = ReadRepoFile("PointOfSale.Web/src/marketplace/components/MarketplaceAuthorityBoundary.tsx");
        var chat = ReadRepoFile("PointOfSale.Web/src/marketplace/hooks/useChat.ts");

        foreach (var source in sources)
        {
            Assert.Contains("deriveServiceRequestCompletionStatePresentation", source, StringComparison.Ordinal);
        }

        Assert.Contains("staleTime: 0", authority, StringComparison.Ordinal);
        Assert.Contains("retry: false", authority, StringComparison.Ordinal);
        Assert.Contains("queryClient.removeQueries", boundary, StringComparison.Ordinal);
        Assert.Contains("isMarketplaceAuthorityInvalidation(event.data, scope)", boundary, StringComparison.Ordinal);
        Assert.Contains("invalidateQueries({ queryKey: marketplaceKeys.scope(authorityScope) })", chat, StringComparison.Ordinal);
        Assert.DoesNotContain("setQueryData", chat, StringComparison.Ordinal);
    }

    [Fact]
    public Task CertificationDocumentation_MustRecordPassFs4D() =>
        // Execute matching empty/nonempty evidence,lifecycle identity and provider
        // completion positives,plus missing/fabricated/foreign/terminal negatives.
        MarketplaceFrontendBehaviorContract.AssertAsync("src/marketplace/completionStateAuthority.test.ts", 3);

    private static string ReadRepoFile(string relativePath) => CertificationSource.ReadRepoFile(relativePath);
}
