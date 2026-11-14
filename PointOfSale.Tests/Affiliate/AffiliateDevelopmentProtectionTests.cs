using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using PointOfSale.API.Development;
using PointOfSale.Tests.Infrastructure;
using PointOfSale.Infrastructure.Services.Affiliate;

namespace PointOfSale.Tests.Affiliate;

public sealed class AffiliateDevelopmentProtectionTests
{
    [Theory]
    [InlineData("appsettings.json")]
    [InlineData("appsettings.Development.json")]
    public void NormalSettings_DoNotOptIntoManualFinancialOverrides(string file)
    {
        var directory = Path.Combine(TestConfiguration.GetSolutionRoot(), "PointOfSale.API");
        var builder = new ConfigurationBuilder().SetBasePath(directory).AddJsonFile("appsettings.json");
        if (File.Exists(Path.Combine(directory, file))) builder.AddJsonFile(file);
        else
        {
            // This staged source deliberately carries no Development overlay.
            // Absence must use the mandatory base policy, not a made-up file or
            // a skipped assertion. A missing base configuration is still fatal.
            Assert.Equal("appsettings.Development.json", file);
            Assert.True(File.Exists(Path.Combine(directory, "appsettings.json")));
        }
        var configuration = builder.Build();
        var settlement = configuration.GetSection("AffiliateSettlement").Get<AffiliateSettlementOptions>()!;
        Assert.True(settlement.RequireVerifiedExternalPayoutEvidence);
        Assert.True(settlement.RequireVerifiedManualDeliveryEvidence);
        Assert.False(settlement.AllowManualPayoutEvidenceOverride);
        Assert.False(settlement.AllowManualDeliveryEvidenceOverride);
    }

#if DEBUG
    [Theory]
    [InlineData("Development", false, false)]
    [InlineData("Development", true, true)]
    [InlineData("Production", false, false)]
    [InlineData("Production", true, false)]
    [InlineData("Staging", false, false)]
    [InlineData("Staging", true, false)]
    [InlineData("Test", false, false)]
    [InlineData("Test", true, false)]
    public void Phase52RegistrationRequiresTheActualDevelopmentMarkerAndDoesNotConstructAService(string runtime, bool marker, bool expected)
    {
        var root = Path.Combine(TestConfiguration.GetSharedTempRoot(), "phase52-registration-" + Guid.NewGuid().ToString("N"));
        Assert.False(Directory.Exists(root));
        var content = Path.Combine(root, "api");
        var evidence = Path.Combine(root, ".ai", "evidence", "affiliate-p52-20261022");
        Directory.CreateDirectory(content);
        try
        {
            if (marker)
            {
                Directory.CreateDirectory(evidence);
                File.WriteAllText(Path.Combine(evidence, "runtime.enabled"), "Fictional registration-only fixture; never start a driver.");
            }
            var environment = Mock.Of<IWebHostEnvironment>(e => e.EnvironmentName == runtime && e.ContentRootPath == content);
            var services = new ServiceCollection();
            // Real production registration helper; inspect descriptors only.
            // No provider is built, so this cannot resolve the hardcoded SQL
            // identity or execute any Development command, even for the positive.
            AffiliatePhase52LocalDriverRegistration.AddIfEnabled(services, environment);
            AffiliatePhase52LocalDriverRegistration.AddIfEnabled(services, environment);
            if (expected)
            {
                var descriptor = Assert.Single(services);
                Assert.Equal(typeof(Microsoft.Extensions.Hosting.IHostedService), descriptor.ServiceType);
                Assert.Equal(typeof(AffiliatePhase52LocalDriver), descriptor.ImplementationType);
                Assert.Equal(ServiceLifetime.Singleton, descriptor.Lifetime);
            }
            else Assert.Empty(services);
        }
        finally { Directory.Delete(root, recursive: true); } // exact newly-created task-owned random directory only
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    [InlineData("Test")]
    public async Task LocalDrivers_RejectOtherRuntimesBeforeResolvingServices(string runtime)
    {
        var scopes = new Mock<IServiceScopeFactory>(MockBehavior.Strict);
        var environment = Mock.Of<IWebHostEnvironment>(e => e.EnvironmentName == runtime);
        using var phase51 = new AffiliatePhase51LocalDriver(scopes.Object, environment, NullLogger<AffiliatePhase51LocalDriver>.Instance);
        using var phase52 = new AffiliatePhase52LocalDriver(scopes.Object, environment, NullLogger<AffiliatePhase52LocalDriver>.Instance);
        var first = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await phase51.StartAsync(default);
            Assert.NotNull(phase51.ExecuteTask);
            await phase51.ExecuteTask.WaitAsync(TimeSpan.FromSeconds(5));
        });
        var second = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await phase52.StartAsync(default);
            Assert.NotNull(phase52.ExecuteTask);
            await phase52.ExecuteTask.WaitAsync(TimeSpan.FromSeconds(5));
        });
        Assert.Equal("Development only", first.Message);
        Assert.Equal("Development only", second.Message);
        scopes.VerifyNoOtherCalls();
        Assert.False(typeof(ControllerBase).IsAssignableFrom(typeof(AffiliatePhase51LocalDriver)));
        Assert.False(typeof(ControllerBase).IsAssignableFrom(typeof(AffiliatePhase52LocalDriver)));
    }
#endif
}
