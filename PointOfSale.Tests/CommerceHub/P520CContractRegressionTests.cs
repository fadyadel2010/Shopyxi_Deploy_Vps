using System.Reflection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using PointOfSale.API.CommerceHub.Handlers;
using PointOfSale.API.Controllers;
using PointOfSale.API.DTOs.CommerceHub;
using PointOfSale.API.Services.CommerceHub;
using PointOfSale.API.Services.Governance;
using PointOfSale.API.Services.MarketplaceFooter;
using PointOfSale.Core.Interfaces.Marketplace;
using PointOfSale.Infrastructure.Data;
using PointOfSale.Infrastructure.Elasticsearch.Services;
using PointOfSale.Infrastructure.Jobs;
using PointOfSale.Tests.Infrastructure;

namespace PointOfSale.Tests.CommerceHub;

public sealed class P520CContractRegressionTests
{
    [Theory]
    [InlineData("/about", "/about")]
    [InlineData("/en/privacy", "/privacy")]
    [InlineData("/ar/contact", "/contact")]
    [InlineData("/terms", "/terms")]
    [InlineData("/en/products?q=phone", "/products?q=phone")]
    public void Footer_UsesRegisteredRootRoutes_NotLanguageSegments(string input, string expected)
    {
        var method = typeof(MarketplaceFooterReadService).GetMethod("CanonicalPublicUrl", BindingFlags.NonPublic | BindingFlags.Static)!;
        Assert.Equal(expected, method.Invoke(null, [input]));
    }

    [Fact]
    public async Task ListingKpis_EmitTrustedCurrentTenant_WithoutCallerTenantParameter()
    {
        var tenantId = Guid.NewGuid();
        var tenant = new TestTenantContext(tenantId);
        await using var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options, tenant);
        var controller = new AdminCommerceHubController(tenant, db,
            Mock.Of<IPublishToCommerceHubHandler>(), Mock.Of<IUpdateCommerceHubListingHandler>(),
            Mock.Of<IUnpublishFromCommerceHubHandler>(), Mock.Of<ICommissionService>(),
            Mock.Of<ICommerceHubSearchIndexService>(), Mock.Of<ICommerceHubReindexService>(),
            Mock.Of<IUserGovernanceService>(), Mock.Of<IMarketplaceEventOutboxService>(),
            NullLogger<AdminCommerceHubController>.Instance)
        { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() } };
        var response = await controller.GetListingKpis(CancellationToken.None);
        var dto = Assert.IsType<CommerceHubListingKpisDto>(Assert.IsType<OkObjectResult>(response.Result).Value);
        Assert.Equal(tenantId, dto.TenantId);
        Assert.Equal(0, dto.TotalListings);
        Assert.Single(typeof(AdminCommerceHubController).GetMethod(nameof(controller.GetListingKpis))!.GetParameters());
    }
}
