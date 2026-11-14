using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using PointOfSale.API.Security;
using PointOfSale.Core.Enums;
using PointOfSale.Infrastructure.Data;

namespace PointOfSale.API.Middleware;

/// <summary>Unverified POS Owners have onboarding access, not merchant-operation authority.</summary>
public sealed class OwnerEmailVerificationMiddleware(RequestDelegate next)
{
    public static bool IsOnboardingRequest(string method, PathString path) =>
        (HttpMethods.IsGet(method) &&
            (path.Equals("/api/auth/session", StringComparison.OrdinalIgnoreCase) ||
             path.Equals("/api/auth/verification-status", StringComparison.OrdinalIgnoreCase))) ||
        (HttpMethods.IsPost(method) &&
            (path.Equals("/api/auth/logout", StringComparison.OrdinalIgnoreCase) ||
             path.Equals("/api/auth/refresh", StringComparison.OrdinalIgnoreCase) ||
             path.Equals("/api/auth/resend-verification", StringComparison.OrdinalIgnoreCase)));

    public async Task InvokeAsync(HttpContext context, ApplicationDbContext db)
    {
        // Independent authentication schemes retain their existing authority.
        if (!context.User.HasClaim(PosSecurityConstants.SqlAuthorityClaim, PosSecurityConstants.SqlAuthorityValue) ||
            !context.User.IsInRole(nameof(UserRole.Owner)) ||
            context.GetEndpoint()?.Metadata.GetMetadata<Microsoft.AspNetCore.Authorization.IAllowAnonymous>() != null ||
            IsOnboardingRequest(context.Request.Method, context.Request.Path))
        {
            await next(context);
            return;
        }

        var userId = context.User.FindFirstValue(ClaimTypes.NameIdentifier);
        var tenantId = context.User.FindFirstValue("tenant_id");
        var verified = Guid.TryParse(userId, out var actor) && Guid.TryParse(tenantId, out var tenant) &&
            await db.Users.IgnoreQueryFilters().AsNoTracking().AnyAsync(u =>
                u.Id == actor && u.TenantId == tenant && u.Role == UserRole.Owner &&
                u.IsActive && !u.IsDeleted && u.EmailVerificationStatus == EmailVerificationStatus.Verified,
                context.RequestAborted);

        if (verified)
        {
            await next(context);
            return;
        }

        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        context.Response.Headers.CacheControl = "private, no-store, max-age=0";
        await context.Response.WriteAsJsonAsync(new
        {
            code = "EMAIL_VERIFICATION_REQUIRED",
            message = "Verify your email address before accessing your store."
        }, context.RequestAborted);
    }
}
