using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using PointOfSale.Infrastructure.Configuration;

namespace PointOfSale.API.Configuration;

/// <summary>
/// Production business processing is independent of database/search provisioning.
/// Runtime credentials observe already-provisioned schema and indexes; seeders
/// belong to explicitly authorized setup operations, not every process restart.
/// Recorded financial history is likewise outside process-startup authority:
/// hosted services that rewrite financial aggregate rows are never started
/// automatically. Window recomputation happens only through explicitly
/// authorized operations (ledger reconciliation comparisons, or an authorized
/// operator-invoked refresh) so admission can observe stored aggregates and
/// reconciliation drift findings stay meaningful.
/// </summary>
public static class ProductionStartupSafetyPolicy
{
    private static readonly HashSet<Type> ProvisioningServices =
    [
        typeof(PointOfSale.API.SuperAdmin.Services.SuperAdminRootSeeder),
        typeof(PointOfSale.API.Services.MarketplaceCms.MarketplaceCmsPageSeeder),
        typeof(PointOfSale.Infrastructure.Accounting.ChartOfAccountsStartupSeeder),
        typeof(PointOfSale.Infrastructure.Elasticsearch.ElasticsearchIndexInitializer),
        typeof(PointOfSale.Infrastructure.Elasticsearch.Services.CommerceHubElasticsearchIndexInitializer)
    ];

    /// <summary>
    /// Automatic startup rewrite of <c>CommerceHubDailyFinancialAggregates</c>
    /// (365-day backfill plus rolling windows) mutates recorded rows on every
    /// boot; financial history may only be recomputed by authorized operations.
    /// </summary>
    private static readonly HashSet<Type> HistoryMutationServices =
    [
        typeof(PointOfSale.API.Services.CommerceHub.CommerceHubDailyFinancialAggregateRefreshService)
    ];

    public static int Apply(IServiceCollection services, IHostEnvironment environment,
        RuntimeAdmissionPolicy admission)
    {
        if (!environment.IsProduction() || !admission.BusinessProcessingEnabled)
            return 0;

        var removed = services.Where(descriptor => descriptor.ServiceType == typeof(IHostedService) &&
            descriptor.ImplementationType is { } type &&
            (ProvisioningServices.Contains(type) || HistoryMutationServices.Contains(type))).ToArray();
        foreach (var descriptor in removed)
            services.Remove(descriptor);

        // Admission must finish before transport consumers or any business worker
        // starts. Appending validators after the workers would be fail-open.
        var validators = new[]
        {
            typeof(PointOfSale.API.Services.Health.PrivateInternalSchemaValidationHostedService),
            typeof(PointOfSale.Infrastructure.Elasticsearch.PrivateInternalSearchValidationHostedService)
        };
        foreach (var descriptor in services.Where(descriptor =>
                     descriptor.ServiceType == typeof(IHostedService) &&
                     descriptor.ImplementationType is { } type && validators.Contains(type)).ToArray())
            services.Remove(descriptor);
        var firstHostedService = services.ToList().FindIndex(descriptor => descriptor.ServiceType == typeof(IHostedService));
        var index = firstHostedService < 0 ? services.Count : firstHostedService;
        foreach (var validator in validators)
            services.Insert(index++, ServiceDescriptor.Singleton(typeof(IHostedService), validator));
        services.Configure<HostOptions>(options => options.ServicesStartConcurrently = false);
        return removed.Length;
    }

    public static int ResolveHangfireWorkerCount(IConfiguration configuration, IHostEnvironment environment)
    {
        var count = configuration.GetValue("Hangfire:WorkerCount", environment.IsProduction() ? 2 : 4);
        if (count is < 1 or > 8)
            throw new InvalidOperationException("Hangfire:WorkerCount must be between 1 and 8.");
        return count;
    }
}
