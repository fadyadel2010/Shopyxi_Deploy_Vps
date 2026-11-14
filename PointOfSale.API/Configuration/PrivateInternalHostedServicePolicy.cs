using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using PointOfSale.Infrastructure.Configuration;

namespace PointOfSale.API.Configuration;

/// <summary>
/// Private admission retains real transport/health/index validation, not a test Host.
/// Unknown application workers fail closed: registration never starts their factories.
/// </summary>
public static class PrivateInternalHostedServicePolicy
{
    private static readonly HashSet<Type> ReadOnlyInfrastructure = new()
    {
        typeof(PointOfSale.Infrastructure.MongoDB.MongoDbIndexInitializer),
        typeof(PointOfSale.Infrastructure.MongoDB.CommerceHubMongoDbIndexInitializer),
        typeof(PointOfSale.Infrastructure.MongoDB.MarketplaceReadModelIndexInitializer),
        typeof(PointOfSale.API.Services.Health.CommerceHubPlatformStartupValidator),
        typeof(PointOfSale.Infrastructure.Elasticsearch.PrivateInternalSearchValidationHostedService),
        typeof(PointOfSale.API.Services.Health.PrivateInternalSchemaValidationHostedService)
    };

    public static int Apply(
        IServiceCollection services,
        RuntimeAdmissionPolicy admission,
        IReadOnlyCollection<ServiceDescriptor> transportHostedServices)
    {
        if (admission.BusinessProcessingEnabled) return 0;
        if (transportHostedServices.Count == 0 || transportHostedServices.Any(d =>
                d.ServiceType != typeof(IHostedService) || !services.Contains(d)))
            throw new InvalidOperationException("Private admission must retain registered live transport services.");

        var withheld = services.Where(d => d.ServiceType == typeof(IHostedService) &&
            !transportHostedServices.Contains(d) &&
            !(d.ImplementationType is { } type && ReadOnlyInfrastructure.Contains(type))).ToArray();
        foreach (var descriptor in withheld) services.Remove(descriptor);
        return withheld.Length;
    }
}
