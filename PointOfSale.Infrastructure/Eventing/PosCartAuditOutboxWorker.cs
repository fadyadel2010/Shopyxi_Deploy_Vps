using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace PointOfSale.Infrastructure.Eventing;

public sealed class PosCartAuditOutboxWorker(
    PosCartAuditOutboxProcessor processor,
    IOptions<PosCartAuditOutboxOptions> options,
    ILogger<PosCartAuditOutboxWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled) return;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(options.Value.PollIntervalSeconds));
        do
        {
            foreach (var tenantId in options.Value.TenantIds.Distinct())
            {
                try { await processor.ProcessTenantAsync(tenantId, stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
                catch (Exception ex)
                {
                    logger.LogError("POS cart audit worker cycle failed for tenant {TenantId}: {ErrorType}",
                        tenantId, ex.GetType().Name);
                }
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
