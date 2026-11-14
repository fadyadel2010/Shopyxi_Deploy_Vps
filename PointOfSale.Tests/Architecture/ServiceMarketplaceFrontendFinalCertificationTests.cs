namespace PointOfSale.Tests.Architecture;

public sealed class ServiceMarketplaceFrontendFinalCertificationTests
{
    [Fact]
    public void RuntimeAuthorityGraph_MustRouteMarketplaceTruthThroughCertifiedBoundaries()
    {
        var boundary = ReadRepoFile("PointOfSale.Web/src/marketplace/components/MarketplaceAuthorityBoundary.tsx");
        var queryKeys = ReadRepoFile("PointOfSale.Web/src/marketplace/queryKeys.ts");
        var shell = ReadRepoFile("PointOfSale.Web/src/marketplace/pages/MarketplaceShell.tsx");
        var profileTab = ReadRepoFile("PointOfSale.Web/src/marketplace/pages/tabs/ProfileTab.tsx");
        var myServicesTab = ReadRepoFile("PointOfSale.Web/src/marketplace/pages/tabs/MyServicesTab.tsx");
        var myRequestsTab = ReadRepoFile("PointOfSale.Web/src/marketplace/pages/tabs/MyRequestsTab.tsx");
        var incomingTab = ReadRepoFile("PointOfSale.Web/src/marketplace/pages/tabs/IncomingTab.tsx");

        Assert.Contains("marketplaceKeys.scope(scope)", queryKeys, StringComparison.Ordinal);
        Assert.Contains("queryClient.removeQueries", boundary, StringComparison.Ordinal);
        Assert.Contains("<Navigate to=\"/\" replace />", boundary, StringComparison.Ordinal);
        Assert.Contains("profileAuthorityFailed", shell, StringComparison.Ordinal);
        Assert.Contains("deriveProviderProfilePresentation", profileTab, StringComparison.Ordinal);
        Assert.Contains("deriveProviderServicePresentation", myServicesTab, StringComparison.Ordinal);
        Assert.Contains("deriveServiceRequestLifecyclePresentation", myRequestsTab, StringComparison.Ordinal);
        Assert.Contains("deriveServiceRequestCompletionStatePresentation", myRequestsTab, StringComparison.Ordinal);
        Assert.Contains("deriveServiceRequestLifecyclePresentation", incomingTab, StringComparison.Ordinal);
        Assert.Contains("deriveServiceRequestCompletionStatePresentation", incomingTab, StringComparison.Ordinal);
    }

    [Fact]
    public void AuthorityOwnership_MustNotAllowCacheRealtimeHistoryOrMutationsToBecomeAuthority()
    {
        var authority = ReadRepoFile("PointOfSale.Web/src/marketplace/authority.ts");
        var chatHook = ReadRepoFile("PointOfSale.Web/src/marketplace/hooks/useChat.ts");
        var profileTab = ReadRepoFile("PointOfSale.Web/src/marketplace/pages/tabs/ProfileTab.tsx");
        var myServicesTab = ReadRepoFile("PointOfSale.Web/src/marketplace/pages/tabs/MyServicesTab.tsx");

        Assert.Contains("staleTime: 0", authority, StringComparison.Ordinal);
        Assert.Contains("retry: false", authority, StringComparison.Ordinal);
        Assert.Contains("refetchOnMount: 'always'", authority, StringComparison.Ordinal);
        Assert.Contains("isCurrentChatRealtimeEvent(message, identity)", chatHook, StringComparison.Ordinal);
        Assert.Contains("queryClient.resetQueries", chatHook, StringComparison.Ordinal);
        Assert.DoesNotContain("setQueryData", chatHook, StringComparison.Ordinal);
        Assert.Contains("import { useMarketplaceProfileEditor, useMarketplaceProfileStats }", profileTab, StringComparison.Ordinal);
        Assert.Contains("useMarketplaceProfileEditor(", profileTab, StringComparison.Ordinal);
        Assert.Contains("createProfileMutation.mutateAsync(data)", profileTab, StringComparison.Ordinal);
        Assert.Contains("updateProfileMutation.mutateAsync(data)", profileTab, StringComparison.Ordinal);
        Assert.Contains("import { useMarketplaceServiceEditor }", myServicesTab, StringComparison.Ordinal);
        Assert.Contains("useMarketplaceServiceEditor(", myServicesTab, StringComparison.Ordinal);
        Assert.Contains("createServiceMutation.mutateAsync(data)", myServicesTab, StringComparison.Ordinal);
        Assert.Contains("updateServiceMutation.mutateAsync(", myServicesTab, StringComparison.Ordinal);
        foreach (var source in new[] {
            ReadRepoFile("PointOfSale.Web/src/marketplace/hooks/useMarketplaceProfileEditor.ts"),
            ReadRepoFile("PointOfSale.Web/src/marketplace/hooks/useMarketplaceServiceEditor.ts") })
        {
            Assert.Contains("publishMarketplaceAuthorityInvalidation(authorityScope)", source, StringComparison.Ordinal);
            Assert.Contains("invalidateQueries({ queryKey: marketplaceKeys.scope(authorityScope) })", source, StringComparison.Ordinal);
            Assert.DoesNotContain("setQueryData", source, StringComparison.Ordinal);
            Assert.DoesNotContain("onMutate", source, StringComparison.Ordinal);
            Assert.Contains("retry: false", source, StringComparison.Ordinal);
            Assert.Contains("networkMode: 'online'", source, StringComparison.Ordinal);
            Assert.Contains("getMarketplaceAuthorityScope() !== authorityScope", source, StringComparison.Ordinal);
            Assert.Contains("authorityScope === 'ineligible'", source, StringComparison.Ordinal);
            // Both create and update commands check before I/O and after response.
            Assert.Equal(4, source.Split("requireCurrentAuthority()", StringSplitOptions.None).Length - 1);
        }
        Assert.DoesNotContain("setQueryData", profileTab, StringComparison.Ordinal);
        Assert.DoesNotContain("setQueryData", myServicesTab, StringComparison.Ordinal);
    }

    [Fact]
    public void FinalCertificationDocumentation_MustRecordPassFsx()
    {
        var certification = ReadRepoFile("docs/architecture/service-marketplace-frontend-final-certification-findings.md");

        foreach (var marker in new[]
        {
            "PASS F-SX",
            "## Authority Graph Findings",
            "## Cross-Family Findings",
            "## Ownership Findings",
            "## Tenant Findings",
            "## Actor Findings",
            "## Boundary Findings",
            "## Freeze Findings",
            "## Failure-Model Findings",
            "## Residual-Risk Findings",
            "## Adversarial Findings",
            "## Runtime Hardening Performed",
            "## Exact Runtime Files Modified",
            "## Regression Freezes Added",
            "## Architecture Updates",
            "## Explicit Answers"
        })
        {
            Assert.Contains(marker, certification, StringComparison.Ordinal);
        }
    }

    private static string ReadRepoFile(string relativePath) => CertificationSource.ReadRepoFile(relativePath);
}
