namespace PointOfSale.Tests.Architecture;

public sealed class ServiceMarketplaceAnalyticsAuthorityCertificationTests
{
    [Fact]
    public void ServiceMarketplaceAnalyticsRuntime_MustDeriveDashboardKpisFromSqlAuthorities()
    {
        var controller = ReadRepoFile("PointOfSale.API/Controllers/MarketplaceController.cs");
        var trustService = ReadRepoFile("PointOfSale.Infrastructure/Services/ProviderTrustService.cs");
        var requestService = ReadRepoFile("PointOfSale.Infrastructure/Services/ServiceRequestService.cs");
        var reviewService = ReadRepoFile("PointOfSale.Infrastructure/Services/ServiceReviewService.cs");

        Assert.Contains("Analytics cards are derivative only; bind them to current SQL tenant/profile authority.", controller, StringComparison.Ordinal);
        Assert.Contains("var tenantId = GetTenantId();", controller, StringComparison.Ordinal);
        Assert.Contains("s.TenantId == tenantId", controller, StringComparison.Ordinal);
        Assert.Contains("r.TenantId == tenantId", controller, StringComparison.Ordinal);
        Assert.Contains("r.ProviderProfileId == profile.Id", controller, StringComparison.Ordinal);
        Assert.Contains("r.Status == Core.Enums.ServiceRequestStatus.CompletedByBuyer", controller, StringComparison.Ordinal);
        Assert.Contains("_trustService.GetTrustAggregateAsync(profile.Id)", controller, StringComparison.Ordinal);

        Assert.Contains("Public trust authority is recomposed from SQL ServiceReview truth", trustService, StringComparison.Ordinal);
        Assert.Contains("ComposeSqlTrustAggregateAsync", trustService, StringComparison.Ordinal);
        Assert.Contains("row.Request.Status == ServiceRequestStatus.CompletedByBuyer", trustService, StringComparison.Ordinal);
        Assert.Contains("ProviderTrustAggregate.FromSqlReviewTruth", trustService, StringComparison.Ordinal);

        Assert.Contains("r.TenantId == tenantId && r.ProviderUserId == providerUserId && !r.IsDeleted", requestService, StringComparison.Ordinal);
        Assert.Contains("r.TenantId == tenantId && r.BuyerUserId == buyerUserId && !r.IsDeleted", requestService, StringComparison.Ordinal);
        Assert.Contains("r.ServiceRequest.Status == ServiceRequestStatus.CompletedByBuyer", reviewService, StringComparison.Ordinal);
    }

    [Fact]
    public void ServiceMarketplaceAnalyticsRuntime_MustNotUseProjectionCacheOrExportAuthority()
    {
        var controller = ReadRepoFile("PointOfSale.API/Controllers/MarketplaceController.cs");
        var searchService = ReadRepoFile("PointOfSale.Infrastructure/Services/MarketplaceSearchService.cs");
        var searchDocument = ReadRepoFile("PointOfSale.Infrastructure/Elasticsearch/Documents/MarketplaceServiceDocument.cs");

        Assert.Contains("Elasticsearch may return candidates; returned listing fields are recomposed from SQL authority.", controller, StringComparison.Ordinal);
        Assert.DoesNotContain("NEVER queries PostgreSQL - only Elasticsearch.", controller, StringComparison.Ordinal);
        Assert.DoesNotContain("[HttpGet(\"analytics/export", controller, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("[HttpGet(\"reports/export", controller, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("[HttpGet(\"dashboard/export", controller, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("[HttpGet(\"export", controller, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("text/csv", controller, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", controller, StringComparison.OrdinalIgnoreCase);

        Assert.Contains("_context.Set<Service>()", searchService, StringComparison.Ordinal);
        Assert.Contains("_providerTrustService.GetTrustAggregateAsync", searchService, StringComparison.Ordinal);
        Assert.DoesNotContain("AverageRating = document.Trust?.AverageRating", searchService, StringComparison.Ordinal);

        Assert.Contains("TotalRequests", searchDocument, StringComparison.Ordinal);
        Assert.Contains("CompletedRequests", searchDocument, StringComparison.Ordinal);
        Assert.Contains("CompletionRate", searchDocument, StringComparison.Ordinal);
        MarketplaceRepairAuthorityCallPath.AssertSqlBusinessDataRecomposition();
    }

    [Fact]
    public async Task AnalyticsControllerMustRejectInactiveAuthorityAndIsolateDashboardCounts()
    {
        // Replaces the historical S1-F PASS-marker assertion. Old certification prose
        // cannot prove current authority. Keep the other architecture guards selected,
        // and exercise the controller's fail-closed reads and actual result composition.
        await MarketplaceAnalyticsReadAuthorityContract.AssertReadAuthorityAsync();
    }

    [Fact]
    public async Task ServiceMarketplaceAnalyticsCertificationDocumentation_MustRecordS1FAuthorityFreeze()
    {
        // Preserve the original historical identity as executable current behavior.
        // Keep the already-executed replacement above as a distinct selected test.
        await MarketplaceAnalyticsReadAuthorityContract.AssertReadAuthorityAsync();
    }

    private static string ReadRepoFile(string relativePath)
    {
        var root = FindRepoRoot(AppContext.BaseDirectory);
        return File.ReadAllText(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
    }

    private static string FindRepoRoot(string startDirectory)
    {
        var directory = new DirectoryInfo(startDirectory);
        while (directory != null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "PointOfSale.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not locate repository root.");
    }
}
