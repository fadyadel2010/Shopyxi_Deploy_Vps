using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using PointOfSale.API.Attributes;
using PointOfSale.API.Controllers;
using PointOfSale.API.DTOs.Storefront;
using PointOfSale.API.Services.Storefront;
using PointOfSale.Infrastructure.Data;
using PointOfSale.Infrastructure.MultiTenancy;

namespace PointOfSale.Tests.Storefront;

public sealed class StorefrontStoreConfigUnknownTenantTests
{
    [Fact]
    public async Task UnknownAutomaticHost_ReturnsNotFoundWithoutQueryingTenantData()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        await using var db = new ApplicationDbContext(options);
        var controller = new StorefrontStoreConfigController(
            new TenantContext(),
            db,
            Mock.Of<IStorefrontThemePreviewTokenService>(),
            NullLogger<StorefrontStoreConfigController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { TraceIdentifier = "unknown-storefront" }
            }
        };

        var result = await controller.GetStoreConfig(cancellationToken: CancellationToken.None);

        var notFound = Assert.IsType<NotFoundObjectResult>(result);
        var error = Assert.IsType<ApiErrorResponse>(notFound.Value);
        Assert.Equal("STOREFRONT_NOT_FOUND", error.Code);
    }

    [Fact]
    public void Endpoint_AllowsMiddlewareToReachNotFoundContractWithoutTenant()
    {
        var method = typeof(StorefrontStoreConfigController).GetMethod(
            nameof(StorefrontStoreConfigController.GetStoreConfig));

        Assert.NotNull(method);
        Assert.NotNull(method!.GetCustomAttributes(typeof(AllowAnonymousTenantAttribute), true).SingleOrDefault());
    }
}
