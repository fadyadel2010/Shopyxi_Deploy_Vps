using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using PointOfSale.API.Controllers;
using PointOfSale.Core.Entities;
using PointOfSale.Core.Entities.Affiliate;
using PointOfSale.Core.Interfaces;
using PointOfSale.Core.Interfaces.Affiliate;
using PointOfSale.Core.Interfaces.Treasury;
using PointOfSale.Infrastructure.Data;
using PointOfSale.Infrastructure.MultiTenancy;
using PointOfSale.Infrastructure.Services.Affiliate;

namespace PointOfSale.Tests.Affiliate;

/// <summary>Pure/in-memory regressions only. No payout, simulator, disposable PostgreSQL fixture or application host.</summary>
public sealed class AffiliateP511WorkflowTests
{
    private static ApplicationDbContext Database() => new(new DbContextOptionsBuilder<ApplicationDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static AdminAffiliateController Controller(ApplicationDbContext db, Guid store,
        IAffiliateSettlementOrchestrator? orchestrator = null)
    {
        var tenant = new TenantContext(); tenant.SetTenantId(store);
        return new(db, null!, Mock.Of<IAffiliateService>(), Mock.Of<IAffiliatePayoutService>(),
            orchestrator ?? Mock.Of<IAffiliateSettlementOrchestrator>(MockBehavior.Strict), Mock.Of<IAffiliateReadinessService>(),
            Mock.Of<IEventPublisher>(), Mock.Of<ITreasuryReadService>(), tenant, NullLogger<AdminAffiliateController>.Instance)
        {
            ControllerContext = new() { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()), new Claim(ClaimTypes.Role, "Admin")], "test")) } }
        };
    }

    [Fact]
    public async Task LegacyAndCanonicalRoutes_RejectMissingReference_WithoutInvokingPayout()
    {
        await using var db = Database();
        var store = Guid.NewGuid();
        var request = AffiliatePayoutRequest.Create(store, Guid.NewGuid(), 150, 150, "EGP", "TEST-NO-TRANSFER", "Other", null);
        request.Approve(Guid.NewGuid()); db.Add(request); await db.SaveChangesAsync();
        var orchestrator = new Mock<IAffiliateSettlementOrchestrator>(MockBehavior.Strict);
        var controller = Controller(db, store, orchestrator.Object);
        var legacy = Assert.IsType<BadRequestObjectResult>((await controller.RecordPayment(request.AffiliateAccountId,
            new AdminSettlementActionRequest { ApplyAll = true }, default)).Result);
        var canonical = Assert.IsType<BadRequestObjectResult>(await controller.MarkPayoutPaid(request.Id,
            new AdminMarkPaidRequest { AdminNotes = "A note is not evidence" }, default));
        foreach (var result in new[] { legacy, canonical })
            Assert.Equal("AFFILIATE_PAYOUT_EXTERNAL_CONFIRMATION_REQUIRED", JsonSerializer.SerializeToElement(result.Value).GetProperty("code").GetString());
        Assert.Equal(PayoutRequestStatus.Approved, request.Status);
        orchestrator.VerifyNoOtherCalls();
        Assert.Empty(await db.AffiliateBalanceLedger.IgnoreQueryFilters().ToListAsync());
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("arbitrary-reference-and-notes")]
    [InlineData("qa-reference-without-capability")]
    public async Task RealEvidenceGuard_RejectsUntrustedReferences_WithoutFinancialEffects(string kind)
    {
        await using var db = Database();
        var store = Guid.NewGuid(); var affiliate = Guid.NewGuid();
        var reference = kind == "qa-reference-without-capability" ? $"QA-SIMULATED-NO-TRANSFER:{Guid.NewGuid():D}" : "NOT-A-TRANSFER";
        if (kind != "missing")
        {
            db.Add(AffiliatePayoutTransferEvidence.Create(store, "AFFILIATE_PAYOUT", reference, 150, "EGP", DateTime.UtcNow,
                kind == "qa-reference-without-capability" ? "QA_SIMULATED_PAYOUT_NO_TRANSFER" : "USER_ENTERED_NOTE", new string('A', 64)));
            await db.SaveChangesAsync();
        }
        var orchestrator = new AffiliateSettlementOrchestrator(db, null!, null!, null!, null!, null!,
            NullLogger<AffiliateSettlementOrchestrator>.Instance, Options.Create(new AffiliateSettlementOptions()));
        var guard = typeof(AffiliateSettlementOrchestrator).GetMethod("EnsureVerifiedExternalPayoutEvidenceAllowedAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => (Task)guard.Invoke(orchestrator,
            [affiliate, store, 150m, reference, "WithdrawalEngine", CancellationToken.None])!);
        Assert.StartsWith("AFFILIATE_PAYOUT_VERIFIED_EXTERNAL_EVIDENCE_REQUIRED:", error.Message);
        Assert.Empty(await db.AffiliateBalanceLedger.IgnoreQueryFilters().ToListAsync());
        Assert.Empty(await db.AffiliatePayoutTransferAllocations.IgnoreQueryFilters().ToListAsync());
        Assert.Empty(await db.JournalEntries.IgnoreQueryFilters().ToListAsync());
        Assert.False(db.ChangeTracker.HasChanges());
    }

    [Fact]
    public async Task Summary_CountsEarnedOnce_AndKeepsApprovedDistinctFromPaid()
    {
        await using var db = Database();
        var store = Guid.NewGuid(); var account = AffiliateAccount.Create(Guid.Empty, Guid.NewGuid(), "P511TEST", "P511 in-memory only");
        db.Add(account);
        AffiliateCommissionLedger Earn(decimal amount)
        {
            var order = new Order { Id = Guid.NewGuid(), TenantId = store, OrderNumber = $"IN-MEMORY-{Guid.NewGuid():N}", Currency = "EGP",
                AffiliateAccountId = account.Id, AffiliateReferralCode = account.ReferralCode };
            var item = new OrderItem { Id = Guid.NewGuid(), OrderId = order.Id }; order.Items.Add(item);
            var attribution = AffiliateAttribution.Create(store, order.Id, account.Id, account.ReferralCode, AttributionSource.ReferralLink);
            attribution.Id = Guid.NewGuid();
            var entry = AffiliateCommissionLedger.CreateEarnedFromSnapshot(store, attribution.Id, account.Id, order.Id, item.Id,
                amount * 10, .1m, amount, "EGP");
            db.AddRange(order, attribution, entry); return entry;
        }
        var earned = Earn(850); var approved = AffiliateCommissionLedger.CreateApproved(earned);
        db.Add(approved); Earn(850);
        await db.SaveChangesAsync();
        var controller = Controller(db, store);
        async Task<AffiliateEarningsSummaryDto> Summary() => Assert.Single(Assert.IsType<List<AffiliateEarningsSummaryDto>>(
            Assert.IsType<OkObjectResult>((await controller.GetAffiliateEarnings(account.Id, default)).Result).Value));
        var summary = await Summary();
        Assert.Equal(1700, summary.TotalEarned); Assert.Equal(850, summary.TotalApproved);
        Assert.Equal(0, summary.TotalPaid); Assert.Equal(0, summary.TotalReversed); Assert.Equal(1700, summary.NetEarnings);
        // This is an isolated lifecycle fixture, not execution of a real or simulated payout.
        db.Add(AffiliateCommissionLedger.CreatePaid(approved, null)); await db.SaveChangesAsync();
        summary = await Summary();
        Assert.Equal(1700, summary.TotalEarned); Assert.Equal(850, summary.TotalApproved);
        Assert.Equal(850, summary.TotalPaid); Assert.Equal(1700, summary.NetEarnings);
        var foreign = Assert.IsType<OkObjectResult>((await Controller(db, Guid.NewGuid()).GetAffiliateEarnings(account.Id, default)).Result);
        Assert.Empty(Assert.IsType<List<AffiliateEarningsSummaryDto>>(foreign.Value));
    }
}
