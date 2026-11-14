namespace PointOfSale.Tests.Architecture;

// Source-wiring guards, not authorization/provider execution. Actual hooks and
// workflow outcomes are exercised by frontend tests, not inferred from these strings.
internal static class MarketplaceRequestCommandCompositionContract
{
    internal static string IncomingCommands()
    {
        var tab = CertificationSource.ReadRepoFile("PointOfSale.Web/src/marketplace/pages/tabs/IncomingTab.tsx");
        Assert.Contains("import { useIncomingRequestCommands }", tab, StringComparison.Ordinal);
        Assert.Contains("useIncomingRequestCommands(currentUserId)", tab, StringComparison.Ordinal);
        Assert.Contains("acceptMutation.mutateAsync(requestId)", tab, StringComparison.Ordinal);
        Assert.Contains("rejectMutation.mutateAsync(requestId)", tab, StringComparison.Ordinal);
        Assert.Contains("markCompletedMutation.mutateAsync(requestId)", tab, StringComparison.Ordinal);
        Assert.DoesNotContain("setQueryData", tab, StringComparison.Ordinal);
        return CertificationSource.ReadRepoFile("PointOfSale.Web/src/marketplace/hooks/useIncomingRequestCommands.ts");
    }

    internal static string BuyerCommands()
    {
        var tab = CertificationSource.ReadRepoFile("PointOfSale.Web/src/marketplace/pages/tabs/MyRequestsTab.tsx");
        Assert.Contains("import { useBuyerRequestCommands }", tab, StringComparison.Ordinal);
        Assert.Contains("useBuyerRequestCommands(currentUserId)", tab, StringComparison.Ordinal);
        Assert.Contains("confirmCompletionMutation.mutateAsync(requestId)", tab, StringComparison.Ordinal);
        Assert.Contains("submitReviewMutation.mutateAsync(review)", tab, StringComparison.Ordinal);
        Assert.DoesNotContain("setQueryData", tab, StringComparison.Ordinal);
        return CertificationSource.ReadRepoFile("PointOfSale.Web/src/marketplace/hooks/useBuyerRequestCommands.ts");
    }

    internal static void AssertRatingStateDelegation()
    {
        var eligibility = CertificationSource.ReadRepoFile("PointOfSale.Web/src/marketplace/ratingEligibility.ts");
        var state = CertificationSource.ReadRepoFile("PointOfSale.Web/src/marketplace/ratingStateAuthority.ts");
        Assert.Contains("import { deriveServiceRequestRatingStatePresentation }", eligibility, StringComparison.Ordinal);
        Assert.Contains("const ratingState = deriveServiceRequestRatingStatePresentation(", eligibility, StringComparison.Ordinal);
        Assert.Contains("return ratingState ? {", eligibility, StringComparison.Ordinal);
        Assert.Contains("canRate: ratingState.canRate", eligibility, StringComparison.Ordinal);
        Assert.Contains("existingReviewId: ratingState.reviewId", eligibility, StringComparison.Ordinal);
        Assert.Contains("actorUserId !== buyerUserId", state, StringComparison.Ordinal);
        Assert.Contains("buyerUserId === providerUserId", state, StringComparison.Ordinal);
        Assert.Contains("lifecycle.statusKey !== 'completedByBuyer'", state, StringComparison.Ordinal);
        Assert.Contains("typeof authority.canReview !== 'boolean'", state, StringComparison.Ordinal);
        Assert.Contains("if (!lifecycle.canReview || reviewId !== null)", state, StringComparison.Ordinal);
        Assert.Contains("if (lifecycle.canReview || reviewId === null)", state, StringComparison.Ordinal);
        Assert.Contains("canRate: true", state, StringComparison.Ordinal);
        Assert.Contains("canRate: false", state, StringComparison.Ordinal);
    }
}
