using System.Text.Json;
using PointOfSale.Core.Entities;
using PointOfSale.Infrastructure.Data;

namespace PointOfSale.API.Services;

internal static class PartyLifecycleAudit
{
    public static void Append(
        ApplicationDbContext context, Guid tenantId, Guid actorUserId,
        string action, string entityType, Guid entityId, string description,
        bool wasActive, object response)
    {
        // The caller owns the lifecycle transaction. A volatile audit retry queue
        // cannot substitute for the durable fact accompanying this state change.
        if (context.Database.CurrentTransaction == null)
            throw new InvalidOperationException("Party lifecycle auditing requires a transaction.");

        context.AuditLogs.Add(new AuditLog
        {
            TenantId = tenantId,
            UserId = actorUserId,
            TimestampUtc = DateTime.UtcNow,
            ActionType = action,
            EntityType = entityType,
            EntityId = entityId.ToString(),
            Description = description,
            BeforeDataJson = JsonSerializer.Serialize(new { IsActive = wasActive }),
            AfterDataJson = JsonSerializer.Serialize(response),
            ReferenceNumber = entityId.ToString(),
            Source = nameof(PartyLifecycleAudit),
            SuccessFlag = true
        });
    }
}
