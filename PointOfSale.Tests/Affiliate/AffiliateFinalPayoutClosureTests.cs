using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Moq;
using PointOfSale.API.Controllers;
using PointOfSale.API.Services;
using PointOfSale.Core.Entities.Affiliate;
using PointOfSale.Infrastructure.Data;

namespace PointOfSale.Tests.Affiliate;

public sealed class AffiliateFinalPayoutClosureTests
{
    [Theory]
    [InlineData("Development", true)] [InlineData("QA", true)]
    [InlineData("Production", false)] [InlineData("Staging", false)]
    [InlineData("Test", false)] [InlineData("", false)]
    public void Simulation_EnvironmentAllowListIsExplicit(string environment, bool allowed) =>
        Assert.Equal(allowed, AffiliateQaPayoutSimulation.AllowsEnvironment(environment));

    [Theory]
    [InlineData("AFFILIATE_PAYOUT_WALLET_REQUIRED", "Register a payout wallet")]
    [InlineData("AFFILIATE_PAYOUT_INSUFFICIENT_ELIGIBLE_FUNDS", "reserved")]
    [InlineData("AFFILIATE_PAYOUT_WALLET_CHANGED_AFTER_REQUEST", "reject")]
    [InlineData("AFFILIATE_PAYOUT_REQUEST_STATUS_INVALID", "Refresh")]
    [InlineData("AFFILIATE_PAYOUT_VERIFIED_EXTERNAL_EVIDENCE_REQUIRED", "Trusted")]
    public void Contract_StableConflictIncludesSafeActionableGuidance(string code, string guidance)
    {
        var type = typeof(AffiliatePortalController).Assembly.GetType("PointOfSale.API.Controllers.AffiliateFinancialErrorContract")!;
        var result = type.GetMethod("From")!.Invoke(null, [new InvalidOperationException(code + ": private financial details must not leak")])!;
        Assert.Equal(409, result.GetType().GetProperty("StatusCode")!.GetValue(result));
        Assert.Equal(code, result.GetType().GetProperty("Code")!.GetValue(result));
        var message = (string)result.GetType().GetProperty("Message")!.GetValue(result)!;
        Assert.Contains(guidance, message);
        Assert.DoesNotContain("private", message);
    }

    [Fact]
    public void SimulationEndpoint_RequiresAdminAndHasNoCallerControlledMoneyOrEvidenceFields()
    {
        Assert.Equal("Owner,Admin", typeof(AffiliateQaPayoutController).GetCustomAttribute<AuthorizeAttribute>()!.Roles);
        Assert.Equal(nameof(QaPayoutSimulationBody.Acknowledgement), Assert.Single(typeof(QaPayoutSimulationBody).GetProperties()).Name);
        Assert.False(new AffiliateQaPayoutOptions().Enabled);
    }

