using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Moq;
using PointOfSale.API.Filters;
using PointOfSale.API.Services.Governance;
using PointOfSale.Core.Interfaces;

namespace PointOfSale.Tests.CommerceHub;

public sealed class CommerceHubApprovedSellerFilterTests
{
    [Fact]
    public async Task Pending_or_rejected_merchant_is_blocked_before_catalog_action_executes()
    {
        var tenantId = Guid.NewGuid();
        var tenant = new Mock<ITenantContext>();
        tenant.SetupGet(context => context.RequiredTenantId).Returns(tenantId);
        var governance = new Mock<IUserGovernanceService>();
        governance
            .Setup(service => service.EnsureVendorListingAllowedAsync(tenantId, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new UserGovernanceException(
                "VENDOR_MARKETPLACE_APPROVAL_REQUIRED",
                "Marketplace approval is required before the vendor can manage CommerceHub listings.",
                StatusCodes.Status409Conflict));
        var filter = new CommerceHubApprovedSellerFilter(tenant.Object, governance.Object);
        var executing = CreateExecutingContext();
        var actionExecuted = false;

        await filter.OnActionExecutionAsync(executing, () =>
        {
            actionExecuted = true;
            return Task.FromResult(new ActionExecutedContext(executing, new List<IFilterMetadata>(), new object()));
        });

        Assert.False(actionExecuted);
        var result = Assert.IsType<ObjectResult>(executing.Result);
        Assert.Equal(StatusCodes.Status403Forbidden, result.StatusCode);
    }

    [Fact]
    public async Task Approved_merchant_reaches_catalog_action()
    {
        var tenantId = Guid.NewGuid();
        var tenant = new Mock<ITenantContext>();
        tenant.SetupGet(context => context.RequiredTenantId).Returns(tenantId);
        var governance = new Mock<IUserGovernanceService>();
        governance
            .Setup(service => service.EnsureVendorListingAllowedAsync(tenantId, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var filter = new CommerceHubApprovedSellerFilter(tenant.Object, governance.Object);
        var executing = CreateExecutingContext();
        var actionExecuted = false;

        await filter.OnActionExecutionAsync(executing, () =>
        {
            actionExecuted = true;
            return Task.FromResult(new ActionExecutedContext(executing, new List<IFilterMetadata>(), new object()));
        });

        Assert.True(actionExecuted);
        Assert.Null(executing.Result);
    }

    private static ActionExecutingContext CreateExecutingContext()
    {
        var actionContext = new ActionContext(
            new DefaultHttpContext(),
            new RouteData(),
            new ActionDescriptor());
        return new ActionExecutingContext(
            actionContext,
            new List<IFilterMetadata>(),
            new Dictionary<string, object?>(),
            new object());
    }
}
