using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using PointOfSale.API.Configuration;
using PointOfSale.Core.Entities;
using PointOfSale.Core.Tenancy;
using PointOfSale.Infrastructure.Configuration;
using PointOfSale.Infrastructure.Data;
using PointOfSale.Infrastructure.Operations;

namespace PointOfSale.Tests.Security;

public sealed class ProductionStartupSafetyPolicyTests
{
    [Fact]
    public void ProductionRetainsBusinessWorkersButRemovesProvisioningAndHistoryRewrites()
    {
        IServiceCollection services = new ServiceCollection();
        services.AddHostedService<PointOfSale.API.SuperAdmin.Services.SuperAdminRootSeeder>();
        services.AddHostedService<PointOfSale.API.Services.MarketplaceCms.MarketplaceCmsPageSeeder>();
        services.AddHostedService<PointOfSale.Infrastructure.Accounting.ChartOfAccountsStartupSeeder>();
        services.AddHostedService<PointOfSale.Infrastructure.Elasticsearch.ElasticsearchIndexInitializer>();
        services.AddHostedService<PointOfSale.Infrastructure.Elasticsearch.Services.CommerceHubElasticsearchIndexInitializer>();
        services.AddHostedService<PointOfSale.API.Jobs.HangfireInitializationHostedService>();
        services.AddHostedService<PointOfSale.API.Services.CommerceHub.CommerceHubDailyFinancialAggregateRefreshService>();
        var factoryCalled = false;
        services.AddHostedService<BusinessWorker>(_ => { factoryCalled = true; return new(); });
        Assert.Equal(6, ProductionStartupSafetyPolicy.Apply(services, Environment("Production"), new(false)));
        Assert.False(factoryCalled);
        var hosted = services.Where(d => d.ServiceType == typeof(IHostedService)).ToArray();
        Assert.Equal(typeof(PointOfSale.API.Services.Health.PrivateInternalSchemaValidationHostedService), hosted[0].ImplementationType);
        Assert.Equal(typeof(PointOfSale.Infrastructure.Elasticsearch.PrivateInternalSearchValidationHostedService), hosted[1].ImplementationType);
        Assert.Contains(hosted, d => d.ImplementationType == typeof(PointOfSale.API.Jobs.HangfireInitializationHostedService));
        Assert.DoesNotContain(hosted, d => d.ImplementationType == typeof(PointOfSale.API.Services.CommerceHub.CommerceHubDailyFinancialAggregateRefreshService));
        Assert.Contains(hosted, d => d.ImplementationFactory != null);
        using var provider = services.BuildServiceProvider();
        Assert.False(provider.GetRequiredService<IOptions<HostOptions>>().Value.ServicesStartConcurrently);
        Assert.Equal(0, ProductionStartupSafetyPolicy.Apply(services, Environment("Production"), new(false)));
        Assert.Equal(hosted.Length, services.Count(d => d.ServiceType == typeof(IHostedService)));
    }

    [Fact]
    public void ProductionStartupMustNotAutomaticallyRewriteRecordedFinancialHistory()
    {
        // The observed full-mode startup rewrote a year of CommerceHubDailyFinancialAggregates
        // rows (1 seeded row replaced by a 365-row backfill). Admission must never start that
        // rewrite; reconciliation comparison and authorized operator refreshes remain available.
        IServiceCollection services = new ServiceCollection();
        services.AddHostedService<PointOfSale.API.Services.CommerceHub.CommerceHubDailyFinancialAggregateRefreshService>();
        Assert.Equal(1, ProductionStartupSafetyPolicy.Apply(services, Environment("Production"), new(false)));
        Assert.DoesNotContain(services, d => d.ServiceType == typeof(IHostedService) &&
            d.ImplementationType == typeof(PointOfSale.API.Services.CommerceHub.CommerceHubDailyFinancialAggregateRefreshService));
    }

    [Theory]
    [InlineData("Development", false)]
    [InlineData("Testing", false)]
    [InlineData("Production", true)]
    public void NonProductionAndPrivatePoliciesRemainUnchanged(string environment, bool privateOnly)
    {
        IServiceCollection services = new ServiceCollection();
        services.AddHostedService<PointOfSale.API.SuperAdmin.Services.SuperAdminRootSeeder>();
        services.AddHostedService<PointOfSale.API.Services.CommerceHub.CommerceHubDailyFinancialAggregateRefreshService>();
        var before = services.ToArray();
        Assert.Equal(0, ProductionStartupSafetyPolicy.Apply(services, Environment(environment), new(privateOnly)));
        Assert.Equal(before, services);
    }

