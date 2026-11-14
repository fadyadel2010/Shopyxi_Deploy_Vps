using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PointOfSale.API.SuperAdmin.Services;
using PointOfSale.Core.Enums;
using PointOfSale.Core.Events;
using PointOfSale.Core.Interfaces;
using PointOfSale.Infrastructure.Data;

namespace PointOfSale.API.SuperAdmin.Controllers;

[Route("api/super-admin/tenant-provisioning")]
public sealed class SuperAdminTenantProvisioningController : SuperAdminBaseController
{
    private const int MaxPageSize = 100;
    private readonly ApplicationDbContext _db;
    private readonly IEventPublisher _events;
    private readonly ISuperAdminAuditService _audit;

    public SuperAdminTenantProvisioningController(
        ApplicationDbContext db,
        IEventPublisher events,
        ISuperAdminAuditService audit)
    {
        _db = db;
        _events = events;
        _audit = audit;
    }

    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> List(
        [FromQuery] TenantProvisioningState? state = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 25,
        CancellationToken cancellationToken = default)
    {
        Response.Headers.CacheControl = "private, no-store";
        if (page < 1 || pageSize is < 1 or > MaxPageSize)
        {
            return ErrorResponse(
                StatusCodes.Status400BadRequest,
                "INVALID_QUERY",
                $"page must be positive and pageSize must be between 1 and {MaxPageSize}.");
        }

        var operations = _db.TenantProvisioningOperations
            .IgnoreQueryFilters()
            .AsNoTracking();
        if (state.HasValue)
        {
            operations = operations.Where(operation => operation.State == state.Value);
        }

        var totalCount = await operations.CountAsync(cancellationToken);
        var items = await operations
            .Join(
                _db.Tenants.IgnoreQueryFilters().AsNoTracking(),
                operation => operation.TenantId,
                tenant => tenant.Id,
                (operation, tenant) => new TenantProvisioningOperationDto(
                    operation.Id,
                    operation.TenantId,
                    tenant.Slug ?? string.Empty,
                    operation.CanonicalHostname,
                    operation.State,
                    operation.AttemptCount,
                    operation.NextAttemptAtUtc,
                    operation.LeaseExpiresAtUtc,
                    operation.LastErrorCategory,
                    operation.LastErrorCode,
                    operation.AccountsSeededAtUtc,
                    operation.StorefrontProjectionQueuedAtUtc,
                    operation.VerificationDispatchedAtUtc,
                    operation.CreatedAtUtc,
                    operation.UpdatedAtUtc,
                    operation.CompletedAtUtc))
            .OrderByDescending(operation => operation.UpdatedAtUtc)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        return OkResponse(
            new { items },
            new Dictionary<string, object?>
            {
                ["page"] = page,
                ["pageSize"] = pageSize,
                ["totalCount"] = totalCount,
                ["totalPages"] = (int)Math.Ceiling(totalCount / (double)pageSize)
            });
    }

    [HttpPost("{operationId:guid}/retry")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Retry(
        Guid operationId,
        [FromBody] RetryTenantProvisioningRequest request,
        CancellationToken cancellationToken = default)
    {
        Response.Headers.CacheControl = "private, no-store";
        var now = DateTime.UtcNow;
        await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);

        var authority = await _db.TenantProvisioningOperations
            .IgnoreQueryFilters()
            .Where(operation => operation.Id == operationId)
            .Join(
                _db.Tenants.IgnoreQueryFilters(),
                operation => operation.TenantId,
                tenant => tenant.Id,
                (operation, tenant) => new { Operation = operation, Tenant = tenant })
            .SingleOrDefaultAsync(cancellationToken);

        if (authority == null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return ErrorResponse(
                StatusCodes.Status404NotFound,
                "PROVISIONING_OPERATION_NOT_FOUND",
                "Tenant provisioning operation was not found.");
        }

        if (authority.Operation.State is TenantProvisioningState.Ready or TenantProvisioningState.Disabled ||
            authority.Tenant.Status != TenantStatus.Active ||
            !authority.Tenant.IsActive ||
            authority.Tenant.DeletedAtUtc.HasValue)
        {
            await transaction.RollbackAsync(cancellationToken);
            return ErrorResponse(
                StatusCodes.Status409Conflict,
                "PROVISIONING_RETRY_FORBIDDEN",
                "Current tenant lifecycle or provisioning state does not permit retry.");
        }

        if (authority.Operation.LeaseExpiresAtUtc > now)
        {
            await transaction.RollbackAsync(cancellationToken);
            return ErrorResponse(
                StatusCodes.Status409Conflict,
                "PROVISIONING_IN_PROGRESS",
                "This operation currently has an active worker lease.");
        }

        authority.Operation.State = TenantProvisioningState.Failed;
        authority.Operation.NextAttemptAtUtc = now;
        authority.Operation.LeaseId = null;
        authority.Operation.LeaseExpiresAtUtc = null;
        authority.Operation.LastErrorCategory = "operator_retry";
        authority.Operation.LastErrorCode = null;
        authority.Operation.UpdatedAtUtc = now;

        await _events.PublishAsync(new TenantProvisioningRequestedEvent
        {
            TenantId = authority.Operation.TenantId,
            ProvisioningOperationId = authority.Operation.Id,
            OwnerUserId = authority.Operation.OwnerUserId,
            StoreConfigId = authority.Operation.StoreConfigId,
            CanonicalHostname = authority.Operation.CanonicalHostname,
            IdempotencyKey = $"tenant-provisioning-admin-retry:{authority.Operation.Id:N}:{authority.Operation.AttemptCount}"
        }, cancellationToken);

        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            await transaction.RollbackAsync(cancellationToken);
            _db.ChangeTracker.Clear();
            return ErrorResponse(
                StatusCodes.Status409Conflict,
                "PROVISIONING_STATE_CHANGED",
                "Provisioning ownership or state changed. Refresh before retrying.");
        }
        await transaction.CommitAsync(cancellationToken);

        await _audit.LogActionAsync(
            action: "TENANT_PROVISIONING_RETRY_REQUESTED",
            entityType: "TenantProvisioningOperation",
            entityId: authority.Operation.Id.ToString(),
            description: "Super Admin requested a controlled tenant provisioning retry.",
            success: true,
            details: new
            {
                authority.Operation.TenantId,
                authority.Operation.CanonicalHostname,
                request.Reason,
                authority.Operation.AttemptCount
            },
            actorId: CurrentSuperAdminId,
            actorEmail: CurrentSuperAdminEmail,
            cancellationToken: cancellationToken);

        return OkResponse(new
        {
            authority.Operation.Id,
            authority.Operation.TenantId,
            authority.Operation.State,
            authority.Operation.NextAttemptAtUtc
        });
    }
}

public sealed class RetryTenantProvisioningRequest
{
    [Required]
    [StringLength(500, MinimumLength = 10)]
    public string Reason { get; init; } = string.Empty;
}

public sealed record TenantProvisioningOperationDto(
    Guid Id,
    Guid TenantId,
    string Slug,
    string CanonicalHostname,
    TenantProvisioningState State,
    int AttemptCount,
    DateTime? NextAttemptAtUtc,
    DateTime? LeaseExpiresAtUtc,
    string? LastErrorCategory,
    string? LastErrorCode,
    DateTime? AccountsSeededAtUtc,
    DateTime? StorefrontProjectionQueuedAtUtc,
    DateTime? VerificationDispatchedAtUtc,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc,
    DateTime? CompletedAtUtc);
