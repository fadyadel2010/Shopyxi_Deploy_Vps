using System.Diagnostics.Metrics;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using PointOfSale.API.Services.Auth;
using PointOfSale.Core.Entities;
using PointOfSale.Core.Enums;
using PointOfSale.Core.Events;
using PointOfSale.Core.Interfaces;
using PointOfSale.Core.Interfaces.Accounting;
using PointOfSale.Infrastructure.Data;

namespace PointOfSale.API.Consumers;

/// <summary>
/// Idempotently converges application-owned resources for a canonical tenant.
/// Wildcard DNS and TLS are global prerequisites and are never touched here.
/// </summary>
public sealed class TenantProvisioningConsumer : IConsumer<TenantProvisioningRequestedEvent>
{
    public const string MeterName = "PointOfSale.TenantProvisioning";

    private static readonly Meter Meter = new(MeterName);
    private static readonly Counter<long> Attempts = Meter.CreateCounter<long>("tenant.provisioning.attempts");
    private static readonly Counter<long> Completions = Meter.CreateCounter<long>("tenant.provisioning.completions");
    private static readonly Counter<long> Failures = Meter.CreateCounter<long>("tenant.provisioning.failures");
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan SuspendedRecheckDelay = TimeSpan.FromMinutes(5);

    private readonly ApplicationDbContext _db;
    private readonly ITenantAccountSeeder _accountSeeder;
    private readonly IEmailVerificationService _emailVerification;
    private readonly IEventPublisher _events;
    private readonly ILogger<TenantProvisioningConsumer> _logger;

    public TenantProvisioningConsumer(
        ApplicationDbContext db,
        ITenantAccountSeeder accountSeeder,
        IEmailVerificationService emailVerification,
        IEventPublisher events,
        ILogger<TenantProvisioningConsumer> logger)
    {
        _db = db;
        _accountSeeder = accountSeeder;
        _emailVerification = emailVerification;
        _events = events;
        _logger = logger;
    }

    public async Task Consume(ConsumeContext<TenantProvisioningRequestedEvent> context)
    {
        var message = context.Message;
        var now = DateTime.UtcNow;
        var leaseId = Guid.NewGuid();

        var claimed = await _db.TenantProvisioningOperations
            .IgnoreQueryFilters()
            .Where(operation =>
                operation.Id == message.ProvisioningOperationId &&
                operation.TenantId == message.TenantId &&
                operation.State != TenantProvisioningState.Ready &&
                operation.State != TenantProvisioningState.Disabled &&
                (!operation.NextAttemptAtUtc.HasValue || operation.NextAttemptAtUtc <= now) &&
                (!operation.LeaseExpiresAtUtc.HasValue || operation.LeaseExpiresAtUtc <= now))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(operation => operation.LeaseId, leaseId)
                .SetProperty(operation => operation.LeaseExpiresAtUtc, now + LeaseDuration)
                .SetProperty(operation => operation.State, TenantProvisioningState.Provisioning)
                .SetProperty(operation => operation.AttemptCount, operation => operation.AttemptCount + 1)
                .SetProperty(operation => operation.NextAttemptAtUtc, (DateTime?)null)
                .SetProperty(operation => operation.UpdatedAtUtc, now),
                context.CancellationToken);

        if (claimed != 1)
        {
            _logger.LogDebug(
                "Tenant provisioning request already completed, fenced, or not due | OperationId: {OperationId} | TenantId: {TenantId}",
                message.ProvisioningOperationId,
                message.TenantId);
            return;
        }

        Attempts.Add(1);

