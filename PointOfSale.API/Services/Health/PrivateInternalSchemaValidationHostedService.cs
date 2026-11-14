using Microsoft.EntityFrameworkCore;
using PointOfSale.Infrastructure.Data;
using PointOfSale.Infrastructure.Operations;

namespace PointOfSale.API.Services.Health;

/// <summary>Unlike the legacy diagnostic observer, await schema admission and propagate any failure.</summary>
public sealed class PrivateInternalSchemaValidationHostedService(IServiceScopeFactory scopeFactory) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var pending = await db.Database.GetPendingMigrationsAsync(cancellationToken);
        if (pending.Any())
            throw new InvalidOperationException("Private internal schema admission requires an already prepared schema; migrations are not permitted.");
        var applied = await db.Database.GetAppliedMigrationsAsync(cancellationToken);
        var latest = applied.LastOrDefault();
        await scope.ServiceProvider.GetRequiredService<ISystemSchemaVersionService>().RecordAsync(
            "postgres", string.IsNullOrWhiteSpace(latest) ? "manual-sql" : latest, cancellationToken: cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
