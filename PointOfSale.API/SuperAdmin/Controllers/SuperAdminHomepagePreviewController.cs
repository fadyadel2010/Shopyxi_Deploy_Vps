using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PointOfSale.API.Services.Storefront;
using PointOfSale.Core.Enums;
using PointOfSale.Core.Helpers;
using PointOfSale.Infrastructure.Data;

namespace PointOfSale.API.SuperAdmin.Controllers;

[Route("api/super-admin/homepage/preview-store-config")]
public sealed class SuperAdminHomepagePreviewController(ApplicationDbContext db) : SuperAdminBaseController
{
    // Explicit platform-authorized tenant selection, never public X-Tenant-Id trust.
    // Use the live theme as the canvas for the locally authored homepage sections.
    [HttpGet("{tenantId:guid}")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<IActionResult> Get(Guid tenantId, CancellationToken cancellationToken)
    {
        var authority = await db.StoreConfigs.AsNoTracking().IgnoreQueryFilters()
            .Where(config => config.TenantId == tenantId)
            .Join(db.Tenants.AsNoTracking().IgnoreQueryFilters(), config => config.TenantId,
                tenant => tenant.Id, (config, tenant) => new { Config = config, Tenant = tenant })
            .Where(row => row.Tenant.IsActive && row.Tenant.Status == TenantStatus.Active && row.Tenant.DeletedAtUtc == null)
            .SingleOrDefaultAsync(cancellationToken);
        if (authority == null) return NotFound();
        if (string.IsNullOrWhiteSpace(authority.Tenant.Currency))
            return ErrorResponse(503, "STOREFRONT_CURRENCY_NOT_CONFIGURED", "Store currency is not configured.");

        var preset = await db.ThemePresets.AsNoTracking()
            .Where(item => item.Key == authority.Config.ThemeLivePresetKey || item.Key == "default")
            .OrderByDescending(item => item.Key == authority.Config.ThemeLivePresetKey)
            .FirstOrDefaultAsync(cancellationToken);
        return OkResponse(StorefrontDraftConfigMapper.MapLive(authority.Config, preset,
            authority.Tenant.Currency.Trim().ToUpperInvariant(),
            CountryCodeHelper.NormalizeOrDefault(authority.Tenant.Country)));
    }
}
