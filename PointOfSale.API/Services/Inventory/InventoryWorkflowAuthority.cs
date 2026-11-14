using Microsoft.EntityFrameworkCore;
using PointOfSale.Core.Entities;
using PointOfSale.Core.Security;
using PointOfSale.Infrastructure.Data;

namespace PointOfSale.API.Services.Inventory;

public sealed class InventoryWorkflowException(string code, string message) : InvalidOperationException(message)
{
    public string Code { get; } = code;
}

internal static class InventoryWorkflowAuthority
{
    public static async Task RequireActorAsync(ApplicationDbContext db, Guid tenantId, Guid actor, string permission, bool finance, CancellationToken ct)
    {
        if (db.ScopedTenantId != tenantId || db.Database.CurrentTransaction == null || !db.Database.IsNpgsql())
            throw new UnauthorizedAccessException("A tenant-scoped PostgreSQL workflow transaction is required.");
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM \"Users\" WHERE \"TenantId\"={tenantId} AND \"Id\"={actor} FOR SHARE", ct);
        var user = await db.Users.IgnoreQueryFilters().AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == actor && x.IsActive && !x.IsDeleted, ct);
        if (user == null || !PosRolePermissionCatalog.IsGranted(user.Role, permission) ||
            (finance && !PosRolePermissionCatalog.IsGranted(user.Role, PosPermissions.FinanceOperate)))
            throw new UnauthorizedAccessException("The current active same-tenant actor lacks workflow authority.");
    }
    public static string Reason(string reason)
    {
        if (string.IsNullOrWhiteSpace(reason) || reason.Trim().Length > 1000)
            throw new InventoryWorkflowException("INVENTORY_REASON_REQUIRED", "A reason of up to 1000 characters is required.");
        return reason.Trim();
    }
    public static bool Quantity(decimal value) => value >= 0 && value <= 99999999999999.9999m && decimal.Round(value, 4) == value;
}
