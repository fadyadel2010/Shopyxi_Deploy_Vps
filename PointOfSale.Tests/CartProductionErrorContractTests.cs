using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using PointOfSale.API.DTOs.Storefront;
using PointOfSale.API.Infrastructure.Mvc;

namespace PointOfSale.Tests;

public class CartProductionErrorContractTests
{
    [Theory]
    [InlineData("CART_CONCURRENCY_CONFLICT")]
    [InlineData("VARIANT_INSUFFICIENT_STOCK")]
    public async Task ProductionCartConflict_PreservesReviewedStaticCodeAndSanitizesDetails(string code)
    {
        var response = await NormalizeAsync(409, code);

        Assert.Equal(code, response.Code);
        Assert.Equal("The request could not be completed.", response.Message);
        Assert.Null(response.Data);
        Assert.DoesNotContain("private-provider-diagnostic", response.Message);
    }

    [Theory]
    [InlineData(404, "MANUAL_PAYMENT_CONFIGURATION_MISSING")]
    [InlineData(404, "MANUAL_PAYMENT_CONFIGURATION_DELETED")]
    [InlineData(404, "MANUAL_PAYMENT_CONFIGURATION_INVALID")]
    [InlineData(404, "MANUAL_PAYMENT_PROVIDER_DISABLED")]
    [InlineData(409, "MANUAL_PAYMENT_CAPABILITY_DISABLED")]
    [InlineData(409, "MANUAL_PAYMENT_CHANNEL_UNSUPPORTED")]
    public async Task ProductionManualPaymentConfiguration_PreservesStaticCodeWithoutProviderDetails(int status, string code)
    {
        var response = await NormalizeAsync(status, code);
        Assert.Equal(code, response.Code);
        Assert.Null(response.Data);
        Assert.DoesNotContain("private-provider-diagnostic", response.Message);
    }

    [Theory]
    [InlineData(409, "UNREVIEWED_CART_CODE", "CONFLICT")]
    [InlineData(400, "provider-private-token", "INVALID_REQUEST")]
    [InlineData(503, "CART_CONCURRENCY_CONFLICT", "INTERNAL_ERROR")]
    [InlineData(500, "VARIANT_INSUFFICIENT_STOCK", "INTERNAL_ERROR")]
    public async Task ProductionBoundary_StillRejectsUnreviewedCodesAndAll5xxDetails(
        int status, string inputCode, string expectedCode)
    {
        var response = await NormalizeAsync(status, inputCode);

        Assert.Equal(expectedCode, response.Code);
        Assert.Null(response.Data);
        Assert.DoesNotContain("private-provider-diagnostic", response.Message);
    }

    private static async Task<ApiErrorResponse> NormalizeAsync(int status, string code)
    {
        using var services = new ServiceCollection()
            .AddSingleton<IHostEnvironment>(new ProductionEnvironment())
            .BuildServiceProvider();
        var http = new DefaultHttpContext { RequestServices = services };
        var action = new ActionContext(http, new RouteData(), new ActionDescriptor());
        var filters = new List<IFilterMetadata>();
        var controller = new object();
        var result = new ObjectResult(new ApiErrorResponse(code, "private-provider-diagnostic")
        {
            Data = new { availableStock = 1, unsafeDetail = "private-provider-diagnostic" }
        }) { StatusCode = status };
        var executing = new ResultExecutingContext(action, filters, result, controller);
        await new ApiErrorResponseNormalizationFilter().OnResultExecutionAsync(
            executing,
            () => Task.FromResult(new ResultExecutedContext(action, filters, executing.Result, controller)));
        var normalized = Assert.IsType<ObjectResult>(executing.Result);
        Assert.Equal(status, normalized.StatusCode);
        return Assert.IsType<ApiErrorResponse>(normalized.Value);
    }

    private sealed class ProductionEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Production;
        public string ApplicationName { get; set; } = "CartProductionErrorContractTests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
