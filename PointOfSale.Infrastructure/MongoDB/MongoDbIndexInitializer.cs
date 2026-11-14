using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PointOfSale.Infrastructure.MongoDB.Services;

namespace PointOfSale.Infrastructure.MongoDB;

/// <summary>
/// Hosted service that ensures MongoDB indexes are created at application startup.
/// This runs once when the application starts and ensures all required indexes exist.
/// 
/// Index creation is idempotent - safe to run multiple times across multiple application instances.
/// </summary>
public class MongoDbIndexInitializer : IHostedService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<MongoDbIndexInitializer> _logger;

    public MongoDbIndexInitializer(
        IServiceProvider serviceProvider,
        ILogger<MongoDbIndexInitializer> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken) => RunInitializationAsync(cancellationToken);

    private async Task RunInitializationAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("MongoDB Index Initializer starting...");

        try
        {
            // Get MongoDbContext from DI (singleton)
            var context = _serviceProvider.GetRequiredService<MongoDbContext>();
            
            // Create indexes
            await context.EnsureIndexesCreatedAsync(cancellationToken);
            
            _logger.LogInformation("MongoDB Index Initializer completed successfully");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "MongoDB required index initialization failed; startup is refused.");
            throw;
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        // Nothing to clean up
        return Task.CompletedTask;
    }
}
