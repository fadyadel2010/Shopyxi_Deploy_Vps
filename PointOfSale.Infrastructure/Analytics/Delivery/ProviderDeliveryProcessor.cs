using System.Text.Json;
using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PointOfSale.Core.Analytics;
using PointOfSale.Core.Entities;
using PointOfSale.Infrastructure.Data;

namespace PointOfSale.Infrastructure.Analytics.Delivery;

public sealed class ProviderDeliveryProcessor : IProviderDeliveryProcessor
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ProviderDeliveryOptions _options;
    private readonly ILogger<ProviderDeliveryProcessor> _logger;
    private readonly ProviderDeliveryMetrics _metrics;

    public ProviderDeliveryProcessor(
        IServiceProvider serviceProvider,
        IOptions<ProviderDeliveryOptions> options,
        ILogger<ProviderDeliveryProcessor> logger,
        ProviderDeliveryMetrics metrics)
    {
        _serviceProvider = serviceProvider;
        _options = options.Value;
        _logger = logger;
        _metrics = metrics;
    }

    public async Task<int> ProcessPendingMessagesAsync(
        int batchSize,
        CancellationToken cancellationToken = default)
    {
        using var activity = ProviderDeliveryTelemetry.ActivitySource.StartActivity(
            "ProviderDelivery.ProcessBatch",
            ActivityKind.Internal);
        using var batchScope = _serviceProvider.CreateScope();
        var db = batchScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var nowUtc = DateTime.UtcNow;

        var ids = await db.Set<ProviderDeliveryOutboxMessage>()
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(message =>
                !message.IsDeleted && message.DeliveredAtUtc == null &&
                !message.IsFailed &&
                !message.IsDeadLettered &&
                (message.NextRetryAtUtc == null || message.NextRetryAtUtc <= nowUtc) &&
                (message.ClaimedUntilUtc == null || message.ClaimedUntilUtc <= nowUtc))
            .OrderBy(message => message.OccurredAtUtc)
            .ThenBy(message => message.CreatedAt)
            .ThenBy(message => message.Id)
            .Take(batchSize)
            .Select(message => message.Id)
            .ToListAsync(cancellationToken);

        if (ids.Count == 0)
        {
            return 0;
        }

        var parallelism = Math.Clamp(_options.MaxParallelism, 1, ids.Count);
        var processed = 0;
        await Parallel.ForEachAsync(
            ids,
            new ParallelOptions
            {
                CancellationToken = cancellationToken,
                MaxDegreeOfParallelism = parallelism
            },
            async (id, ct) =>
            {
                if (await ProcessOneAsync(id, ct))
                {
                    Interlocked.Increment(ref processed);
                }
            });

        await RefreshMetricsAsync(cancellationToken);
        return processed;
    }

    private async Task<bool> ProcessOneAsync(Guid messageId, CancellationToken cancellationToken)
    {
        using var activity = ProviderDeliveryTelemetry.ActivitySource.StartActivity(
            "ProviderDelivery.ProcessMessage",
            ActivityKind.Internal);
        activity?.SetTag("provider_delivery.message_id", messageId);

        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var sender = scope.ServiceProvider.GetRequiredService<IProviderDeliveryHttpSender>();
        var retryEngine = scope.ServiceProvider.GetRequiredService<IProviderDeliveryRetryEngine>();
        var circuitBreaker = scope.ServiceProvider.GetRequiredService<IProviderCircuitBreaker>();
        var nowUtc = DateTime.UtcNow;

        var claimResult = await ClaimAsync(db, messageId, nowUtc, cancellationToken);
        if (claimResult == null)
        {
            return false;
        }

        db.ChangeTracker.Clear();
        var message = await db.Set<ProviderDeliveryOutboxMessage>()
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(entry => entry.Id == messageId && !entry.IsDeleted &&
                entry.ClaimedUntilUtc == claimResult && entry.ClaimedUntilUtc > DateTime.UtcNow &&
                entry.DeliveredAtUtc == null && !entry.IsFailed && !entry.IsDeadLettered, cancellationToken);

        if (message == null)
        {
            return false;
        }

        using var logScope = _logger.BeginScope(new Dictionary<string, object?>
        {
            ["CorrelationId"] = message.CorrelationId,
            ["TenantId"] = message.TenantId,
            ["Provider"] = message.Provider,
            ["DeliveryKey"] = message.DeliveryKey,
            ["ProviderDeliveryMessageId"] = message.Id
        });

        var provider = Enum.Parse<AnalyticsProvider>(message.Provider);
        activity?.SetTag("provider", message.Provider);
        // Tenant identifiers belong to authorized business state, not exported traces.
        if (!await circuitBreaker.CanAttemptAsync(provider, message.TenantId, cancellationToken) ||
            !await circuitBreaker.CanAttemptAsync(provider, null, cancellationToken))
        {
            using var retryActivity = ProviderDeliveryTelemetry.ActivitySource.StartActivity(
                "ProviderDelivery.RetryScheduled",
                ActivityKind.Internal);
            var nextRetryAtUtc = nowUtc.AddMinutes(Math.Min(_options.CircuitOpenMinutes, _options.MaxRetryDelayMinutes));
            message.RecordRetryableFailure(
                nowUtc,
                "circuit_open",
                "Provider circuit is open.",
                null,
                null,
                nextRetryAtUtc,
                BuildFailureMetadata("circuit_open", "Circuit breaker prevented provider call."));
            if (!await SaveOwnedOutcomeAsync(db, message, claimResult.Value, cancellationToken)) return false;
            _metrics.RecordFailure(message.Provider, "circuit_open", terminal: false);
            return false;
        }

        var circuitStateBeforeSend = await circuitBreaker.GetStateAsync(provider, message.TenantId, cancellationToken);
        if (!await db.Set<ProviderDeliveryOutboxMessage>().IgnoreQueryFilters().AsNoTracking()
            .AnyAsync(entry => entry.Id == message.Id && !entry.IsDeleted && entry.TenantId == message.TenantId &&
                entry.Provider == message.Provider && entry.DeliveryKey == message.DeliveryKey &&
                entry.PayloadHash == message.PayloadHash && entry.ClaimedUntilUtc == claimResult &&
                entry.ClaimedUntilUtc > DateTime.UtcNow && entry.DeliveredAtUtc == null &&
                !entry.IsFailed && !entry.IsDeadLettered, cancellationToken)) return false;
        var result = await sender.SendAsync(message, cancellationToken);
        if (result.Success)
        {
            message.MarkDelivered(DateTime.UtcNow, result.StatusCode);
            if (!await SaveOwnedOutcomeAsync(db, message, claimResult.Value, cancellationToken)) return false;
            await circuitBreaker.RecordSuccessAsync(provider, message.TenantId, cancellationToken);
            if (string.Equals(circuitStateBeforeSend.State, ProviderCircuitStates.HalfOpen, StringComparison.Ordinal))
            {
                _metrics.RecordCircuitRecovered(message.Provider);
            }

            _metrics.RecordDelivery(message.Provider, result.Latency);
            _logger.LogInformation(
                "Provider delivery succeeded | Provider: {Provider} | MessageId: {MessageId} | StatusCode: {StatusCode} | AttemptCount: {AttemptCount}",
                message.Provider,
                message.Id,
                result.StatusCode,
                message.AttemptCount);
            return true;
        }

        var nextAttemptNumber = message.AttemptCount + 1;
        var decision = retryEngine.Classify(result, nextAttemptNumber, _options, DateTime.UtcNow);
        var sanitizedResponse = ProviderDeliverySecretMasker.Mask(result.ResponseBody);
        var sanitizedError = ProviderDeliverySecretMasker.Mask(result.ErrorMessage ?? "Provider delivery failed.");
        var metadataJson = BuildFailureMetadata(decision.Diagnostic, sanitizedError);

        if (decision.IsTerminal)
        {
            using var deadLetterActivity = ProviderDeliveryTelemetry.ActivitySource.StartActivity(
                "ProviderDelivery.DeadLetter",
                ActivityKind.Internal);
            deadLetterActivity?.SetTag("provider", message.Provider);
            deadLetterActivity?.SetTag("failure.class", decision.FailureClass);
            message.RecordTerminalFailure(
                DateTime.UtcNow,
                decision.FailureClass,
                sanitizedError,
                result.StatusCode,
                sanitizedResponse,
                decision.ReplayEligible,
                metadataJson);
        }
        else
        {
            using var retryActivity = ProviderDeliveryTelemetry.ActivitySource.StartActivity(
                "ProviderDelivery.RetryScheduled",
                ActivityKind.Internal);
            retryActivity?.SetTag("provider", message.Provider);
            retryActivity?.SetTag("failure.class", decision.FailureClass);
            message.RecordRetryableFailure(
                DateTime.UtcNow,
                decision.FailureClass,
                sanitizedError,
                result.StatusCode,
                sanitizedResponse,
                decision.NextRetryAtUtc ?? DateTime.UtcNow.AddMinutes(1),
                metadataJson);
        }

        if (!await SaveOwnedOutcomeAsync(db, message, claimResult.Value, cancellationToken)) return false;
        await circuitBreaker.RecordFailureAsync(provider, message.TenantId, _options, cancellationToken);
        await circuitBreaker.RecordFailureAsync(provider, null, _options, cancellationToken);

        _metrics.RecordFailure(message.Provider, decision.FailureClass, decision.IsTerminal);
        _logger.LogWarning(
            "Provider delivery failed | Provider: {Provider} | MessageId: {MessageId} | FailureClass: {FailureClass} | AttemptCount: {AttemptCount} | Terminal: {Terminal} | NextRetryAtUtc: {NextRetryAtUtc}",
            message.Provider,
            message.Id,
            decision.FailureClass,
            message.AttemptCount,
            decision.IsTerminal,
            message.NextRetryAtUtc);

        return false;
    }

    private async Task<DateTime?> ClaimAsync(
        ApplicationDbContext db,
        Guid messageId,
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync<DateTime?>(async () =>
        {
            await using var ownership = db.Database.IsNpgsql() && db.Database.CurrentTransaction is null
                ? await db.Database.BeginTransactionAsync(cancellationToken) : null;
            using var activity = ProviderDeliveryTelemetry.ActivitySource.StartActivity(
                "ProviderDelivery.Lease",
                ActivityKind.Internal);
            activity?.SetTag("provider_delivery.message_id", messageId);

            // PostgreSQL persists microseconds. Keep the exact value used by both
            // the atomic claim and subsequent ownership predicates, never reload
            // a different owner's lease and infer ownership from its fresh state.
            nowUtc = db.Database.IsNpgsql()
                ? await ReadPostLockClockAsync(db, messageId, cancellationToken) : DateTime.UtcNow;
            var leaseUntilUtc = nowUtc.AddSeconds(_options.ProcessingLeaseSeconds);
            leaseUntilUtc = new DateTime(leaseUntilUtc.Ticks - leaseUntilUtc.Ticks % 10, DateTimeKind.Utc);
            if (db.Database.IsRelational())
            {
                var updatedRows = await db.Set<ProviderDeliveryOutboxMessage>()
                    .IgnoreQueryFilters()
                    .Where(message =>
                        message.Id == messageId && !message.IsDeleted &&
                        message.DeliveredAtUtc == null &&
                        !message.IsFailed &&
                        !message.IsDeadLettered &&
                        (message.NextRetryAtUtc == null || message.NextRetryAtUtc <= nowUtc) &&
                        (message.ClaimedUntilUtc == null || message.ClaimedUntilUtc <= nowUtc))
                    .ExecuteUpdateAsync(setters => setters
                        .SetProperty(message => message.ClaimedUntilUtc, leaseUntilUtc)
                        .SetProperty(message => message.UpdatedAt, nowUtc),
                        cancellationToken);

                activity?.SetTag("provider_delivery.lease_acquired", updatedRows == 1);
                if (ownership is not null) await ownership.CommitAsync(cancellationToken);
                return updatedRows == 1 ? leaseUntilUtc : null;
            }

            db.ChangeTracker.Clear();
            var message = await db.Set<ProviderDeliveryOutboxMessage>()
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(entry => entry.Id == messageId && !entry.IsDeleted, cancellationToken);

            if (message == null || !message.TryClaim(nowUtc, _options.ProcessingLeaseSeconds))
            {
                return null;
            }

            await db.SaveChangesAsync(cancellationToken);
            return message.ClaimedUntilUtc;
        });
    }

    private static async Task<bool> SaveOwnedOutcomeAsync(
        ApplicationDbContext db, ProviderDeliveryOutboxMessage message, DateTime claimedUntilUtc, CancellationToken cancellationToken)
    {
        if (!db.Database.IsRelational())
        {
            await db.SaveChangesAsync(cancellationToken);
            return true;
        }

        // ProviderDeliveryOutbox has no concurrency token. A tracked SaveChanges
        // would allow an expired sender to acknowledge or dead-letter a new
        // owner's claim. Persist only this lease's outcome, atomically.
        await using var ownership = db.Database.IsNpgsql() && db.Database.CurrentTransaction is null
            ? await db.Database.BeginTransactionAsync(cancellationToken) : null;
        var nowUtc = db.Database.IsNpgsql()
            ? await ReadPostLockClockAsync(db, message.Id, cancellationToken) : DateTime.UtcNow;
        var updated = await db.Set<ProviderDeliveryOutboxMessage>().IgnoreQueryFilters()
            .Where(entry => entry.Id == message.Id && !entry.IsDeleted && entry.TenantId == message.TenantId &&
                entry.Provider == message.Provider && entry.DeliveryKey == message.DeliveryKey &&
                entry.PayloadHash == message.PayloadHash && entry.ClaimedUntilUtc == claimedUntilUtc &&
                entry.ClaimedUntilUtc > nowUtc && entry.DeliveredAtUtc == null && !entry.IsFailed && !entry.IsDeadLettered)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(entry => entry.DeliveredAtUtc, message.DeliveredAtUtc)
                .SetProperty(entry => entry.ClaimedUntilUtc, message.ClaimedUntilUtc)
                .SetProperty(entry => entry.NextRetryAtUtc, message.NextRetryAtUtc)
                .SetProperty(entry => entry.AttemptCount, message.AttemptCount)
                .SetProperty(entry => entry.IsFailed, message.IsFailed)
                .SetProperty(entry => entry.IsDeadLettered, message.IsDeadLettered)
                .SetProperty(entry => entry.ReplayEligible, message.ReplayEligible)
                .SetProperty(entry => entry.DeadLetteredAtUtc, message.DeadLetteredAtUtc)
                .SetProperty(entry => entry.FailureClass, message.FailureClass)
                .SetProperty(entry => entry.LastError, message.LastError)
                .SetProperty(entry => entry.LastHttpStatusCode, message.LastHttpStatusCode)
                .SetProperty(entry => entry.LastResponseBody, message.LastResponseBody)
                .SetProperty(entry => entry.FailureMetadataJson, message.FailureMetadataJson)
                .SetProperty(entry => entry.UpdatedAt, nowUtc), cancellationToken);
        if (ownership is not null) await ownership.CommitAsync(cancellationToken);
        db.ChangeTracker.Clear();
        return updated == 1;
    }

    private static async Task<DateTime> ReadPostLockClockAsync(ApplicationDbContext db, Guid messageId, CancellationToken ct)
    {
        if (db.Database.CurrentTransaction is null)
            throw new InvalidOperationException("Provider delivery ownership requires its current transaction.");
        // This is the global system-job queue's exact message identity, not a
        // tenant-facing query. Outcome CAS additionally binds tenant/provider/key,
        // payload and lease. Never evaluate expiry before a required lock wait.
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT 1 FROM \"ProviderDeliveryOutbox\" WHERE \"Id\" = {messageId} FOR UPDATE", ct);
        return await db.Database.SqlQuery<DateTime>($"SELECT clock_timestamp() AS \"Value\"").SingleAsync(ct);
    }

    private async Task RefreshMetricsAsync(CancellationToken cancellationToken)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var pending = await db.Set<ProviderDeliveryOutboxMessage>()
            .IgnoreQueryFilters()
            .CountAsync(message => !message.IsDeleted && message.DeliveredAtUtc == null && !message.IsDeadLettered, cancellationToken);
        var dead = await db.Set<ProviderDeliveryOutboxMessage>()
            .IgnoreQueryFilters()
            .CountAsync(message => !message.IsDeleted && message.IsDeadLettered, cancellationToken);
        _metrics.SetPending(pending);
        _metrics.SetDeadLettered(dead);
    }

    private static string BuildFailureMetadata(string diagnostic, string error) =>
        JsonSerializer.Serialize(new
        {
            diagnostic,
            error = ProviderDeliverySecretMasker.Mask(error),
            recordedAtUtc = DateTime.UtcNow
        });
}
