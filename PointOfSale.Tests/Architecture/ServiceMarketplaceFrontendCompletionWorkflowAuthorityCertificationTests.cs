namespace PointOfSale.Tests.Architecture;

public sealed class ServiceMarketplaceFrontendCompletionWorkflowAuthorityCertificationTests
{
    [Fact]
    public void CompletionExecution_MustBeScopedDeduplicatedAndBackendRevalidated()
    {
        var workflow = ReadRepoFile("PointOfSale.Web/src/marketplace/completionWorkflow.ts");
        var markCompleted = ReadRepoFile("PointOfSale.Web/src/marketplace/hooks/useMarkCompleted.ts");
        var confirmCompletion = ReadRepoFile("PointOfSale.Web/src/marketplace/hooks/useConfirmCompletion.ts");
        var incoming = ReadRepoFile("PointOfSale.Web/src/marketplace/pages/tabs/IncomingTab.tsx");
        var myRequests = ReadRepoFile("PointOfSale.Web/src/marketplace/pages/tabs/MyRequestsTab.tsx");

        Assert.Contains("completionExecutions", workflow, StringComparison.Ordinal);
        Assert.Contains("Completion workflow already in progress", workflow, StringComparison.Ordinal);
        Assert.Contains("authorityScope !== getCurrentAuthorityScope()", workflow, StringComparison.Ordinal);
        Assert.Contains("deriveServiceRequestLifecycleMutationAuthority(response, kind)", workflow, StringComparison.Ordinal);
        Assert.Contains("await revalidate()", workflow, StringComparison.Ordinal);
        Assert.Contains("deriveServiceRequestCompletionOutcome", workflow, StringComparison.Ordinal);
        Assert.Contains("authoritativeActorId !== currentActorId", workflow, StringComparison.Ordinal);
        Assert.Contains("authoritativeRequestId !== expectedRequestId", workflow, StringComparison.Ordinal);

        var incomingCommands = MarketplaceRequestCommandCompositionContract.IncomingCommands();
        var buyerCommands = MarketplaceRequestCommandCompositionContract.BuyerCommands();
        foreach (var source in new[] { markCompleted, confirmCompletion, incomingCommands, buyerCommands })
        {
            Assert.Contains("executeServiceRequestCompletionWorkflow", source, StringComparison.Ordinal);
            Assert.Contains("marketplaceApi.getRequestDetails(requestId)", source, StringComparison.Ordinal);
            Assert.Contains("publishMarketplaceAuthorityInvalidation(authorityScope)", source, StringComparison.Ordinal);
            Assert.Contains("marketplaceKeys.scope(authorityScope)", source, StringComparison.Ordinal);
            Assert.DoesNotContain("setQueryData", source, StringComparison.Ordinal);
            Assert.DoesNotContain("onMutate", source, StringComparison.Ordinal);
            Assert.Contains("retry: false", source, StringComparison.Ordinal);
            Assert.Contains("networkMode: 'online'", source, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void CompletionWorkflow_MustKeepCacheRealtimeHistoryAndActorStateDerivative()
    {
        var authority = ReadRepoFile("PointOfSale.Web/src/marketplace/authority.ts");
        var boundary = ReadRepoFile("PointOfSale.Web/src/marketplace/components/MarketplaceAuthorityBoundary.tsx");
        var queryKeys = ReadRepoFile("PointOfSale.Web/src/marketplace/queryKeys.ts");
        var chat = ReadRepoFile("PointOfSale.Web/src/marketplace/hooks/useChat.ts");

        Assert.Contains("tenantId", authority, StringComparison.Ordinal);
        Assert.Contains("userId", authority, StringComparison.Ordinal);
        Assert.Contains("role !== 'superadmin'", authority, StringComparison.Ordinal);
        Assert.Contains("scope: (scope = getMarketplaceAuthorityScope())", queryKeys, StringComparison.Ordinal);
        Assert.Contains("queryClient.removeQueries", boundary, StringComparison.Ordinal);
        Assert.Contains("isMarketplaceAuthorityInvalidation(event.data, scope)", boundary, StringComparison.Ordinal);
        Assert.Contains("queryClient.invalidateQueries({ queryKey: marketplaceKeys.scope(authorityScope) })", chat, StringComparison.Ordinal);
        Assert.DoesNotContain("setQueryData", chat, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CertificationDocumentation_MustRecordPassFs4B()
    {
        // Execute actual workflow evidence/timeout/actor/duplicate arbitration and
        // all four real command-hook surfaces under hostile inherited defaults.
        await MarketplaceFrontendBehaviorContract.AssertAsync("src/marketplace/completionWorkflow.test.ts", 5);
        await MarketplaceFrontendBehaviorContract.AssertAsync("src/marketplace/hooks/completionCommandHooks.test.tsx", 16);
    }

    private static string ReadRepoFile(string relativePath) => CertificationSource.ReadRepoFile(relativePath);
}