        try
        {
            // ExecuteUpdate bypasses the tracker. A retry/redelivery using the
            // same context must load the newly claimed lease and concurrency
            // originals, not an earlier suspended/failed tracked snapshot.
            foreach (var tracked in _db.ChangeTracker.Entries<TenantProvisioningOperation>()
                         .Where(entry => entry.Entity.Id == message.ProvisioningOperationId)
                         .ToList())
            {
                tracked.State = EntityState.Detached;
            }
            var operation = await _db.TenantProvisioningOperations
                .IgnoreQueryFilters()
                .SingleAsync(item => item.Id == message.ProvisioningOperationId, context.CancellationToken);

            EnsureMessageMatchesAuthority(operation, message);

            var tenantState = await _db.Tenants
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(tenant => tenant.Id == operation.TenantId)
                .Select(tenant => new { tenant.Status, tenant.IsActive, tenant.DeletedAtUtc })
                .SingleAsync(context.CancellationToken);

            if (tenantState.Status == TenantStatus.Suspended)
            {
                await SetTerminalStateAsync(
                    operation,
                    TenantProvisioningState.Suspended,
                    now,
                    now + SuspendedRecheckDelay,
                    context.CancellationToken);
                return;
            }

            if (tenantState.Status == TenantStatus.Deleted ||
                !tenantState.IsActive ||
                tenantState.DeletedAtUtc.HasValue)
            {
                await SetTerminalStateAsync(
                    operation,
                    TenantProvisioningState.Disabled,
                    now,
                    nextAttemptAtUtc: null,
                    context.CancellationToken);
                return;
            }

            if (!operation.AccountsSeededAtUtc.HasValue)
            {
                // The account seeder is idempotent. A crash after its commit and
                // before this checkpoint safely converges on the next attempt.
                await _accountSeeder.SeedAsync(operation.TenantId, context.CancellationToken);
                operation.AccountsSeededAtUtc = DateTime.UtcNow;
                operation.UpdatedAtUtc = operation.AccountsSeededAtUtc.Value;
                await _db.SaveChangesAsync(context.CancellationToken);
            }

            if (!operation.StorefrontProjectionQueuedAtUtc.HasValue)
            {
                var storeConfig = await _db.StoreConfigs
                    .IgnoreQueryFilters()
                    .AsNoTracking()
                    .SingleAsync(
                        config => config.Id == operation.StoreConfigId &&
                                  config.TenantId == operation.TenantId,
                        context.CancellationToken);

                await using (var projectionTransaction =
                    await _db.Database.BeginTransactionAsync(context.CancellationToken))
                {
                    await _events.PublishAsync(new StoreConfigCreatedEvent
                    {
                        TenantId = operation.TenantId,
                        StoreConfigId = storeConfig.Id,
                        ThemeId = storeConfig.ThemeId,
                        StoreName = storeConfig.StoreName,
                        CreatedAtUtc = storeConfig.CreatedAtUtc,
                        IdempotencyKey = $"tenant-provisioning-store-config:{operation.Id:N}"
                    }, context.CancellationToken);

                    operation.StorefrontProjectionQueuedAtUtc = DateTime.UtcNow;
                    operation.UpdatedAtUtc = operation.StorefrontProjectionQueuedAtUtc.Value;
                    await _db.SaveChangesAsync(context.CancellationToken);
                    await projectionTransaction.CommitAsync(context.CancellationToken);
                }
            }

            if (!operation.VerificationDispatchedAtUtc.HasValue)
            {
                var owner = await _db.Users
                    .IgnoreQueryFilters()
                    .SingleAsync(
                        user => user.Id == operation.OwnerUserId &&
                                user.TenantId == operation.TenantId &&
                                !user.IsDeleted,
                        context.CancellationToken);

                if (owner.EmailVerificationStatus != EmailVerificationStatus.Verified)
                {
                    // SMTP provides at-least-once delivery. Token persistence is
                    // durable, and duplicate links remain isolated to this owner.
                    await _emailVerification.CreateVerificationTokenAsync(
                        owner,
                        context.CancellationToken);
                }

                operation.VerificationDispatchedAtUtc = DateTime.UtcNow;
                operation.UpdatedAtUtc = operation.VerificationDispatchedAtUtc.Value;
                await _db.SaveChangesAsync(context.CancellationToken);
            }

            var completedAt = DateTime.UtcNow;
            await using (var readyTransaction =
                await _db.Database.BeginTransactionAsync(context.CancellationToken))
            {
                await _events.PublishAsync(new TenantProvisioningReadyEvent
                {
                    TenantId = operation.TenantId,
                    ProvisioningOperationId = operation.Id,
                    CanonicalHostname = operation.CanonicalHostname,
                    IdempotencyKey = $"tenant-provisioning-ready:{operation.Id:N}"
                }, context.CancellationToken);

                operation.State = TenantProvisioningState.Ready;
                operation.CompletedAtUtc = completedAt;
                operation.UpdatedAtUtc = completedAt;
                operation.NextAttemptAtUtc = null;
                operation.LeaseId = null;
                operation.LeaseExpiresAtUtc = null;
                operation.LastErrorCategory = null;
                operation.LastErrorCode = null;
                await _db.SaveChangesAsync(context.CancellationToken);
                await readyTransaction.CommitAsync(context.CancellationToken);
            }

            Completions.Add(1);
            _logger.LogInformation(
                "Tenant provisioning converged | OperationId: {OperationId} | TenantId: {TenantId} | Hostname: {Hostname} | Attempts: {AttemptCount}",
                operation.Id,
                operation.TenantId,
                operation.CanonicalHostname,
                operation.AttemptCount);
        }
        catch (Exception exception)
        {
            var category = Classify(exception);
            Failures.Add(1, new KeyValuePair<string, object?>("category", category));
            await RecordFailureAsync(
                message.ProvisioningOperationId,
                leaseId,
                exception,
                context.CancellationToken);
            throw;
        }
    }

    private static void EnsureMessageMatchesAuthority(
        TenantProvisioningOperation operation,
        TenantProvisioningRequestedEvent message)
    {
        if (operation.TenantId != message.TenantId ||
            operation.OwnerUserId != message.OwnerUserId ||
            operation.StoreConfigId != message.StoreConfigId ||
            !string.Equals(
                operation.CanonicalHostname,
                message.CanonicalHostname,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException("TENANT_PROVISIONING_AUTHORITY_MISMATCH");
        }
    }

    private async Task SetTerminalStateAsync(
        TenantProvisioningOperation operation,
        TenantProvisioningState state,
        DateTime now,
        DateTime? nextAttemptAtUtc,
        CancellationToken cancellationToken)
    {
        operation.State = state;
        operation.UpdatedAtUtc = now;
        operation.LeaseId = null;
        operation.LeaseExpiresAtUtc = null;
        operation.NextAttemptAtUtc = nextAttemptAtUtc;
        await _db.SaveChangesAsync(cancellationToken);
    }

    private async Task RecordFailureAsync(
        Guid operationId,
        Guid leaseId,
        Exception exception,
        CancellationToken cancellationToken)
    {
        _db.ChangeTracker.Clear();
        var operation = await _db.TenantProvisioningOperations
            .IgnoreQueryFilters()
            .AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == operationId, cancellationToken);
        if (operation == null)
        {
            return;
        }

        var delaySeconds = Math.Min(3600, 15 * Math.Pow(2, Math.Min(operation.AttemptCount, 8)));
        var jitterSeconds = Math.Abs(HashCode.Combine(operation.Id, operation.AttemptCount)) % 17;
        var nextAttempt = DateTime.UtcNow.AddSeconds(delaySeconds + jitterSeconds);
        var category = Classify(exception);

        await _db.TenantProvisioningOperations
            .IgnoreQueryFilters()
            .Where(item => item.Id == operationId && item.LeaseId == leaseId)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.State, TenantProvisioningState.Failed)
                .SetProperty(item => item.NextAttemptAtUtc, nextAttempt)
                .SetProperty(item => item.LeaseId, (Guid?)null)
                .SetProperty(item => item.LeaseExpiresAtUtc, (DateTime?)null)
                .SetProperty(item => item.LastErrorCategory, category)
                .SetProperty(item => item.LastErrorCode, exception.GetType().Name)
                .SetProperty(item => item.UpdatedAtUtc, DateTime.UtcNow),
                cancellationToken);

        _logger.LogError(
            exception,
            "Tenant provisioning failed and was scheduled for retry | OperationId: {OperationId} | Attempt: {Attempt} | NextAttemptAtUtc: {NextAttemptAtUtc} | Category: {Category}",
            operationId,
            operation.AttemptCount,
            nextAttempt,
            category);
    }

    private static string Classify(Exception exception) => exception switch
    {
        DbUpdateConcurrencyException => "concurrency",
        DbUpdateException => "database",
        TimeoutException => "timeout",
        OperationCanceledException => "cancelled",
        HttpRequestException => "external_dependency",
        InvalidOperationException when exception.Message.Contains("AUTHORITY", StringComparison.Ordinal) => "authority",
        _ => "unexpected"
    };
}

public sealed class TenantProvisioningConsumerDefinition : ConsumerDefinition<TenantProvisioningConsumer>
{
    public TenantProvisioningConsumerDefinition()
    {
        EndpointName = "tenant-provisioning-v1";
        ConcurrentMessageLimit = 2;
    }

    protected override void ConfigureConsumer(
        IReceiveEndpointConfigurator endpointConfigurator,
        IConsumerConfigurator<TenantProvisioningConsumer> consumerConfigurator,
        IRegistrationContext context)
    {
        endpointConfigurator.UseMessageRetry(retry => retry.Exponential(
            retryLimit: 5,
            minInterval: TimeSpan.FromSeconds(1),
            maxInterval: TimeSpan.FromSeconds(30),
            intervalDelta: TimeSpan.FromSeconds(3)));
    }
}
