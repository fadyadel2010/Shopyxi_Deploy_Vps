using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Moq;
using PointOfSale.API.SuperAdmin.Controllers;
using PointOfSale.Core.Entities;
using PointOfSale.Core.Enums;
using PointOfSale.Core.Interfaces;
using PointOfSale.Infrastructure.Data;

namespace PointOfSale.Tests.Controllers;

public sealed class SuperAdminHomepagePreviewTests
{
    [Fact]
    public void Preview_RequiresPlatformAuthorityAndDoesNotExposeAnonymousDrafts()
    {
        var metadata = typeof(SuperAdminHomepagePreviewController).GetCustomAttributes(true);
        Assert.Contains(metadata, item => item is AuthorizeAttribute);
        Assert.DoesNotContain(metadata, item => item is AllowAnonymousAttribute);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task Preview_UsesExactSelectedActiveTenant(bool active, bool matching)
    {
        using var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options, Mock.Of<ITenantContext>());
        var tenantId = Guid.NewGuid();
        db.Tenants.Add(new Tenant { Id = tenantId, Name = "Preview vendor", IsActive = active,
            Status = TenantStatus.Active, Currency = "EGP" });
        db.StoreConfigs.Add(new StoreConfig { Id = Guid.NewGuid(), TenantId = tenantId, StoreName = "Selected preview" });
        await db.SaveChangesAsync();
        var controller = new SuperAdminHomepagePreviewController(db)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
        var result = await controller.Get(matching ? tenantId : Guid.NewGuid(), CancellationToken.None);
        if (active && matching) Assert.IsType<OkObjectResult>(result);
        else Assert.IsType<NotFoundResult>(result);
    }
}
