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

public sealed class StockCountStaleProductionErrorContractTests
{
    [Fact]
    public async Task Production409_StaleSnapshotKeepsStaticRecountGuidance_NotArbitraryMessageOrDetails()
    {
        var response = await Normalize(409, "STOCK_COUNT_STALE");
        Assert.Equal("STOCK_COUNT_STALE", response.Code);
        Assert.Equal("Inventory or reservations changed after the snapshot. Cancel this draft and create a fresh count; recount the affected physical units.", response.Message);
        Assert.Null(response.Data);
        Assert.DoesNotContain("private-provider-diagnostic", response.Message);
    }

    [Theory]
    [InlineData(409, "UNREVIEWED_STOCK_CODE", "CONFLICT")]
    [InlineData(409, "STOCK_COUNT_CONFLICT", "CONFLICT")]
    [InlineData(400, "STOCK_COUNT_STALE", "INVALID_REQUEST")]
    [InlineData(500, "STOCK_COUNT_STALE", "INTERNAL_ERROR")]
    public async Task OnlyExactStale409IsSpecial_OtherSafetyNormalizationIsUnchanged(int status, string code, string expected)
    {
        var response = await Normalize(status, code);
        Assert.Equal(expected, response.Code);
        Assert.Null(response.Data);
        Assert.DoesNotContain("private-provider-diagnostic", response.Message);
    }

    private static async Task<ApiErrorResponse> Normalize(int status, string code)
    {
        using var services = new ServiceCollection().AddSingleton<IHostEnvironment>(new ProductionEnvironment()).BuildServiceProvider();
        var http = new DefaultHttpContext { RequestServices = services };
        var action = new ActionContext(http, new RouteData(), new ActionDescriptor());
        var filters = new List<IFilterMetadata>();
        var controller = new object();
        // Same anonymous lower-case contract returned by StockCountsController.
        var result = new ObjectResult(new { code, message = "private-provider-diagnostic", details = "private-provider-diagnostic" }) { StatusCode = status };
        var executing = new ResultExecutingContext(action, filters, result, controller);
        await new ApiErrorResponseNormalizationFilter().OnResultExecutionAsync(executing,
            () => Task.FromResult(new ResultExecutedContext(action, filters, executing.Result, controller)));
        var normalized = Assert.IsType<ObjectResult>(executing.Result);
        Assert.Equal(status, normalized.StatusCode);
        return Assert.IsType<ApiErrorResponse>(normalized.Value);
    }

    private sealed class ProductionEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Production;
        public string ApplicationName { get; set; } = nameof(StockCountStaleProductionErrorContractTests);
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
