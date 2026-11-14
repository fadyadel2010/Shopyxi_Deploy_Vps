using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using PointOfSale.Infrastructure.Marketplace.Generation;

namespace PointOfSale.Tests.Marketplace;

/// <summary>Architecture contracts only: no PostgreSQL or OpenSearch certification.</summary>
public sealed class MarketplaceGenerationLifecycleContractTests
{
    public static IEnumerable<object[]> Transitions() =>
        from before in Enum.GetValues<GenerationLifecycleState>()
        from after in Enum.GetValues<GenerationLifecycleState>()
        select new object[] { before, after };

    [Theory]
    [MemberData(nameof(Transitions))]
    public void LifecycleRejectsEveryUnreviewedTransition(object before, object after)
    {
        var from = (GenerationLifecycleState)before;
        var to = (GenerationLifecycleState)after;
        var allowed = (from, to) is
            (GenerationLifecycleState.Building, GenerationLifecycleState.Verified) or
            (GenerationLifecycleState.Verified, GenerationLifecycleState.Active) or
            (GenerationLifecycleState.Active, GenerationLifecycleState.Retained) or
            (GenerationLifecycleState.Retained, GenerationLifecycleState.Verified) or
            (GenerationLifecycleState.Retained, GenerationLifecycleState.Retiring) or
            (GenerationLifecycleState.Verified, GenerationLifecycleState.Retiring) or
            (GenerationLifecycleState.Retiring, GenerationLifecycleState.Retired);
        if (allowed) MarketplaceGenerationLifecycle.RequireTransition(from, to);
        else Assert.Throws<InvalidOperationException>(() => MarketplaceGenerationLifecycle.RequireTransition(from, to));
    }

    [Fact]
    public async Task CoordinatorIsRegisteredInactiveAndNoHostedWorkerIsAdded()
    {
        var services = new ServiceCollection();
        services.AddMarketplaceGenerationCoordination(new ConfigurationBuilder().Build());
        Assert.DoesNotContain(services, d => d.ServiceType == typeof(IHostedService));
        using var provider = services.BuildServiceProvider();
        var coordinator = provider.GetRequiredService<MarketplaceGenerationCoordinator>();
        Assert.True(coordinator.IsInactive);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.ActivateAsync());
        Assert.Equal("GENERATION_RELEASE_AUTHORIZATION_REQUIRED", error.Message);
    }

    [Theory]
    [InlineData("ExternalWritesEnabled")]
    [InlineData("AutomaticRecoveryEnabled")]
    [InlineData("AutomaticCutoverEnabled")]
    [InlineData("GenerationReadersEnabled")]
    public void ConfigurationCannotTurnProofOrReleaseAuthorizationIntoABoolean(string option)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["Marketplace:Generation:" + option] = "true" }).Build();
        var services = new ServiceCollection().AddMarketplaceGenerationCoordination(configuration);
        using var provider = services.BuildServiceProvider();
        Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<MarketplaceGenerationCoordinator>().IsInactive);
    }

    [Fact]
    public async Task CancelledActivationCannotBeginCoordination()
    {
        var coordinator = new MarketplaceGenerationCoordinator(Options.Create(new MarketplaceGenerationOptions()));
        using var cts = new CancellationTokenSource(); cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => coordinator.ActivateAsync(cts.Token));
    }

    [Theory]
    [InlineData(0, 7, 7, false, false, 0)]
    [InlineData(0, 7, 8, true, true, 1)]
    [InlineData(1, 7, 8, true, true, 1)]
    [InlineData(0, 7, 8, true, false, 3)]
    [InlineData(0, 7, 8, false, true, 3)]
    [InlineData(0, 7, 9, true, true, 3)]
    [InlineData(1, 7, 7, false, false, 3)]
    [InlineData(2, 7, 100, false, false, 2)]
    [InlineData(3, 7, 7, false, false, 3)]
    public void CrashRecoveryBindsPointerVersionTargetAndOperation(int state, long expected, long pointer,
        bool targetMatches, bool operationMatches, int action)
    {
        var target = Guid.NewGuid(); var operation = Guid.NewGuid();
        Assert.Equal((GenerationRecoveryAction)action, MarketplaceGenerationLifecycle.Recover((GenerationPointerOutcome)state,
            expected, pointer, target, targetMatches ? target : Guid.NewGuid(), operation, operationMatches ? operation : Guid.NewGuid()));
    }

    [Fact]
    public void RecoveryCannotOverflowPointerVersion()
    {
        Assert.Equal(GenerationRecoveryAction.RejectForeignPointer, MarketplaceGenerationLifecycle.Recover(
            GenerationPointerOutcome.Intent, long.MaxValue, 0, Guid.NewGuid(), null, Guid.NewGuid(), null));
    }

    [Theory]
    [InlineData("active")]
    [InlineData("previous")]
    [InlineData("retained")]
    [InlineData("pin")]
    [InlineData("reader")]
    [InlineData("write")]
    [InlineData("delivery")]
    [InlineData("replay")]
    [InlineData("refresh")]
    [InlineData("physical")]
    public void EachRetirementExclusionIndependentlyBlocksDeletion(string defect)
    {
        Assert.Throws<InvalidOperationException>(() => MarketplaceGenerationLifecycle.RequireRetirement(
            defect == "active", defect == "previous", defect != "retained", defect == "pin" ? 1 : 0,
            defect == "reader" ? 1 : 0, defect == "write" ? 1 : 0, defect == "delivery" ? 1 : 0,
            defect != "replay", defect != "refresh", defect != "physical"));
    }

    [Fact]
    public void RetirementAcceptsOnlyCompleteUnreferencedExpiredGeneration()
    {
        MarketplaceGenerationLifecycle.RequireRetirement(false, false, true, 0, 0, 0, 0, true, true, true);
        Assert.Throws<ArgumentOutOfRangeException>(() => MarketplaceGenerationLifecycle.RequireRetirement(
            false, false, true, -1, 0, 0, 0, true, true, true));
    }

    [Theory]
    [InlineData("tenant")]
    [InlineData("reader")]
    [InlineData("query")]
    [InlineData("pin")]
    [InlineData("journal")]
    [InlineData("generation")]
    [InlineData("version")]
    [InlineData("secret")]
    [InlineData("physical")]
    public void ReaderPinCannotBeReboundToForeignScopeOrPhysicalTarget(string defect)
    {
        var tenant = Guid.NewGuid(); var reader = Guid.NewGuid(); var generation = Guid.NewGuid();
        var pin = new GenerationReaderPin(Guid.NewGuid(), new string('a', 64), Guid.NewGuid(), generation, 1,
            tenant, reader, new string('b', 64), DateTime.UtcNow.AddMinutes(1), "marketplace_services_g_" + generation.ToString("N"));
        MarketplaceGenerationReaderPins.Validate(pin, tenant, reader, pin.QueryHash);
        pin = defect switch {
            "tenant" => pin with { Tenant = Guid.NewGuid() }, "reader" => pin with { Reader = Guid.NewGuid() },
            "query" => pin with { QueryHash = new string('c', 64) }, "pin" => pin with { PinId = Guid.Empty },
            "journal" => pin with { Journal = Guid.Empty }, "generation" => pin with { Generation = Guid.NewGuid() },
            "version" => pin with { PointerVersion = 0 }, "secret" => pin with { Secret = "invalid" },
            _ => pin with { PhysicalName = "marketplace_services" }
        };
        Assert.Throws<InvalidOperationException>(() => MarketplaceGenerationReaderPins.Validate(pin, tenant, reader, new string('b', 64)));
    }
}
