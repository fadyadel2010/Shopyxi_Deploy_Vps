using System.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using PointOfSale.API.Controllers;
using PointOfSale.API.Security;
using PointOfSale.Core.Entities;
using PointOfSale.Core.Entities.Affiliate;
using PointOfSale.Core.Enums;
using PointOfSale.Core.Interfaces.Affiliate;
using PointOfSale.Core.Security;
using PointOfSale.Infrastructure.Data;
using PointOfSale.Infrastructure.Services.Affiliate;
using PointOfSale.Tests.Infrastructure;

namespace PointOfSale.Tests.Affiliate;

/// <summary>Server authority regressions; isolated EF memory only, never an actual payout.</summary>
public sealed class AffiliateManualPayoutAuthorityTests
{
    private static AdminManualPayoutCommand Command() => new(Guid.NewGuid(), 150m, "EGP", new string('A', 64),
        "Cash", true, AffiliateManualPayout.AttestationText);

    [Fact]
    public async Task Review_DoesNotDiscloseForeignWithdrawal()
    {
        var store = Guid.NewGuid();
        await using var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options, new TestTenantContext(store));
        var foreign = AffiliatePayoutRequest.Create(Guid.NewGuid(), Guid.NewGuid(), 150, 150, "EGP", "TEST-ONLY", "Other", null);
        db.Add(foreign);
        await db.SaveChangesAsync();
        var service = new Mock<IAffiliateSettlementOrchestrator>(MockBehavior.Strict);
        var controller = new AffiliateManualPayoutController(db, new TestTenantContext(store), service.Object);
        Assert.IsType<NotFoundObjectResult>(await controller.Review(foreign.Id, default));
        service.VerifyNoOtherCalls();
        Assert.False(db.ChangeTracker.HasChanges());
    }

    [Fact]
    public async Task Execute_RejectsMissingActorBeforeCallingOrchestrator()
    {
        var service = new Mock<IAffiliateSettlementOrchestrator>(MockBehavior.Strict);
        var controller = new AffiliateManualPayoutController(null!, new TestTenantContext(Guid.NewGuid()), service.Object)
        {
            ControllerContext = new() { HttpContext = new DefaultHttpContext() }
        };
        Assert.IsType<UnauthorizedResult>(await controller.Execute(Guid.NewGuid(), Command(), default));
        service.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("foreign-store")]
    [InlineData("inactive")]
    [InlineData("deleted")]
    [InlineData("manager")]
    [InlineData("missing")]
    public async Task Orchestrator_RevalidatesCurrentSqlActorBeforeFinancialWork(string scenario)
    {
        var store = Guid.NewGuid();
        await using var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var actor = new User { Id = Guid.NewGuid(), TenantId = scenario == "foreign-store" ? Guid.NewGuid() : store,
            Role = scenario == "manager" ? UserRole.Manager : UserRole.Admin,
            IsActive = scenario != "inactive", IsDeleted = scenario == "deleted" };
        if (scenario != "missing") { db.Add(actor); await db.SaveChangesAsync(); }
        var service = new AffiliateSettlementOrchestrator(db, null!, null!, null!, null!, null!,
            NullLogger<AffiliateSettlementOrchestrator>.Instance, Options.Create(new AffiliateSettlementOptions()));
        await Assert.ThrowsAsync<SecurityException>(() => service.ExecuteAdminManualPayoutAsync(Guid.NewGuid(), store, actor.Id, Command()));
        Assert.Empty(await db.JournalEntries.IgnoreQueryFilters().ToListAsync());
        Assert.Empty(await db.AffiliateManualPayouts.ToListAsync());
        Assert.False(db.ChangeTracker.HasChanges());
    }

    [Theory]
    [InlineData("Review", "GET")]
    [InlineData("Execute", "POST")]
    public void ManualActions_RequireCanonicalPayoutPermission(string action, string method)
    {
        Assert.True(PosEndpointPermissionCatalog.TryResolve("AffiliateManualPayout", action, [method], out var permission));
        Assert.Equal(PosPermissions.AffiliatesPayoutManage, permission);
        Assert.True(PosRolePermissionCatalog.IsGranted(UserRole.Owner, permission));
        Assert.True(PosRolePermissionCatalog.IsGranted(UserRole.Admin, permission));
        Assert.False(PosRolePermissionCatalog.IsGranted(UserRole.Manager, permission));
        Assert.False(PosRolePermissionCatalog.IsGranted(UserRole.Cashier, permission));
        Assert.False(PosRolePermissionCatalog.IsGranted(UserRole.Warehouse, permission));
    }
}
