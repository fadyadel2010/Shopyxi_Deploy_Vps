using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace PointOfSale.Infrastructure.Marketplace.Generation;

/// <summary>Registration is not release authority. No background worker is registered.</summary>
public sealed class MarketplaceGenerationOptions
{
    public bool ExternalWritesEnabled { get; set; }
    public bool AutomaticRecoveryEnabled { get; set; }
    public bool AutomaticCutoverEnabled { get; set; }
    public bool GenerationReadersEnabled { get; set; }
}

public static class MarketplaceGenerationRegistration
{
    public static IServiceCollection AddMarketplaceGenerationCoordination(this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddOptions<MarketplaceGenerationOptions>()
            .Bind(configuration.GetSection("Marketplace:Generation"))
            .Validate(o => !o.ExternalWritesEnabled && !o.AutomaticRecoveryEnabled &&
                !o.AutomaticCutoverEnabled && !o.GenerationReadersEnabled,
                "Generation activation requires independent provider certification and separate release authorization; APP.2 cannot enable it.")
            .ValidateOnStart();
        services.AddSingleton<MarketplaceGenerationCoordinator>();
        services.AddScoped<IMarketplaceGenerationReads, SqlMarketplaceGenerationReads>();
        services.AddScoped<IMarketplaceGenerationMutationRouter, MarketplaceGenerationMutationRouter>();
        services.AddSingleton<MarketplaceGenerationRunDelivery>();
        services.AddSingleton<MarketplaceGenerationTransition>();
        services.AddSingleton<MarketplaceGenerationRollback>();
        services.AddSingleton<MarketplaceGenerationRetention>();
        return services;
    }
}

/// <summary>
/// Explicitly inert application facade. Durable lifecycle algorithms are independently
/// testable; this facade never interprets DI, a local receipt or configuration as proof.
/// </summary>
public sealed class MarketplaceGenerationCoordinator(IOptions<MarketplaceGenerationOptions> options)
{
    public bool IsInactive
    {
        get
        {
            var value = options.Value; // Executes configured fail-closed validation.
            return !value.ExternalWritesEnabled && !value.AutomaticRecoveryEnabled &&
                !value.AutomaticCutoverEnabled && !value.GenerationReadersEnabled;
        }
    }

    public Task ActivateAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _ = IsInactive;
        throw new InvalidOperationException("GENERATION_RELEASE_AUTHORIZATION_REQUIRED");
    }
}

internal enum GenerationLifecycleState { Building, Verified, Active, Retained, Retiring, Retired }
internal enum GenerationPointerOutcome { Intent, Committed, Completed, Superseded }
internal enum GenerationRecoveryAction { RetryCompareAndSwap, CompleteCommitted, AlreadyComplete, RejectForeignPointer }

/// <summary>Pure decisions only, not evidence of PostgreSQL/OpenSearch behavior.</summary>
internal static class MarketplaceGenerationLifecycle
{
    internal static void RequireTransition(GenerationLifecycleState from, GenerationLifecycleState to)
    {
        if ((from, to) is not
            ((GenerationLifecycleState.Building, GenerationLifecycleState.Verified) or
             (GenerationLifecycleState.Verified, GenerationLifecycleState.Active) or
             (GenerationLifecycleState.Active, GenerationLifecycleState.Retained) or
             (GenerationLifecycleState.Retained, GenerationLifecycleState.Verified) or
             (GenerationLifecycleState.Retained, GenerationLifecycleState.Retiring) or
             (GenerationLifecycleState.Verified, GenerationLifecycleState.Retiring) or
             (GenerationLifecycleState.Retiring, GenerationLifecycleState.Retired)))
            throw new InvalidOperationException("Illegal generation lifecycle transition.");
    }

    internal static GenerationRecoveryAction Recover(GenerationPointerOutcome outcome, long expectedVersion,
        long pointerVersion, Guid target, Guid? active, Guid operation, Guid? pointerOperation)
    {
        if (expectedVersion < 0 || pointerVersion < 0 || target == Guid.Empty || operation == Guid.Empty)
            throw new ArgumentException("Exact durable pointer/operation identity required.");
        if (outcome == GenerationPointerOutcome.Completed)
            return GenerationRecoveryAction.AlreadyComplete;
        if (outcome == GenerationPointerOutcome.Superseded)
            return GenerationRecoveryAction.RejectForeignPointer;
        if (expectedVersion < long.MaxValue && pointerVersion == expectedVersion + 1 && active == target && pointerOperation == operation)
            return GenerationRecoveryAction.CompleteCommitted;
        if (outcome == GenerationPointerOutcome.Intent && pointerVersion == expectedVersion)
            return GenerationRecoveryAction.RetryCompareAndSwap;
        return GenerationRecoveryAction.RejectForeignPointer;
    }

    internal static void RequireRetirement(bool active, bool previous, bool retentionElapsed,
        long validPins, long admittedReaders, long outstandingWrites, long pendingDeliveries, bool replayComplete,
        bool refreshComplete, bool physicalIdentityVerified)
    {
        if (new[] { validPins, admittedReaders, outstandingWrites, pendingDeliveries }.Any(n => n < 0))
            throw new ArgumentOutOfRangeException(nameof(validPins));
        if (active || previous || !retentionElapsed || validPins != 0 || admittedReaders != 0 ||
            outstandingWrites != 0 || pendingDeliveries != 0 || !replayComplete || !refreshComplete || !physicalIdentityVerified)
            throw new InvalidOperationException("Generation remains referenced or has unfinished work.");
    }
}
