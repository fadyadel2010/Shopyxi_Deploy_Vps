using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using PointOfSale.Core.Enums;
using PointOfSale.Infrastructure.Data;
using PointOfSale.Infrastructure.Instrumentation;
using PointOfSale.Infrastructure.Reliability;

namespace PointOfSale.API.Security;

public static class PosSecurityConstants
{
    public const string SessionBindingClaim = "pos_session";
    public const string SqlAuthorityClaim = "pos_sql_authority";
    public const string SqlAuthorityValue = "current";
}

public sealed record PosSessionAuthorityResult(bool Authorized, string Reason, Guid? UserId = null, Guid? TenantId = null)
{
    public static PosSessionAuthorityResult Allow(Guid userId, Guid tenantId) =>
        new(true, "current_sql_pos_session_authority", userId, tenantId);

    public static PosSessionAuthorityResult Deny(string reason, Guid? userId = null, Guid? tenantId = null) =>
        new(false, reason, userId, tenantId);
}

public interface IPosSessionAuthority
{
    Task<PosSessionAuthorityResult> ValidateAccessTokenAsync(
        ClaimsPrincipal principal,
        CancellationToken cancellationToken);
}

/// <summary>
/// Revalidates a POS access token against its current SQL session and actor scope.
/// Access-token claims identify the candidate session only; current SQL state decides access.
/// </summary>
public sealed class PosSessionAuthority : IPosSessionAuthority
{
    private readonly ApplicationDbContext _dbContext;

