using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using PointOfSale.Core.Security;

namespace PointOfSale.API.Security;

public sealed class PosPermissionAuthorizationHandler
    : AuthorizationHandler<PosPermissionRequirement>
{
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        PosPermissionRequirement requirement)
    {
        if (HasPermission(context.User, requirement.Permission))
        {
            context.Succeed(requirement);
        }
        return Task.CompletedTask;
    }

    // Shared by field-level projections; identical trust requirements to endpoint admission.
    public static bool HasPermission(ClaimsPrincipal? user, string permission)
    {
        if (!PosPermissions.All.Contains(permission) || user?.Identity?.IsAuthenticated != true)
            return false;
        var roleClaims = user.FindAll(ClaimTypes.Role).Select(claim => claim.Value).ToArray();
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        var tenantId = user.FindFirstValue("tenant_id");
        var sessionBinding = user.FindFirstValue(PosSecurityConstants.SessionBindingClaim);
        var sqlAuthorityClaims = user.FindAll(PosSecurityConstants.SqlAuthorityClaim).ToArray();

        // These claims are minted only by the POS bearer issuer and are revalidated
        // against current SQL state by PosSessionAuthority before authorization.
        if (roleClaims.Length != 1 ||
            sqlAuthorityClaims.Length != 1 ||
            !string.Equals(sqlAuthorityClaims[0].Value, PosSecurityConstants.SqlAuthorityValue, StringComparison.Ordinal) ||
            !Guid.TryParse(userId, out var parsedUserId) || parsedUserId == Guid.Empty ||
            !Guid.TryParse(tenantId, out var parsedTenantId) || parsedTenantId == Guid.Empty ||
            string.IsNullOrWhiteSpace(sessionBinding) ||
            !PosRolePermissionCatalog.TryParseKnownRole(roleClaims[0], out var role))
        {
            return false;
        }

        return PosRolePermissionCatalog.IsGranted(role, permission);
    }
}
