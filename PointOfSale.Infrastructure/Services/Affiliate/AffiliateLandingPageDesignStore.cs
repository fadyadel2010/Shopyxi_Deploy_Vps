using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using PointOfSale.Core.Constants;
using PointOfSale.Core.Entities.Affiliate;
using PointOfSale.Core.Enums;
using PointOfSale.Infrastructure.Data;

namespace PointOfSale.Infrastructure.Services.Affiliate;

/// <summary>Presentation-only persistence with SQL authority rechecked at the write.</summary>
public static class AffiliateLandingPageDesignStore
{
    public static async Task SaveAsync(ApplicationDbContext db, Guid accountId, Guid tenantId, Guid productId, string template, ClaimsPrincipal actor, CancellationToken ct)
    {
        if (!LandingTemplates.Allowed.Contains(template)) throw new ArgumentException("Unsupported template.", nameof(template));
        if (actor.Identity?.IsAuthenticated != true ||
            !Guid.TryParse(actor.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? actor.FindFirst("sub")?.Value, out var userId) ||
            !Guid.TryParse(actor.FindFirst("affiliate_account_id")?.Value, out var claimedAccount) || claimedAccount != accountId ||
            !long.TryParse(actor.FindFirst("iat")?.Value, out var issuedAt) || issuedAt < 0 || issuedAt > 253402300799 ||
            string.IsNullOrWhiteSpace(actor.FindFirst("affiliate_session")?.Value))
            throw new UnauthorizedAccessException("Current affiliate identity and session are required.");
        if (!db.Database.IsNpgsql()) throw new InvalidOperationException("Affiliate design authority requires PostgreSQL.");
        var issuedAtUtc = DateTimeOffset.FromUnixTimeSeconds(issuedAt).UtcDateTime;
        var session = actor.FindFirst("affiliate_session")!.Value;
        // Authentication and account lifecycle lock user before account. The
        // multi-row CTE below must not acquire account first while waiting for
        // that user: doing so deadlocks a concurrent account revocation. Hold
        // these gates through the upsert, retaining any caller-owned transaction.
        await using var ownedTransaction = db.Database.CurrentTransaction is null
            ? await db.Database.BeginTransactionAsync(ct) : null;
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT 1 FROM \"AffiliateUsers\" WHERE \"Id\" = {userId} AND \"AffiliateAccountId\" = {accountId} FOR SHARE", ct);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT 1 FROM \"AffiliateAccounts\" WHERE \"Id\" = {accountId} FOR SHARE", ct);
        // A single conditional upsert closes the admission-to-write gap. Row share
        // locks serialize revocation/publication with this statement (and any outer
        // transaction), while ON CONFLICT preserves simultaneous first-save safety.
        var written = await db.Database.ExecuteSqlInterpolatedAsync($"""
            WITH admitted AS (
                SELECT a."Id"
                FROM "AffiliateAccounts" a
                JOIN "AffiliateUsers" u ON u."AffiliateAccountId" = a."Id"
                JOIN "AffiliateProductPolicies" policy ON policy."TenantId" = {tenantId} AND policy."ProductId" = {productId}
                JOIN "Products" p ON p."Id" = policy."ProductId" AND p."TenantId" = policy."TenantId"
                JOIN "Tenants" t ON t."Id" = p."TenantId"
                JOIN "AffiliateCategories" c ON c."Id" = policy."AffiliateCategoryId"
                WHERE a."Id" = {accountId} AND u."Id" = {userId}
                  AND NOT a."IsDeleted" AND a."Status" = {(int)AffiliateStatus.Active}
                  AND NOT u."IsDeleted" AND u."EmailVerified" AND u."RefreshToken" = {session}
                  AND (u."TokensValidAfterUtc" IS NULL OR u."TokensValidAfterUtc" < {issuedAtUtc})
                  AND policy."IsEnabled" AND NOT policy."IsDeleted"
                  AND c."IsActive" AND NOT c."IsDeleted"
                  AND ((c."Level" = 1 AND c."ParentId" IS NULL) OR
                       (c."Level" = 2 AND EXISTS (SELECT 1 FROM "AffiliateCategories" parent
                         WHERE parent."Id" = c."ParentId" AND parent."Level" = 1 AND parent."ParentId" IS NULL
                           AND parent."IsActive" AND NOT parent."IsDeleted" FOR SHARE)))
                  AND NOT p."IsDeleted" AND p."IsActive" AND p."IsOnlineVisible" AND p."IsSearchable"
                  AND p."PublishStatus" = {(int)PublishStatus.Published}
                  AND t."IsActive" AND t."Status" = {(int)TenantStatus.Active} AND t."DeletedAtUtc" IS NULL
                FOR SHARE OF a, u, policy, p, t, c
            )
            INSERT INTO "AffiliateLandingPageDesigns" ("AffiliateAccountId", "TenantId", "ProductId", "TemplateType", "UpdatedAtUtc")
            SELECT "Id", {tenantId}, {productId}, {template}, {DateTime.UtcNow} FROM admitted
            ON CONFLICT ("AffiliateAccountId", "TenantId", "ProductId")
            DO UPDATE SET "TemplateType" = EXCLUDED."TemplateType", "UpdatedAtUtc" = EXCLUDED."UpdatedAtUtc"
            """, ct);
        if (written != 1) throw new UnauthorizedAccessException("Affiliate or product authority changed before the design write.");
        if (ownedTransaction is not null) await ownedTransaction.CommitAsync(ct);
    }

    public static Task<string?> ReadAsync(ApplicationDbContext db, Guid accountId, Guid tenantId, Guid productId, CancellationToken ct) =>
        db.AffiliateLandingPageDesigns.AsNoTracking()
            .Where(d => d.AffiliateAccountId == accountId && d.TenantId == tenantId && d.ProductId == productId)
            .Select(d => d.TemplateType).SingleOrDefaultAsync(ct);
}
