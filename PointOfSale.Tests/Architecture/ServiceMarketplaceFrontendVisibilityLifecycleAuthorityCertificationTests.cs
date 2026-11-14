using PointOfSale.Core.Entities;
using PointOfSale.Core.Enums;
using PointOfSale.Infrastructure.Jobs;

namespace PointOfSale.Tests.Architecture;

public sealed class ServiceMarketplaceFrontendVisibilityLifecycleAuthorityCertificationTests
{
    [Fact]
    public void DiscoveryDetailAndRequestEntry_MustRequireCurrentLifecycleAuthority()
    {
        var search = ReadRepoFile(
            "PointOfSale.Infrastructure/Services/MarketplaceSearchService.cs");
        var catalog = ReadRepoFile(
            "PointOfSale.Infrastructure/Services/ServiceCatalogService.cs");
        var requests = ReadRepoFile(
            "PointOfSale.Infrastructure/Services/ServiceRequestService.cs");

        foreach (var source in new[] { search, catalog, requests })
        {
            Assert.Contains("TenantStatus.Active", source, StringComparison.Ordinal);
            Assert.Contains("UserGovernanceRole.Vendor", source, StringComparison.Ordinal);
            Assert.Contains("UserGovernanceStatus.Suspended", source, StringComparison.Ordinal);
            Assert.Contains("UserGovernanceStatus.Banned", source, StringComparison.Ordinal);
        }

        Assert.Contains("!service.IsDeleted", search, StringComparison.Ordinal);
        Assert.Contains("service.IsActive", search, StringComparison.Ordinal);
        Assert.Contains("!profile.IsDeleted", search, StringComparison.Ordinal);
        Assert.Contains("profile.IsActive", search, StringComparison.Ordinal);
        Assert.Contains("!service.IsDeleted", catalog, StringComparison.Ordinal);
        Assert.Contains("service.IsActive", catalog, StringComparison.Ordinal);
        Assert.Contains("!profile.IsDeleted", catalog, StringComparison.Ordinal);
        Assert.Contains("profile.IsActive", catalog, StringComparison.Ordinal);
    }

    [Fact]
    public void ProjectionRepairAndReindex_MustNotResurrectLifecycleInvalidServices()
    {
        var projection = ReadRepoFile(
            "PointOfSale.Infrastructure/Consumers/ServiceDiscoveryProjectionConsumer.cs");
        var drift = ReadRepoFile(
            "PointOfSale.Infrastructure/Jobs/ElasticsearchDriftReconciliationJob.cs");
        var controller = ReadRepoFile(
            "PointOfSale.API/Controllers/MarketplaceController.cs");

        Assert.Contains("TenantStatus.Active", projection, StringComparison.Ordinal);
        Assert.Contains("UserGovernanceRole.Vendor", projection, StringComparison.Ordinal);
        Assert.Contains("UserGovernanceStatus.Suspended", projection, StringComparison.Ordinal);
        Assert.Contains("UserGovernanceStatus.Banned", projection, StringComparison.Ordinal);

        // The repair now uses a fail-closed allow-list, so requiring the text of
        // two rejected enum values would reject the stronger implementation.
        // Execute its actual predicate and verify that convergence calls it.
        var tenant = new Tenant { Id = Guid.NewGuid(), Name = "fixture", IsActive = true, Status = TenantStatus.Active };
        var profile = MarketplaceProfile.Create(tenant.Id, Guid.NewGuid(), "fixture");
        var service = Service.Create(tenant.Id, profile.Id, "fixture", "description");
        var vendor = new UserGovernanceProfile { Id = Guid.NewGuid(), SubjectId = tenant.Id,
            Role = UserGovernanceRole.Vendor, CreatedAtUtc = DateTime.UtcNow };
        foreach (var status in Enum.GetValues<UserGovernanceStatus>().Append((UserGovernanceStatus)int.MaxValue))
        {
            vendor.Status = status;
            Assert.Equal(status is UserGovernanceStatus.Active or UserGovernanceStatus.FinanciallyLocked,
                ElasticsearchDriftReconciliationJob.IsLiveSqlLineage(service, tenant, profile, vendor));
        }
        vendor.Status = UserGovernanceStatus.Active;
        tenant.IsActive = false;
        Assert.False(ElasticsearchDriftReconciliationJob.IsLiveSqlLineage(service, tenant, profile, vendor));
        tenant.IsActive = true;
        profile.IsDeleted = true;
        Assert.False(ElasticsearchDriftReconciliationJob.IsLiveSqlLineage(service, tenant, profile, vendor));
        MarketplaceRepairAuthorityCallPath.AssertLiveVisibilityControlsConvergence();

        Assert.Contains("DeleteServiceDocumentAsync", projection, StringComparison.Ordinal);
        Assert.Contains("current SQL service/tenant/profile authority missing", projection, StringComparison.Ordinal);
        Assert.Contains("extraInEs", drift, StringComparison.Ordinal);
        Assert.Contains("ConvergeOneAsync(hit.Id", drift, StringComparison.Ordinal);
        Assert.Contains("IMarketplaceSearchRepairAuthority", controller, StringComparison.Ordinal);
        Assert.Contains("RepairServicesAsync(new(GetUserId(), GetTenantId(), null)", controller, StringComparison.Ordinal);
        Assert.DoesNotContain("_elasticClient.Indices.DeleteAsync", controller, StringComparison.Ordinal);
        Assert.DoesNotContain("BulkAsync", controller, StringComparison.Ordinal);
    }

