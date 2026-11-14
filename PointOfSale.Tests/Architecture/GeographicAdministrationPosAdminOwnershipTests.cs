using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PointOfSale.API.Controllers;
using PointOfSale.API.Services.Admin;
using PointOfSale.API.SuperAdmin.Models;
using PointOfSale.Core.Security;
using PointOfSale.Infrastructure.Data;

namespace PointOfSale.Tests.Architecture;

public sealed class GeographicAdministrationPosAdminOwnershipTests
{
    [Fact]
    public void GeographicAdministration_MustNotBePlacedUnderSuperAdmin()
    {
        var root = FindRepoRoot(AppContext.BaseDirectory);
        var superAdminFiles = Directory.EnumerateFiles(Path.Combine(root, "PointOfSale.API", "SuperAdmin"), "*.cs", SearchOption.AllDirectories);

        foreach (var file in superAdminFiles)
        {
            var source = File.ReadAllText(file);
            Assert.DoesNotContain("GeographicAdministration", source, StringComparison.Ordinal);
            Assert.DoesNotContain("CarrierGeographic", source, StringComparison.Ordinal);
            Assert.DoesNotContain("GeographicCatalog", source, StringComparison.Ordinal);
            Assert.DoesNotContain("CountryProfile", source, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void GeographicAdministration_ControllerMustBePosAdminOwned()
    {
        var controller = typeof(AdminGeographicAdministrationController);
        Assert.Equal("api/admin/geographic", Assert.Single(controller.GetCustomAttributes(typeof(RouteAttribute), false).Cast<RouteAttribute>()).Template);
        foreach (var action in new[] { "CreateMappingDraft", "AddMapping", "PublishMappingRelease", "RollbackMappingRelease", "ValidateMappingDraft" })
            Assert.Contains(controller.GetMethod(action)!.GetCustomAttributes(typeof(AuthorizeAttribute), false).Cast<AuthorizeAttribute>(),
                a => a.Policy == PosPermissions.ShippingManage && a.Roles == "Owner,Admin,Manager");
        foreach (var action in new[] { "CreateCatalogDraft", "StageCatalogImport", "ValidateCatalogDraft", "PublishCatalogRelease" })
            Assert.Contains(controller.GetMethod(action)!.GetCustomAttributes(typeof(AuthorizeAttribute), false).Cast<AuthorizeAttribute>(),
                a => a.Policy == SuperAdminSecurityConstants.PolicyName && a.Roles == null);
    }

    [Fact]
    public async Task GlobalCatalogAuthority_CannotBeSuppliedByTenantRoleText()
    {
        await using var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase("catalog-no-implicit-authority-" + Guid.NewGuid()).Options);
        var service = new GeographicAdministrationService(db);
        foreach (var role in new[] { "Owner", "Admin", "Manager", SuperAdminSecurityConstants.Role })
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.CreateCatalogDraftAsync(
                new(1, "synthetic", "US", "synthetic", "test"), Guid.NewGuid(), role));
        Assert.Empty(db.GeographicCatalogReleases);
        Assert.Empty(db.GeographicAudit);
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

        throw new InvalidOperationException("Repository root could not be found.");
    }
}
