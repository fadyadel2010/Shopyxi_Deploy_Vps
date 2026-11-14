using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PointOfSale.Core.Entities;
using PointOfSale.Core.Enums;
using PointOfSale.Core.Payments;
using PointOfSale.Infrastructure.Data;

namespace PointOfSale.Infrastructure.Fulfillment;

/// <summary>
/// The authorized POS completion command is manual delivery-confirmation authority.
/// A status value, entered POD URL, or an event payload alone is not that command.
/// Uses the existing audit store; delivery never manufactures collection authority.
/// </summary>
public static class ManualOrderCompletionAuthority
{
    public const string AuditAction = "ManualOrderCompleted";
    public const string AuditSource = "OrderFulfillmentService";

    public static bool IsAllowedRole(UserRole role) => role is UserRole.Owner or UserRole.Admin or UserRole.Manager;

    internal static AuditLog CreateAudit(Order order, User actor, DateTime atUtc, string? idempotencyKey)
    {
        // PostgreSQL timestamps retain microseconds, not DateTime's 100ns precision.
        atUtc = new DateTime(atUtc.Ticks - atUtc.Ticks % 10, DateTimeKind.Utc);
        return new AuditLog
        {
            TenantId = order.TenantId, UserId = actor.Id, UserNameSnapshot = actor.Username,
            ActionType = AuditAction, EntityType = nameof(Order), EntityId = order.Id.ToString(),
            TimestampUtc = atUtc, Source = AuditSource, SuccessFlag = true, ReferenceNumber = order.OrderNumber,
            Description = "Authorized POS Admin completion confirms manual delivery; payment/accounting validated separately.",
            AfterDataJson = JsonSerializer.Serialize(new CompletionSnapshot(order.Id, order.TenantId, actor.Id, actor.Role,
                FulfillmentStatus.Completed, OrderStatus.Completed, order.DeliveryReference, order.ProofOfDeliveryUrl, atUtc, idempotencyKey))
        };
    }

    public static bool Matches(Order order, AuditLog audit)
    {
        if (order.FulfillmentMode != FulfillmentMode.Manual || order.Status != OrderStatus.Completed ||
            order.FulfillmentStatus != FulfillmentStatus.Completed || order.IsDeleted ||
            OrderOperationalFinancialAuthority.GetCompletionBlockReason(order) != null ||
            audit.TenantId != order.TenantId || audit.EntityType != nameof(Order) || audit.EntityId != order.Id.ToString() ||
            audit.ActionType != AuditAction || audit.Source != AuditSource || !audit.SuccessFlag ||
            audit.UserId is not Guid actorId || actorId == Guid.Empty || audit.AfterDataJson == null)
            return false;
        try
        {
            var snapshot = JsonSerializer.Deserialize<CompletionSnapshot>(audit.AfterDataJson);
            return snapshot != null && snapshot.OrderId == order.Id && snapshot.TenantId == order.TenantId &&
                snapshot.ActorId == actorId && IsAllowedRole(snapshot.ActorRole) &&
                snapshot.FulfillmentStatus == FulfillmentStatus.Completed && snapshot.OrderStatus == OrderStatus.Completed &&
                snapshot.CompletedAtUtc == audit.TimestampUtc &&
                !string.IsNullOrWhiteSpace(snapshot.DeliveryReference) && !string.IsNullOrWhiteSpace(snapshot.ProofOfDeliveryUrl) &&
                snapshot.DeliveryReference == order.DeliveryReference && snapshot.ProofOfDeliveryUrl == order.ProofOfDeliveryUrl;
        }
        catch (JsonException) { return false; }
    }

    public static async Task<bool> HasAuthorityAsync(ApplicationDbContext db, Order order, CancellationToken ct)
    {
        if (order.FulfillmentMode != FulfillmentMode.Manual || order.Status != OrderStatus.Completed ||
            order.FulfillmentStatus != FulfillmentStatus.Completed) return false;
        var id = order.Id.ToString();
        var audits = await db.AuditLogs.IgnoreQueryFilters().AsNoTracking().Where(a => a.TenantId == order.TenantId &&
            a.EntityType == nameof(Order) && a.EntityId == id && a.ActionType == AuditAction && a.Source == AuditSource && a.SuccessFlag)
            .ToListAsync(ct);
        return audits.Any(a => Matches(order, a));
    }

    private sealed record CompletionSnapshot(Guid OrderId, Guid TenantId, Guid ActorId, UserRole ActorRole,
        FulfillmentStatus FulfillmentStatus, OrderStatus OrderStatus, string? DeliveryReference, string? ProofOfDeliveryUrl,
        DateTime CompletedAtUtc, string? IdempotencyKey);
}

/// <summary>Only the exact audit created by the canonical completion transaction may be inserted.</summary>
internal static class CompletionAuditWriteAuthority
{
    private static readonly AsyncLocal<AuditLog?> Current = new();
    internal static bool Allows(AuditLog audit) => ReferenceEquals(Current.Value, audit);
    internal static IDisposable Begin(AuditLog? audit)
    {
        var previous = Current.Value; Current.Value = audit; return new Scope(previous);
    }
    private sealed class Scope(AuditLog? previous) : IDisposable
    {
        public void Dispose() => Current.Value = previous;
    }
}
