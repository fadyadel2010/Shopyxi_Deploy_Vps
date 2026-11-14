using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PointOfSale.Infrastructure.Eventing;

namespace PointOfSale.Infrastructure.Extensions;

public static class PosCartAuditOutboxExtensions
{
    public static IServiceCollection AddPosCartAuditOutbox(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<PosCartAuditOutboxOptions>()
            .Bind(configuration.GetSection(PosCartAuditOutboxOptions.SectionName))
            .Validate(o => o.BatchSize is >= 1 and <= 500, "POS cart audit batch size must be 1..500.")
            .Validate(o => o.PollIntervalSeconds is >= 1 and <= 300, "POS cart audit interval must be 1..300 seconds.")
            .Validate(o => !o.Enabled || (o.TenantIds.Length > 0 && o.TenantIds.All(id => id != Guid.Empty)),
                "Enabled POS cart audit requires explicit nonempty tenant IDs.")
            .ValidateOnStart();
        services.AddSingleton<PosCartAuditOutboxProcessor>();
        services.AddHostedService<PosCartAuditOutboxWorker>();
        return services;
    }
}