    [Theory]
    [InlineData("Development", "disabled")] [InlineData("Development", "expired")]
    [InlineData("Development", "missing-marker")] [InlineData("Development", "missing-request")]
    [InlineData("Development", "far-future")] [InlineData("Development", "currency")]
    [InlineData("Production", "enabled")] [InlineData("Staging", "enabled")]
    public async Task Simulation_FailsClosedWithoutEveryGuard_AndOrdinaryRoutesNeverGainCapability(string environment, string fault)
    {
        var root = Path.Combine(Path.GetTempPath(), "opencode", $"affiliate-final-policy-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.GetDirectoryName(AffiliateQaPayoutSimulation.MarkerPath(root))!);
        var marker = AffiliateQaPayoutSimulation.MarkerPath(root);
        try
        {
            if (fault != "missing-marker") await File.WriteAllTextAsync(marker, "SIMULATED ONLY");
            var p = new AffiliateQaPayoutOptions { Enabled = fault != "disabled", StoreTenantId = Guid.NewGuid(),
                AffiliateAccountId = Guid.NewGuid(), WithdrawalRequestId = Guid.NewGuid(), SourceOrderId = Guid.NewGuid(),
                Amount = 850, Currency = "EGP", ExpiresAtUtc = DateTime.UtcNow.AddMinutes(20) };
            if (fault == "expired") p.ExpiresAtUtc = DateTime.UtcNow.AddSeconds(-1);
            if (fault == "far-future") p.ExpiresAtUtc = DateTime.UtcNow.AddDays(1);
            if (fault == "missing-request") p.WithdrawalRequestId = Guid.Empty;
            if (fault == "currency") p.Currency = "USD";
            var host = new Mock<IWebHostEnvironment>(); host.SetupGet(h => h.EnvironmentName).Returns(environment);
            host.SetupGet(h => h.ContentRootPath).Returns(root);
            await using var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
            var simulation = new AffiliateQaPayoutSimulation(db, host.Object, Options.Create(p), null!);
            Assert.False(simulation.IsEnabled);
            var evidence = AffiliatePayoutTransferEvidence.Create(p.StoreTenantId, "AFFILIATE_PAYOUT",
                AffiliateQaPayoutSimulation.Reference(p.WithdrawalRequestId), 850, "EGP", DateTime.UtcNow,
                AffiliateQaPayoutSimulation.Source, new string('A', 64));
            Assert.False(await simulation.VerifyAsync(evidence, p.AffiliateAccountId, p.StoreTenantId, 850, default));
            await Assert.ThrowsAsync<InvalidOperationException>(() => simulation.ExecuteAsync(Guid.NewGuid(), p.StoreTenantId,
                p.WithdrawalRequestId, AffiliateQaPayoutSimulation.Acknowledgement, default));
            Assert.Empty(db.ChangeTracker.Entries());
        }
        finally
        {
            if (File.Exists(marker)) File.Delete(marker);
            Directory.Delete(Path.GetDirectoryName(marker)!);
            Directory.Delete(Path.Combine(root, ".ai"));
            Directory.Delete(root);
        }
    }

    [Fact]
    public void Exact850_RequestPreservesWalletAndStateGuards()
    {
        var actor = Guid.NewGuid();
        var request = AffiliatePayoutRequest.Create(Guid.NewGuid(), Guid.NewGuid(), 850, 850, "EGP", "QANOTRANSFER", "Other", null);
        Assert.Equal(850, request.RemainingAmount);
        Assert.Equal(PayoutRequestStatus.Pending, request.Status);
        Assert.Throws<InvalidOperationException>(() => request.EnsurePayoutWalletSnapshotMatches("CHANGEDNOTRANSFER", "Other", null));
        request.Approve(actor);
        Assert.Throws<InvalidOperationException>(() => request.Cancel());
        Assert.Equal(850, request.ApprovedAmount);
        Assert.Throws<InvalidOperationException>(() => request.Approve(actor));
    }

    /// <summary>ConfigurationBinder may return Local for zone-suffixed instants; compare normalized UTC instants.</summary>
    [Theory]
    [InlineData("visible", true)]
    [InlineData("already-expired", false)]
    [InlineData("feature-off", false)]
    public void Simulation_ConfigurationBindingAdmitsOnlyPolicyWithinWindow(string format, bool expected)
    {
        var root = Path.Combine(Path.GetTempPath(), "opencode", $"affiliate-final-binding-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.GetDirectoryName(AffiliateQaPayoutSimulation.MarkerPath(root))!);
        try
        {
            File.WriteAllText(AffiliateQaPayoutSimulation.MarkerPath(root), "MARKER");
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"AffiliateQaPayout:{nameof(AffiliateQaPayoutOptions.Enabled)}"] = format == "feature-off" ? "false" : "true",
                [$"AffiliateQaPayout:{nameof(AffiliateQaPayoutOptions.StoreTenantId)}"] = "c9c5c1ae-949e-498e-bc63-07242daeb49d",
                [$"AffiliateQaPayout:{nameof(AffiliateQaPayoutOptions.AffiliateAccountId)}"] = "01a17533-6425-7e4a-a410-c54811cb5d66",
                [$"AffiliateQaPayout:{nameof(AffiliateQaPayoutOptions.WithdrawalRequestId)}"] = "f7ca6b1d-c3b7-4caf-971a-9d7a19971e0c",
                [$"AffiliateQaPayout:{nameof(AffiliateQaPayoutOptions.SourceOrderId)}"] = "197d7eb1-2a60-483a-91f3-b714b554e06a",
                [$"AffiliateQaPayout:{nameof(AffiliateQaPayoutOptions.Amount)}"] = "850",
                [$"AffiliateQaPayout:{nameof(AffiliateQaPayoutOptions.Currency)}"] = "EGP",
                [$"AffiliateQaPayout:{nameof(AffiliateQaPayoutOptions.ExpiresAtUtc)}"] = format == "already-expired"
                    ? DateTime.UtcNow.AddMinutes(-1).ToString("O")
                    : DateTime.UtcNow.AddMinutes(20).ToString("O"),
            }).Build();
            var policy = config.GetSection(AffiliateQaPayoutOptions.Section).Get<AffiliateQaPayoutOptions>()!;
            var host = Mock.Of<IWebHostEnvironment>(h => h.EnvironmentName == "Development" && h.ContentRootPath == root);
            using var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().Options);
            var simulation = new AffiliateQaPayoutSimulation(db, host, Options.Create(policy), null!);
            Assert.Equal(expected, simulation.IsEnabled);
        }
        finally
        {
            File.Delete(AffiliateQaPayoutSimulation.MarkerPath(root));
            Directory.Delete(Path.GetDirectoryName(AffiliateQaPayoutSimulation.MarkerPath(root))!);
            Directory.Delete(Path.Combine(root, ".ai"));
            Directory.Delete(root);
        }
    }

    [Fact]
    public async Task EnabledSimulation_StillRejectsOrdinaryVerifierCallsAndNonPostgresDatabase()
    {
        var root = Path.Combine(Path.GetTempPath(), "opencode", $"affiliate-final-enabled-{Guid.NewGuid():N}");
        var marker = AffiliateQaPayoutSimulation.MarkerPath(root);
        Directory.CreateDirectory(Path.GetDirectoryName(marker)!);
        try
        {
            await File.WriteAllTextAsync(marker, "SIMULATION ONLY");
            var policy = new AffiliateQaPayoutOptions { Enabled = true, StoreTenantId = Guid.NewGuid(),
                AffiliateAccountId = Guid.NewGuid(), WithdrawalRequestId = Guid.NewGuid(), SourceOrderId = Guid.NewGuid(),
                Amount = 850, Currency = "EGP", ExpiresAtUtc = DateTime.UtcNow.AddMinutes(20) };
            var host = Mock.Of<IWebHostEnvironment>(h => h.EnvironmentName == "Development" && h.ContentRootPath == root);
            await using var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
            var simulation = new AffiliateQaPayoutSimulation(db, host, Options.Create(policy), null!);
            Assert.True(simulation.IsEnabled);
            var evidence = AffiliatePayoutTransferEvidence.Create(policy.StoreTenantId, "AFFILIATE_PAYOUT",
                AffiliateQaPayoutSimulation.Reference(policy.WithdrawalRequestId), 850, "EGP", DateTime.UtcNow,
                AffiliateQaPayoutSimulation.Source, new string('A', 64));
            Assert.False(await simulation.VerifyAsync(evidence, policy.AffiliateAccountId, policy.StoreTenantId, 850, default));
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => simulation.ExecuteAsync(Guid.NewGuid(),
                policy.StoreTenantId, policy.WithdrawalRequestId, AffiliateQaPayoutSimulation.Acknowledgement, default));
            Assert.Equal("AFFILIATE_PAYOUT_QA_SIMULATION_DATABASE_MISMATCH", error.Message);
            Assert.Empty(db.ChangeTracker.Entries());
        }
        finally
        {
            File.Delete(marker);
            Directory.Delete(Path.GetDirectoryName(marker)!);
            Directory.Delete(Path.Combine(root, ".ai"));
            Directory.Delete(root);
        }
    }
}