    [Fact]
    public void FrontendCacheHistoryAndRealtime_MustRemainDerivative()
    {
        var boundary = ReadRepoFile(
            "PointOfSale.Web/src/marketplace/components/MarketplaceAuthorityBoundary.tsx");
        var discover = ReadRepoFile(
            "PointOfSale.Web/src/marketplace/pages/tabs/DiscoverTab.tsx");
        var detail = ReadRepoFile(
            "PointOfSale.Web/src/marketplace/pages/ServiceDetailsPage.tsx");
        var activate = ReadRepoFile(
            "PointOfSale.Web/src/marketplace/hooks/useActivateService.ts");
        var deactivate = ReadRepoFile(
            "PointOfSale.Web/src/marketplace/hooks/useDeactivateService.ts");

        Assert.Contains("AUTHORITY_REVALIDATION_INTERVAL_MS", boundary, StringComparison.Ordinal);
        Assert.Contains("isMarketplaceAuthorityInvalidation(event.data, scope)", boundary, StringComparison.Ordinal);
        Assert.Contains("queryClient.removeQueries", boundary, StringComparison.Ordinal);
        Assert.DoesNotContain("setQueryData", boundary, StringComparison.Ordinal);
        Assert.Contains("authorityFailed || isValidating ? undefined", discover, StringComparison.Ordinal);
        Assert.Contains("if (serviceAuthorityValidating)", detail, StringComparison.Ordinal);

        foreach (var mutation in new[] { activate, deactivate })
        {
            Assert.Contains("publishMarketplaceAuthorityInvalidation(authorityScope, serviceId)", mutation, StringComparison.Ordinal);
            Assert.Contains("marketplaceKeys.scope(authorityScope)", mutation, StringComparison.Ordinal);
            Assert.DoesNotContain("setQueryData", mutation, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task CertificationDocumentation_MustRecordPassFs1F()
    {
        // Actual inactive/incomplete identity and contradictory request-entry refusal;
        // actual rendered detail/history disappears during refresh,error or actor change.
        await MarketplaceFrontendBehaviorContract.AssertAsync("src/marketplace/serviceDetailAuthority.test.ts", 8);
        await MarketplaceFrontendBehaviorContract.AssertAsync("src/marketplace/pages/ServiceDetailsPage.test.tsx", 3);
    }

    private static string ReadRepoFile(string relativePath) =>
        CertificationSource.ReadRepoFile(relativePath);
}
