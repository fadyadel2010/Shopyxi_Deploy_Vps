using System.Reflection;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.EntityFrameworkCore;
using PointOfSale.API.Controllers;
using PointOfSale.API.DTOs.GeographicAdministration;
using PointOfSale.API.Services.Admin;
using PointOfSale.API.SuperAdmin.Models;
using PointOfSale.Core.Entities;
using PointOfSale.Core.Entities.Geographic;
using PointOfSale.Core.Enums;
using PointOfSale.Core.Security;
using PointOfSale.Core.SuperAdmin.Models;
using PointOfSale.Infrastructure.Data;

namespace PointOfSale.Tests.Shipping;

public sealed class GeographicPlatformAuthorityTests
{
    [Theory]
    [InlineData("Owner")]
    [InlineData("Admin")]
    [InlineData("Manager")]
    [InlineData("SuperAdmin")]
    public async Task RoleTextAloneCannotAuthorizeGlobalCatalogMutation(string role)
    {
        await using var db = Db();
        var actor = Guid.NewGuid();
        var accessor = new HttpContextAccessor { HttpContext = new DefaultHttpContext() };
        accessor.HttpContext.User = new(new ClaimsIdentity([
            new(ClaimTypes.NameIdentifier, actor.ToString()), new(ClaimTypes.Role, role),
            new("permission", PosPermissions.ShippingManage)], "tenant-jwt"));
        var service = new GeographicAdministrationService(db, accessor);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.CreateCatalogDraftAsync(Draft(), actor, role));
        Assert.Empty(db.GeographicCatalogReleases);
        Assert.Empty(db.GeographicAudit);
    }

    [Theory]
    [InlineData("stale-token")]
    [InlineData("banned")]
    [InlineData("missing-governance")]
    [InlineData("different-actor")]
    [InlineData("deleted")]
    public async Task MutationRevalidatesPersistedPlatformIdentity(string defect)
    {
        await using var db = Db();
        var actor = Guid.NewGuid();
        var accessor = await GeographicPlatformFixture.Authorize(db, actor, includeGovernance: defect != "missing-governance");
        var service = new GeographicAdministrationService(db, accessor);
        if (defect == "stale-token") (await db.SuperAdmins.SingleAsync()).ClearRefreshToken();
        if (defect == "banned") (await db.UserGovernanceProfiles.SingleAsync()).Status = UserGovernanceStatus.Banned;
        if (defect == "deleted") db.SuperAdmins.RemoveRange(db.SuperAdmins);
        await db.SaveChangesAsync();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.CreateCatalogDraftAsync(
            Draft(), defect == "different-actor" ? Guid.NewGuid() : actor, "Owner"));
        Assert.Empty(db.GeographicCatalogReleases);
        Assert.Empty(db.GeographicAudit);
    }

    [Fact]
    public async Task CurrentPlatformActorCanCreateGlobalDraftAndAuditIsPlatformScoped()
    {
        await using var db = Db();var actor = Guid.NewGuid();
        var accessor = await GeographicPlatformFixture.Authorize(db, actor);
        var result = await new GeographicAdministrationService(db, accessor).CreateCatalogDraftAsync(Draft(), actor, "Owner");
        Assert.True(result.Succeeded);
        var audit = Assert.Single(db.GeographicAudit);
        Assert.Equal(GeographicAuditActorKind.PlatformAdmin, audit.ActorKind);
        Assert.Equal(SuperAdminSecurityConstants.Role, audit.ActorRole);
        Assert.Null(audit.TenantId);
    }

    [Fact]
    public void EveryCatalogRouteUsesCentralPlatformPolicyWhileCarrierRoutesRetainTenantPolicy()
    {
        foreach (var method in typeof(AdminGeographicAdministrationController).GetMethods())
        {
            var routes = method.GetCustomAttributes<HttpMethodAttribute>().ToArray();
            if (routes.Length == 0) continue;
            var policies = method.GetCustomAttributes<AuthorizeAttribute>().ToArray();
            if (routes.Any(r => r.Template!.StartsWith("catalog/", StringComparison.Ordinal)))
            {
                Assert.Contains(policies, p => p.Policy == SuperAdminSecurityConstants.PolicyName);
                Assert.DoesNotContain(policies, p => p.Roles != null || p.Policy == PosPermissions.ShippingManage);
            }
            else Assert.Contains(policies, p => p.Policy == PosPermissions.ShippingManage && p.Roles == "Owner,Admin,Manager");
        }
    }

    private static ApplicationDbContext Db() => new(new DbContextOptionsBuilder<ApplicationDbContext>()
        .UseInMemoryDatabase("platform-catalog-" + Guid.NewGuid()).Options);
    private static CreateCatalogDraftRequest Draft() => new(1,"synthetic","US","Synthetic only","test");
}

internal static class GeographicPlatformFixture
{
    internal static async Task<GeographicTestHttpContextAccessor> Authorize(ApplicationDbContext db, Guid actor, bool includeGovernance = true)
    {
        if (!await db.SuperAdmins.AnyAsync(a => a.Id == actor))
        {
            var admin = SuperAdminUser.Create(actor + "@example.invalid", "synthetic", false, DateTime.UtcNow);
            db.Entry(admin).Property(a => a.Id).CurrentValue = actor;
            db.SuperAdmins.Add(admin);
            if (includeGovernance)
                db.UserGovernanceProfiles.Add(new UserGovernanceProfile { Id = Guid.NewGuid(), SubjectId = actor,
                    Role = UserGovernanceRole.Admin, Status = UserGovernanceStatus.Active, CreatedAtUtc = DateTime.UtcNow });
            await db.SaveChangesAsync();
        }
        var version = await db.SuperAdmins.Where(a => a.Id == actor).Select(a => a.TokenVersion).SingleAsync();
        return new GeographicTestHttpContextAccessor { HttpContext = new DefaultHttpContext { User = new(new ClaimsIdentity([
            new(ClaimTypes.NameIdentifier,actor.ToString()), new(ClaimTypes.Role,SuperAdminSecurityConstants.Role),
            new(SuperAdminSecurityConstants.AuthTypeClaim,SuperAdminSecurityConstants.AuthTypeValue),
            new(SuperAdminSecurityConstants.TokenVersionClaim,version.ToString())],SuperAdminSecurityConstants.AuthenticationScheme)) } };
    }
}

// Test identity is fixture-local, not AsyncLocal: setting the framework accessor in an
// awaited factory's ExecutionContext does not propagate to the caller's context.
internal sealed class GeographicTestHttpContextAccessor : IHttpContextAccessor
{
    public HttpContext? HttpContext { get; set; }
}
