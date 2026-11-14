#if DEBUG
using System.Diagnostics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PointOfSale.Infrastructure.Data;
using PointOfSale.Infrastructure.Services;
using PointOfSale.Infrastructure.Services.Affiliate;

namespace PointOfSale.API.Development;

/// <summary>One-shot, local-file opt-in observation only. No HTTP route or financial command.</summary>
public sealed class AffiliateProtectionAudit(IServiceScopeFactory scopes, IWebHostEnvironment environment,
    IServiceProvider services) : BackgroundService
{
    public static string EvidenceDirectory(string contentRoot)
    {
        var lifecycleAudit = Path.GetFullPath(Path.Combine(contentRoot, "..", ".ai", "evidence", "affiliate-p53d-20261023"));
        if (File.Exists(Path.Combine(lifecycleAudit, "audit.requested"))) return lifecycleAudit;
        var couponAudit = Path.GetFullPath(Path.Combine(contentRoot, "..", ".ai", "evidence", "affiliate-p53c-20261023"));
        if (File.Exists(Path.Combine(couponAudit, "audit.requested"))) return couponAudit;
        var cancellationAudit = Path.GetFullPath(Path.Combine(contentRoot, "..", ".ai", "evidence", "affiliate-p53b-20261023"));
        return File.Exists(Path.Combine(cancellationAudit, "audit.requested")) ? cancellationAudit :
            Path.GetFullPath(Path.Combine(contentRoot, "..", ".ai", "evidence", "affiliate-p53a-20261022"));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!environment.IsDevelopment()) throw new InvalidOperationException("Development only");
        var directory = EvidenceDirectory(environment.ContentRootPath);
        if (!File.Exists(Path.Combine(directory, "audit.requested"))) return;
        await Task.Yield();
        await using var scope = scopes.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var configuration = sp.GetRequiredService<IConfiguration>();
        var qa = sp.GetRequiredService<IOptionsMonitor<SyntheticQaOptions>>().CurrentValue;
        var settlement = sp.GetRequiredService<IOptions<AffiliateSettlementOptions>>().Value;
        var db = sp.GetRequiredService<ApplicationDbContext>();
        await db.Database.OpenConnectionAsync(stoppingToken);
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = "SELECT current_database(), host(inet_server_addr()), pg_backend_pid()";
        await using var reader = await command.ExecuteReaderAsync(stoppingToken);
        await reader.ReadAsync(stoppingToken);
        var identity = new { database = reader.GetString(0), server = reader.GetString(1), backendPid = reader.GetInt32(2) };
        var providers = (configuration as IConfigurationRoot)?.Providers.ToArray() ?? [];
        string? Source(string key) => providers.LastOrDefault(p => p.TryGet(key, out _))?.ToString();
        var evidence = new
        {
            capturedAtUtc = DateTime.UtcNow, apiPid = Environment.ProcessId,
            executable = Environment.ProcessPath, startedAtUtc = Process.GetCurrentProcess().StartTime.ToUniversalTime(),
            environment = environment.EnvironmentName, identity,
            syntheticQa = new { qa.Enabled, signingKeyPresent = !string.IsNullOrEmpty(qa.SigningKey),
                verifierCount = qa.VerifierIds.Length, runtimeAllowed = SyntheticQaCertificationAuthority.IsRuntimeAllowed(environment.EnvironmentName, qa),
                enabledSource = Source("SyntheticQa:Enabled"), signingKeySource = Source("SyntheticQa:SigningKey") },
            settlement = new { settlement.RequireVerifiedExternalPayoutEvidence, settlement.AllowManualPayoutEvidenceOverride,
                settlement.RequireVerifiedManualDeliveryEvidence, settlement.AllowManualDeliveryEvidenceOverride,
                externalEvidenceKeyPresent = !string.IsNullOrEmpty(settlement.ExternalEvidenceHmacSecret),
                manualOverrideSource = Source("AffiliateSettlement:AllowManualDeliveryEvidenceOverride") },
            p53d = new {
                mailEnabled = File.Exists(Path.Combine(AffiliateP53dMailSink.DirectoryPath(environment.ContentRootPath), "mail.enabled")),
                payoutEnabled = File.Exists(Path.Combine(AffiliateP53dMailSink.DirectoryPath(environment.ContentRootPath), "payout.enabled")),
                evidenceRequested = File.Exists(Path.Combine(AffiliateP53dMailSink.DirectoryPath(environment.ContentRootPath), "payout-evidence.requested")),
                registeredVerifiers = sp.GetServices<PointOfSale.Core.Interfaces.Affiliate.IAffiliatePayoutEvidenceVerifier>().Select(v => v.GetType().FullName).ToArray(),
                emailSender = sp.GetRequiredService<PointOfSale.API.Services.Auth.IEmailSender>().GetType().FullName
            },
            localDrivers = new
            {
                phase51Marker = File.Exists(Path.Combine(environment.ContentRootPath, "..", ".ai", "evidence", "affiliate-phase51-20261022", "runtime.enabled")),
                phase52Marker = File.Exists(Path.Combine(environment.ContentRootPath, "..", ".ai", "evidence", "affiliate-p52-20261022", "runtime.enabled")),
                registered = services.GetServices<IHostedService>().Select(s => s.GetType().FullName)
                    .Where(n => n?.Contains("AffiliatePhase5", StringComparison.Ordinal) == true).ToArray()
            }
        };
        await File.WriteAllTextAsync(Path.Combine(directory, "runtime-protections.json"),
            JsonSerializer.Serialize(evidence, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }), stoppingToken);
    }
}
#endif
