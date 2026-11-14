using MassTransit;
using System.Diagnostics.Metrics;
using System.Threading;
using Microsoft.EntityFrameworkCore;
using PointOfSale.Core.Enums;
using PointOfSale.Core.Events;
using PointOfSale.Infrastructure.Data;

namespace PointOfSale.API.Jobs;

/// <summary>
/// Re-emits persisted due/stuck operations. PostgreSQL remains the work source;
/// process memory never owns provisioning state.
/// </summary>
public sealed class TenantProvisioningRecoveryWorker : BackgroundService
{
    private static readonly Meter Meter = new(Consumers.TenantProvisioningConsumer.MeterName);
    private static int _pendingOperations;
    private static int _overdueOperations;
    private static long _lastSuccessfulRecoveryScan;
    private static readonly ObservableGauge<int> PendingOperations = Meter.CreateObservableGauge(
        "tenant.provisioning.pending.operations", () => Volatile.Read(ref _pendingOperations));
    private static readonly ObservableGauge<int> OverdueOperations = Meter.CreateObservableGauge(
        "tenant.provisioning.recovery.overdue", () => Volatile.Read(ref _overdueOperations));
    private static readonly ObservableGauge<long> LastSuccessfulRecoveryScan = Meter.CreateObservableGauge(
        "tenant.provisioning.recovery.last.success.timestamp",
        () => Volatile.Read(ref _lastSuccessfulRecoveryScan), unit: "s");
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IBus _bus;
    private readonly ILogger<TenantProvisioningRecoveryWorker> _logger;

    public TenantProvisioningRecoveryWorker(
        IServiceScopeFactory scopeFactory,
        IBus bus,
        ILogger<TenantProvisioningRecoveryWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _bus = bus;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await DispatchDueOperationsAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                _logger.LogError(
                    exception,
                    "Tenant provisioning recovery scan failed; persisted operations remain recoverable.");
            }

            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
        }
    }

    internal async Task<int> DispatchDueOperationsAsync(CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var now = DateTime.UtcNow;
        var stuckCutoff = now.AddMinutes(-10);

        // Installed RabbitMQ scraping exports aggregate queue metrics without
        // queue labels. Export the actual PostgreSQL work authority rather than
        // pretending a missing queue-specific series is a healthy zero backlog.
        var pending = await db.TenantProvisioningOperations.IgnoreQueryFilters()
            .CountAsync(operation => operation.State == TenantProvisioningState.Provisioning ||
                                     operation.State == TenantProvisioningState.Failed,
                cancellationToken);
        var overdue = await db.TenantProvisioningOperations.IgnoreQueryFilters()
            .CountAsync(operation =>
                (operation.State == TenantProvisioningState.Provisioning ||
                 operation.State == TenantProvisioningState.Failed) &&
                operation.UpdatedAtUtc <= stuckCutoff &&
                (!operation.NextAttemptAtUtc.HasValue || operation.NextAttemptAtUtc <= now) &&
                (!operation.LeaseExpiresAtUtc.HasValue || operation.LeaseExpiresAtUtc <= now),
                cancellationToken);
        Volatile.Write(ref _pendingOperations, pending);
        Volatile.Write(ref _overdueOperations, overdue);

        var due = await db.TenantProvisioningOperations
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(operation =>
                (operation.State == TenantProvisioningState.Failed ||
                 operation.State == TenantProvisioningState.Suspended) &&
                    (!operation.NextAttemptAtUtc.HasValue || operation.NextAttemptAtUtc <= now) ||
                operation.State == TenantProvisioningState.Provisioning &&
                    operation.UpdatedAtUtc <= stuckCutoff &&
                    (!operation.LeaseExpiresAtUtc.HasValue || operation.LeaseExpiresAtUtc <= now))
            .OrderBy(operation => operation.NextAttemptAtUtc ?? operation.CreatedAtUtc)
            .Take(25)
            .ToListAsync(cancellationToken);

        foreach (var operation in due)
        {
            await _bus.Publish(new TenantProvisioningRequestedEvent
            {
                EventId = Guid.NewGuid(),
                TenantId = operation.TenantId,
                IdempotencyKey = $"tenant-provisioning-recovery:{operation.Id:N}:{operation.AttemptCount}",
                ProvisioningOperationId = operation.Id,
                OwnerUserId = operation.OwnerUserId,
                StoreConfigId = operation.StoreConfigId,
                CanonicalHostname = operation.CanonicalHostname
            }, cancellationToken);
        }

        if (due.Count > 0)
        {
            _logger.LogInformation(
                "Re-emitted {Count} due tenant provisioning operations.",
                due.Count);
        }

        Volatile.Write(ref _lastSuccessfulRecoveryScan, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        return due.Count;
    }
}
