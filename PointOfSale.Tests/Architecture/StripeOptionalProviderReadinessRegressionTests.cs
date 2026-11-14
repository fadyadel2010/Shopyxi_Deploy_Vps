using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using PointOfSale.API.Configuration;
using PointOfSale.API.HealthChecks;
using PointOfSale.API.Services.Payments.Stripe;
using PointOfSale.Core.Entities;
using PointOfSale.Infrastructure.Data;

namespace PointOfSale.Tests.Architecture;

public sealed class StripeOptionalProviderReadinessRegressionTests
{
    [Fact]
    public async Task DisabledWithoutCredentialsOrTenants_DoesNotResolveAnyPersistenceScope()
    {
        var scopes = new Mock<IServiceScopeFactory>(MockBehavior.Strict);
        var check = new StripeOperationalReadinessHealthCheck(
            new StripeFoundationHealthContributor(Options.Create(new StripeOptions())), scopes.Object);
        var result = await check.CheckHealthAsync(new HealthCheckContext());
        Assert.Equal(HealthStatus.Healthy, result.Status);
        Assert.Equal(false, result.Data["enabled"]);
        scopes.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task EnabledWithValidGlobalAndProtectedTenantConfiguration_IsHealthy()
    {
        using var fixture = new Fixture(EnabledOptions());
        await fixture.AddTenantAsync();
        Assert.Equal(HealthStatus.Healthy, (await fixture.CheckAsync()).Status);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("malformed")]
    [InlineData("wrong-environment")]
    [InlineData("short")]
    public async Task EnabledWithInvalidGlobalCredential_FailsClosed(string kind)
    {
        var options = EnabledOptions();
        options = options with { SecretKey = kind switch { "missing" => "", "malformed" => "invalid", "wrong-environment" => "sk_live_" + Guid.NewGuid().ToString("N"), _ => "rk_test_x" } };
        using var fixture = new Fixture(options);
        await fixture.AddTenantAsync();
        var result = await fixture.CheckAsync();
        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Equal(false, result.Data["credentialHealth"]);
        Assert.False(new StripeOptionsValidator().Validate(null, options).Succeeded);
    }

    [Fact]
    public async Task TenantFeatureFlagEnablementCannotBeTreatedAsDisabled()
    {
        using var fixture = new Fixture(EnabledOptions() with { Enabled = false });
        Assert.Equal(HealthStatus.Unhealthy, (await fixture.CheckAsync()).Status);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("bad-ciphertext")]
    [InlineData("bad-json")]
    [InlineData("missing-key")]
    [InlineData("wrong-environment")]
    public async Task EnabledWithInvalidProtectedTenantConfiguration_FailsClosed(string kind)
    {
        using var fixture = new Fixture(EnabledOptions());
        await fixture.AddTenantAsync(kind);
        var result = await fixture.CheckAsync();
        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Equal(false, result.Data["tenantConfigurationHealth"]);
    }

    [Fact]
    public async Task EnabledWithoutTenants_IsUnhealthy()
    {
        using var fixture = new Fixture(EnabledOptions());
        Assert.Equal(HealthStatus.Unhealthy, (await fixture.CheckAsync()).Status);
    }

    [Fact]
    public async Task OneValidTenantMustNotHideAnotherInvalidEnabledTenant()
    {
        using var fixture = new Fixture(EnabledOptions());
        await fixture.AddTenantAsync();
        await fixture.AddTenantAsync("bad-ciphertext");
        Assert.Equal(HealthStatus.Unhealthy, (await fixture.CheckAsync()).Status);
    }

    [Fact]
    public async Task RepairingTenantConfigurationRestoresReadinessWithoutRestart()
    {
        using var fixture = new Fixture(EnabledOptions());
        var id = await fixture.AddTenantAsync("bad-ciphertext");
        Assert.Equal(HealthStatus.Unhealthy, (await fixture.CheckAsync()).Status);
        await fixture.RepairAsync(id);
        Assert.Equal(HealthStatus.Healthy, (await fixture.CheckAsync()).Status);
    }

    [Theory]
    [InlineData("postgresql")]
    [InlineData("mongodb")]
    [InlineData("redis")]
    [InlineData("rabbitmq")]
    [InlineData("elasticsearch")]
    [InlineData("data_protection")]
    public async Task DisabledStripeNeverMasksMandatoryFailureOrRecovery(string dependency)
    {
        using var fixture = new Fixture(new StripeOptions(), dependency);
        var service = fixture.Services.GetRequiredService<HealthCheckService>();
        fixture.MandatoryState = HealthCheckResult.Healthy();
        Assert.Equal(HealthStatus.Healthy, (await service.CheckHealthAsync(r => r.Tags.Contains("ready"))).Status);
        fixture.MandatoryState = HealthCheckResult.Degraded("Controlled dependency degradation.");
        Assert.Equal(HealthStatus.Degraded, (await service.CheckHealthAsync(r => r.Tags.Contains("ready"))).Status);
        fixture.MandatoryState = HealthCheckResult.Unhealthy("Controlled dependency outage.");
        Assert.Equal(HealthStatus.Unhealthy, (await service.CheckHealthAsync(r => r.Tags.Contains("ready"))).Status);
        fixture.MandatoryState = HealthCheckResult.Healthy();
        Assert.Equal(HealthStatus.Healthy, (await service.CheckHealthAsync(r => r.Tags.Contains("ready"))).Status);
    }

    [Fact]
    public void ActualProgramRetainsReadyEndpointAndMandatoryDependencyRegistrations()
    {
        var source = CertificationSource.ReadRepoFile("PointOfSale.API/Program.cs");
        foreach (var name in new[] { "postgresql", "redis", "mongodb", "elasticsearch", "rabbitmq", "stripe_operational_readiness" })
            Assert.Contains("\"" + name + "\"", source, StringComparison.Ordinal);
        Assert.Contains("Predicate = check => check.Tags.Contains(\"ready\")", source, StringComparison.Ordinal);
        var dp = CertificationSource.ReadRepoFile("PointOfSale.API/Configuration/ProductionDataProtection.cs");
        Assert.Contains("empty-directory initialization is not permitted", dp, StringComparison.Ordinal);
    }

    private static StripeOptions EnabledOptions() => new()
    {
        Enabled = true,
        PublishableKey = "pk_test_" + Guid.NewGuid().ToString("N"),
        SecretKey = "rk_test_" + Guid.NewGuid().ToString("N"),
        WebhookSecret = "whsec_" + Guid.NewGuid().ToString("N"),
        FeatureFlags = new StripeFeatureFlags { Enabled = true, SandboxEnabled = true, PaymentIntentsEnabled = true, WebhooksEnabled = true }
    };

    private sealed class Fixture : IDisposable
    {
        public ServiceProvider Services { get; }
        public HealthCheckResult MandatoryState { get; set; } = HealthCheckResult.Healthy();
        private readonly IDataProtectionProvider _protection = new EphemeralDataProtectionProvider();

        public Fixture(StripeOptions options, string dependency = "postgresql")
        {
            var services = new ServiceCollection();
            services.AddLogging();
            var root = new InMemoryDatabaseRoot();
            var name = Guid.NewGuid().ToString("N");
            services.AddDbContext<ApplicationDbContext>(o => o.UseInMemoryDatabase(name, root));
            services.AddSingleton<IDataProtectionProvider>(_protection);
            services.AddSingleton<IOptions<StripeOptions>>(Options.Create(options));
            services.AddSingleton<IStripeFoundationHealthContributor, StripeFoundationHealthContributor>();
            services.AddScoped<IStripeTenantConfigurationResolver, StripeTenantConfigurationResolver>();
            services.AddSingleton(Mock.Of<IStripePaymentService>());
            services.AddSingleton(Mock.Of<IStripeRuntimeBridge>());
            services.AddSingleton(Mock.Of<IStripeRuntimeApplicationService>());
            services.AddSingleton(Mock.Of<IStripeWebhookRuntimeIntegrationService>());
            services.AddSingleton(Mock.Of<IStripeWebhookEvidenceAdapter>());
            services.AddHealthChecks()
                .AddCheck<StripeOperationalReadinessHealthCheck>("stripe_operational_readiness", tags: new[] { "ready" })
                .AddCheck(dependency, () => MandatoryState, tags: new[] { "ready" });
            Services = services.BuildServiceProvider(validateScopes: true);
        }

        public async Task<HealthCheckResult> CheckAsync()
        {
            using var scope = Services.CreateScope();
            var check = ActivatorUtilities.CreateInstance<StripeOperationalReadinessHealthCheck>(scope.ServiceProvider);
            return await check.CheckHealthAsync(new HealthCheckContext());
        }

        private string ProtectedConfig(string kind = "valid")
        {
            if (kind == "missing") return "";
            if (kind == "bad-ciphertext") return "not-protected-data";
            var config = new Dictionary<string, string>
            {
                ["publishableKey"] = "pk_test_" + Guid.NewGuid().ToString("N"),
                ["secretKey"] = (kind == "wrong-environment" ? "sk_live_" : "rk_test_") + Guid.NewGuid().ToString("N"),
                ["webhookSecret"] = "whsec_" + Guid.NewGuid().ToString("N")
            };
            if (kind == "missing-key") config.Remove("secretKey");
            return _protection.CreateProtector("PaymentMethodSecrets").Protect(kind == "bad-json" ? "{" : JsonSerializer.Serialize(config));
        }

        public async Task<Guid> AddTenantAsync(string kind = "valid")
        {
            using var scope = Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var method = new TenantPaymentMethod { Id = Guid.NewGuid(), TenantId = Guid.NewGuid(), Code = "STRIPE", DisplayName = "Synthetic isolated regression", IsEnabled = true, IsSandbox = true, ConfigEncrypted = ProtectedConfig(kind) };
            db.TenantPaymentMethods.Add(method); await db.SaveChangesAsync(); return method.Id;
        }

        public async Task RepairAsync(Guid id)
        {
            using var scope = Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var method = await db.TenantPaymentMethods.IgnoreQueryFilters().SingleAsync(x => x.Id == id);
            method.ConfigEncrypted = ProtectedConfig(); await db.SaveChangesAsync();
        }

        public void Dispose() => Services.Dispose();
    }
}
