using System.Reflection;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using PointOfSale.API.SuperAdmin.Controllers;
using PointOfSale.API.SuperAdmin.DTOs;
using PointOfSale.API.SuperAdmin.Models;
using PointOfSale.API.SuperAdmin.Services;

namespace PointOfSale.Tests.SuperAdmin;

public sealed class SuperAdminOrdersControllerTests
{
    private readonly Mock<ISuperAdminOrdersService> _service = new();
    private SuperAdminOrdersController Controller() => new(_service.Object, Mock.Of<ISuperAdminAuditService>(),
        NullLogger<SuperAdminOrdersController>.Instance)
    {
        ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
    };

    [Fact]
    public void OrdersSurface_IsDedicatedSuperAdminNoStoreAndGetOnly()
    {
        var controller = typeof(SuperAdminOrdersController);
        Assert.Equal(SuperAdminSecurityConstants.PolicyName, controller.GetCustomAttribute<AuthorizeAttribute>(true)?.Policy);
        Assert.True(controller.GetCustomAttribute<ResponseCacheAttribute>()?.NoStore);
        var methods = controller.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
        Assert.All(methods, m => Assert.NotNull(m.GetCustomAttribute<HttpGetAttribute>()));
        Assert.Null(controller.GetCustomAttribute<AllowAnonymousAttribute>(true));
    }

    [Fact]
    public async Task Export_UsesUtf8BomExplicitMimeAndExactRowCount()
    {
        _service.Setup(s => s.ExportOrdersCsvAsync(It.IsAny<SuperAdminOrdersListQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SuperAdminOrdersCsvExport { Content = "Order,Customer\nFIXTURE,اسم عربي\n", RowCount = 1 });
        var controller = Controller();
        var file = Assert.IsType<FileContentResult>(await controller.ExportOrders());
        Assert.Equal("text/csv; charset=utf-8", file.ContentType);
        Assert.Equal(new byte[] { 239, 187, 191 }, file.FileContents[..3]);
        Assert.Contains("اسم عربي", Encoding.UTF8.GetString(file.FileContents));
        Assert.Equal("1", controller.Response.Headers["X-Export-Row-Count"].ToString());
    }

    [Fact]
    public async Task ExportLimit_IsAnExplicitErrorNeverASuccessfullyTruncatedDownload()
    {
        _service.Setup(s => s.ExportOrdersCsvAsync(It.IsAny<SuperAdminOrdersListQuery>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new SuperAdminOrdersExportLimitExceededException(10001, 10000));
        var result = Assert.IsType<ObjectResult>(await Controller().ExportOrders());
        Assert.Equal(422, result.StatusCode);
    }

    [Fact]
    public async Task InvalidEnum_IsRejectedBeforeAnyReadServiceCall()
    {
        var result = Assert.IsType<ObjectResult>(await Controller().GetOrders(
            paymentStatus: (SuperAdminOrderPaymentFilter)999));
        Assert.Equal(400, result.StatusCode);
        _service.Verify(s => s.GetOrdersAsync(It.IsAny<SuperAdminOrdersListQuery>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task UnknownDetail_DoesNotReturnAnotherOrder()
    {
        var result = Assert.IsType<ObjectResult>(await Controller().GetOrderDetails(Guid.NewGuid()));
        Assert.Equal(404, result.StatusCode);
    }
}
