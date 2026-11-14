using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PointOfSale.Core.Entities;
using PointOfSale.Core.Enums;
using PointOfSale.Infrastructure.Data;

namespace PointOfSale.Infrastructure.Eventing;

/// <summary>
/// Durable, local consumer of POS cart lifecycle events. The contract is an
/// append-only audit projection, NOT stock, money, provider delivery or checkout.
/// Audit append and completion are committed atomically; source events are kept.
/// </summary>
public sealed class PosCartAuditOutboxProcessor(
    IServiceScopeFactory scopeFactory,
    IOptions<PosCartAuditOutboxOptions> options,
    ILogger<PosCartAuditOutboxProcessor> logger,
    TimeProvider? clock = null)
{
    private DateTime NowUtc => (clock ?? TimeProvider.System).GetUtcNow().UtcDateTime;
    private static readonly HashSet<string> EventTypes = new(StringComparer.Ordinal)
    {
        "PosCartCreated", "PosCartItemAdded", "PosCartItemUpdated", "PosCartItemRemoved",
        "PosCartHold", "PosCartResume", "PosCartCancel", "PosCartExpire",
        "PosCartRecover", "PosCartAbandon", "PosCartExpired", "PosCartCompleted"
    };

    public async Task<int> ProcessTenantAsync(Guid tenantId, CancellationToken ct = default)
    {
        var settings = options.Value;
        if (!settings.Enabled || tenantId == Guid.Empty || !settings.TenantIds.Contains(tenantId))
            return 0;

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var retryBefore = NowUtc.AddMinutes(-1);
        var pending = await db.PosCartOutboxMessages.IgnoreQueryFilters().AsNoTracking()
            .Where(m => m.TenantId == tenantId && !m.IsDeleted && m.ProcessedAtUtc == null &&
                (m.ProcessingError == null || m.UpdatedAt == null || m.UpdatedAt <= retryBefore))
            .OrderBy(m => m.OccurredAtUtc).ThenBy(m => m.CartVersion).ThenBy(m => m.Id)
            .Take(settings.BatchSize).Select(m => new { m.Id, m.CartId }).ToListAsync(ct);
        var count = 0;
        foreach (var item in pending)
        {
            try
            {
                if (await ProcessAsync(tenantId, item.CartId, item.Id, ct)) count++;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                // Do not put exception text or event payload/PII into operational
                // logs. Preserve the pending event and expose a bounded code.
                logger.LogError("POS cart audit projection failed: Tenant {TenantId}, Message {MessageId}, Error {ErrorType}",
                    tenantId, item.Id, ex.GetType().Name);
                await RecordFailureAsync(tenantId, item.Id, ct);
            }
        }
        return count;
    }

    private async Task<bool> ProcessAsync(Guid tenantId, Guid cartId, Guid id, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var transaction = db.Database.IsRelational()
                ? await db.Database.BeginTransactionAsync(ct) : null;
            if (db.Database.ProviderName?.Contains("Npgsql", StringComparison.OrdinalIgnoreCase) == true)
            {
                var key = LockKey(tenantId, cartId);
                await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock({key})", ct);
            }
            var message = await db.PosCartOutboxMessages.IgnoreQueryFilters()
                .SingleOrDefaultAsync(m => m.Id == id && m.TenantId == tenantId && m.CartId == cartId, ct);
            if (message == null || message.IsDeleted || message.ProcessedAtUtc != null) return false;

            // A failed/unknown older event blocks its own cart, not another tenant
            // or cart. Never skip poison rows to manufacture a drained queue.
            if (await db.PosCartOutboxMessages.IgnoreQueryFilters().AnyAsync(m =>
                m.TenantId == tenantId && m.CartId == cartId && !m.IsDeleted &&
                m.ProcessedAtUtc == null && m.CartVersion < message.CartVersion, ct)) return false;

            ValidateSnapshot(message);
            var reference = $"POSCART:{message.Id:D}";
            var existing = await db.AuditLogs.IgnoreQueryFilters().SingleOrDefaultAsync(a =>
                a.TenantId == tenantId && a.Source == "PosCartOutbox" && a.ReferenceNumber == reference, ct);
            if (existing != null)
            {
                if (existing.EntityId != cartId.ToString("D") || existing.ActionType != message.EventType ||
                    existing.AfterDataJson != message.PayloadJson || existing.UserId != message.CreatedBy)
                    throw new InvalidOperationException("POS cart audit idempotency conflict.");
            }
            else
            {
                db.AuditLogs.Add(new AuditLog
                {
                    TenantId = tenantId,
                    TimestampUtc = message.OccurredAtUtc,
                    // Older events have no proven executor. Their snapshot's
                    // cashier is retained, but is NOT invented as the audit actor.
                    UserId = message.CreatedBy,
                    ActionType = message.EventType,
                    EntityType = "PosCart",
                    EntityId = cartId.ToString("D"),
                    Description = $"POS cart lifecycle: {message.EventType}, version {message.CartVersion}",
                    AfterDataJson = message.PayloadJson,
                    ReferenceNumber = reference,
                    Source = "PosCartOutbox",
                    SuccessFlag = true
                });
            }
            message.ProcessedAtUtc = NowUtc;
            message.ProcessingError = null;
            message.UpdatedAt = NowUtc;
            await db.SaveChangesAsync(ct);
            if (transaction != null) await transaction.CommitAsync(ct);
            return true;
        });
    }

    private static void ValidateSnapshot(PosCartOutboxMessage message)
    {
        if (!EventTypes.Contains(message.EventType) || message.CartVersion < 1 ||
            message.TenantId == Guid.Empty || message.CartId == Guid.Empty)
            throw new InvalidOperationException("Invalid POS cart event envelope.");
        using var document = JsonDocument.Parse(message.PayloadJson);
        var root = document.RootElement;
        if (root.GetProperty("tenantId").GetGuid() != message.TenantId ||
            root.GetProperty("cartId").GetGuid() != message.CartId ||
            root.GetProperty("version").GetInt32() != message.CartVersion ||
            !Enum.IsDefined((PosCartStatus)root.GetProperty("status").GetInt32()))
            throw new InvalidOperationException("POS cart snapshot/envelope mismatch.");
    }

    private async Task RecordFailureAsync(Guid tenantId, Guid id, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var message = await db.PosCartOutboxMessages.IgnoreQueryFilters()
            .SingleOrDefaultAsync(m => m.Id == id && m.TenantId == tenantId && m.ProcessedAtUtc == null, ct);
        if (message == null) return;
        message.ProcessingError = "POS_CART_AUDIT_PROJECTION_FAILED";
        message.UpdatedAt = NowUtc;
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException) { /* Another worker advanced this row. */ }
    }

    private static long LockKey(Guid tenantId, Guid cartId) =>
        BinaryPrimitives.ReadInt64LittleEndian(SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes($"pos-cart-audit:{tenantId:D}:{cartId:D}")));
}