    [Theory]
    [InlineData("Production", null, 2)]
    [InlineData("Development", null, 4)]
    [InlineData("Production", "1", 1)]
    [InlineData("Production", "8", 8)]
    public void HangfireConcurrencyIsBounded(string environment, string? value, int expected) =>
        Assert.Equal(expected, ProductionStartupSafetyPolicy.ResolveHangfireWorkerCount(Config(value), Environment(environment)));

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("9")]
    [InlineData("100")]
    [InlineData("invalid")]
    public void InvalidWorkerCountsFailStartup(string value) =>
        Assert.Throws<InvalidOperationException>(() => ProductionStartupSafetyPolicy.ResolveHangfireWorkerCount(Config(value), Environment("Production")));

    [Theory]
    [InlineData("commercehub")]
    [InlineData("CommerceHub")]
    [InlineData("domains")]
    [InlineData("api")]
    [InlineData("www")]
    public void PlatformHostLabelsCannotBecomeTenantSlugs(string slug) =>
        Assert.False(StoreSlug.TryNormalize(slug, out _, out _));

    [Theory]
    [InlineData("app82-e2e-one")]
    [InlineData("app82-e2e-two")]
    public void SyntheticTenantSlugsStillWork(string slug) =>
        Assert.True(StoreSlug.TryNormalize(slug, out _, out _));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProductionSchemaMetadataIsAlwaysReadOnly(bool privateOnly)
    {
        await using var db = NewDb();
        var stamp = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        db.Set<SystemSchemaVersion>().Add(new() { Id = Guid.NewGuid(), Component = "postgres", Version = "accepted", AppliedAt = stamp, CreatedAt = stamp, DetailsJson = "{}" });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var saves = db.Saves;
        var service = new SystemSchemaVersionService(db, new(privateOnly), Environment("Production"));
        await service.RecordAsync("postgres", "accepted", new { mustNotPersist = true });
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RecordAsync("postgres", "wrong"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RecordAsync("missing", "accepted"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RecordAsync("", "accepted"));
        var row = await db.Set<SystemSchemaVersion>().AsNoTracking().SingleAsync();
        Assert.Equal(stamp, row.AppliedAt);
        Assert.Equal("{}", row.DetailsJson);
        Assert.Equal(saves, db.Saves);
        Assert.Empty(db.ChangeTracker.Entries());
    }

    [Fact]
    public async Task DevelopmentProvisioningRemainsAvailable()
    {
        await using var db = NewDb();
        await new SystemSchemaVersionService(db, new(false), Environment("Development")).RecordAsync("component", "version");
        Assert.Equal(1, db.Saves);
        Assert.Single(await db.Set<SystemSchemaVersion>().ToArrayAsync());
    }

    private static IConfiguration Config(string? value) => new ConfigurationBuilder().AddInMemoryCollection(
        new Dictionary<string, string?> { ["Hangfire:WorkerCount"] = value }).Build();
    private static IHostEnvironment Environment(string name) => new TestEnvironment { EnvironmentName = name };
    private static MetadataDb NewDb() => new(new DbContextOptionsBuilder<ApplicationDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private sealed class TestEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Production";
        public string ApplicationName { get; set; } = "Certification";
        public string ContentRootPath { get; set; } = ".";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
    private sealed class BusinessWorker : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
    private sealed class MetadataDb(DbContextOptions<ApplicationDbContext> options) : ApplicationDbContext(options)
    {
        public int Saves { get; private set; }
        protected override void OnModelCreating(ModelBuilder builder)
        {
            foreach (var type in builder.Model.GetEntityTypes().Where(x => x.ClrType != typeof(SystemSchemaVersion)).ToArray())
                builder.Ignore(type.ClrType);
            builder.Entity<SystemSchemaVersion>().HasKey(x => x.Id);
        }
        public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            Saves++;
            return base.SaveChangesAsync(cancellationToken);
        }
    }
}