    public PosSessionAuthority(ApplicationDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public Task<PosSessionAuthorityResult> ValidateAccessTokenAsync(
        ClaimsPrincipal principal,
        CancellationToken cancellationToken) => ValidateAccessTokenCoreAsync(principal, false, cancellationToken);

    // Internal service mutation boundaries call this after acquiring authority
    // locks. A middleware read or request memo is not durable commit authority.
    internal Task<PosSessionAuthorityResult> ValidateFreshAccessTokenAsync(
        ClaimsPrincipal principal, CancellationToken cancellationToken) =>
        ValidateAccessTokenCoreAsync(principal, true, cancellationToken);

    internal static async Task HoldTenantGovernanceAsync(ApplicationDbContext db, Guid tenantId, CancellationToken ct)
    {
        if (db.Database.CurrentTransaction == null || tenantId == Guid.Empty)
            throw new InvalidOperationException("POS mutation governance requires the owned tenant transaction.");
        if (!await TenantLineageAuthority.IsCurrentActiveTenantAsync(db, tenantId, ct)) return;
        // Absence previously meant the default Active/uncertified policy. Give
        // that default a durable identity before locking it: a concurrent first
        // ban must conflict with this unique (Role, SubjectId), not insert through
        // the gap in an empty SELECT FOR SHARE. Never update an existing policy,
        // grant verification/marketplace approval, or commit separately.
        var id = Guid.NewGuid(); var now = DateTime.UtcNow;
        if (db.Database.IsNpgsql())
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO "UserGovernanceProfiles"
                    ("Id","SubjectId","Role","Status","RiskLevel","IsKycVerified","IsMarketplaceApproved","SuspiciousFlags","CreatedAtUtc")
                VALUES ({id},{tenantId},{(int)UserGovernanceRole.Vendor},{(int)UserGovernanceStatus.Active},{(int)UserGovernanceRiskLevel.Low},FALSE,FALSE,0,{now})
                ON CONFLICT ("Role","SubjectId") DO NOTHING
                """, ct);
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT 1 FROM \"UserGovernanceProfiles\" WHERE \"SubjectId\"={tenantId} AND \"Role\"={(int)UserGovernanceRole.Vendor} FOR SHARE", ct);
        }
        else if (string.Equals(db.Database.ProviderName, "Microsoft.EntityFrameworkCore.Sqlite", StringComparison.Ordinal))
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT OR IGNORE INTO "UserGovernanceProfiles"
                    ("Id","SubjectId","Role","Status","RiskLevel","IsKycVerified","IsMarketplaceApproved","SuspiciousFlags","CreatedAtUtc")
                VALUES ({id},{tenantId},{(int)UserGovernanceRole.Vendor},{(int)UserGovernanceStatus.Active},{(int)UserGovernanceRiskLevel.Low},FALSE,FALSE,0,{now})
                """, ct);
        }
        else throw new NotSupportedException("POS mutation governance requires PostgreSQL or the relational SQLite contract provider.");
    }

    private async Task<PosSessionAuthorityResult> ValidateAccessTokenCoreAsync(
        ClaimsPrincipal principal, bool freshAuthority, CancellationToken cancellationToken)
    {
        if (!TryGetGuidClaim(principal, ClaimTypes.NameIdentifier, out var userId) ||
            !TryGetGuidClaim(principal, "tenant_id", out var tokenTenantId))
        {
            return Deny("invalid_actor_or_tenant_claim");
        }

        var tokenRole = principal.FindFirstValue(ClaimTypes.Role);
        var sessionBinding = principal.FindFirstValue(PosSecurityConstants.SessionBindingClaim);
        if (string.IsNullOrWhiteSpace(tokenRole) || string.IsNullOrWhiteSpace(sessionBinding))
        {
            return Deny("missing_pos_session_binding", userId, tokenTenantId);
        }

        var actor = await _dbContext.Users
            .AsNoTracking()
            .IgnoreQueryFilters()
            .Where(user => user.Id == userId)
            .Select(user => new
            {
                user.TenantId,
                user.Role,
                user.IsActive,
                user.IsDeleted,
                user.RefreshToken,
                user.RefreshTokenExpiryTime
            })
            .FirstOrDefaultAsync(cancellationToken);

        if (actor == null)
        {
            return Deny("missing_current_actor_sql_authority", userId, tokenTenantId);
        }

        if (actor.IsDeleted || !actor.IsActive)
        {
            return Deny("inactive_or_deleted_actor", userId, actor.TenantId);
        }

        if (actor.TenantId != tokenTenantId)
        {
            return Deny("current_actor_tenant_mismatch", userId, tokenTenantId);
        }

        if (!string.Equals(actor.Role.ToString(), tokenRole, StringComparison.OrdinalIgnoreCase))
        {
            return Deny("stale_actor_role_claim", userId, actor.TenantId);
        }

        var now = freshAuthority && _dbContext.Database.IsNpgsql()
            ? await _dbContext.Database.SqlQuery<DateTime>($"SELECT clock_timestamp() AS \"Value\"").SingleAsync(cancellationToken)
            : DateTime.UtcNow;
        if (actor.RefreshTokenExpiryTime is null ||
            actor.RefreshTokenExpiryTime <= now ||
            !MatchesCurrentSession(sessionBinding, actor.RefreshToken))
        {
            return Deny("revoked_or_rotated_pos_session", userId, actor.TenantId);
        }

        var currentTenant = freshAuthority
            ? await TenantLineageAuthority.IsCurrentActiveTenantAsync(_dbContext, actor.TenantId, cancellationToken)
            : await TenantLineageAuthority.IsActiveTenantAsync(_dbContext, actor.TenantId, cancellationToken);
        if (!currentTenant)
        {
            return Deny("tenant_inactive_or_deleted", userId, actor.TenantId);
        }

        var governanceStatus = await _dbContext.UserGovernanceProfiles
            .AsNoTracking()
            .Where(profile =>
                profile.SubjectId == actor.TenantId &&
                profile.Role == UserGovernanceRole.Vendor)
            .OrderByDescending(profile => profile.UpdatedAtUtc ?? profile.CreatedAtUtc)
            .Select(profile => (UserGovernanceStatus?)profile.Status)
            .FirstOrDefaultAsync(cancellationToken);

        if (governanceStatus == UserGovernanceStatus.Banned || governanceStatus == UserGovernanceStatus.Suspended)
        {
            return Deny("tenant_governance_banned", userId, actor.TenantId);
        }

        return PosSessionAuthorityResult.Allow(userId, actor.TenantId);
    }

    private static bool TryGetGuidClaim(ClaimsPrincipal principal, string claimType, out Guid value)
    {
        var claimValue = principal.FindFirstValue(claimType);
        return Guid.TryParse(claimValue, out value) && value != Guid.Empty;
    }

    private static bool MatchesCurrentSession(string presentedBinding, string? currentBinding)
    {
        if (string.IsNullOrWhiteSpace(currentBinding))
        {
            return false;
        }

        var presentedBytes = Encoding.UTF8.GetBytes(presentedBinding);
        var currentBytes = Encoding.UTF8.GetBytes(currentBinding);
        return presentedBytes.Length == currentBytes.Length &&
               CryptographicOperations.FixedTimeEquals(presentedBytes, currentBytes);
    }

    private static PosSessionAuthorityResult Deny(string reason, Guid? userId = null, Guid? tenantId = null)
    {
        OperationalConvergenceTelemetry.RecordAuthorityFailure(
            "pos_session_authority",
            OperationalConvergenceAuthority.PosSession,
            reason switch
            {
                "revoked_or_rotated_pos_session" => OperationalConvergenceReason.SessionRevokedOrRotated,
                "stale_actor_role_claim" => OperationalConvergenceReason.StaleActorRole,
                "tenant_inactive_or_deleted" => OperationalConvergenceReason.TenantInactiveOrDeleted,
                "tenant_governance_banned" => OperationalConvergenceReason.GovernanceRestricted,
                _ => OperationalConvergenceReason.ActorScopeRejected
            },
            tenantId);

        return PosSessionAuthorityResult.Deny(reason, userId, tenantId);
    }
}
