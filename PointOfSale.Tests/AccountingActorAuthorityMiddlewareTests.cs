using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using System.Security.Claims;
using PointOfSale.API.Middleware;
using PointOfSale.API.Security;
using PointOfSale.Core.Interfaces;
using PointOfSale.Infrastructure.Accounting;

namespace PointOfSale.Tests;

public sealed class AccountingActorAuthorityMiddlewareTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task UsesOnlySqlAuthorizedActor_NotCallerHeaders_AndClearsScope(bool allowed)
    {
        var tenant = Guid.NewGuid(); var user = Guid.NewGuid();
        var context = new DefaultHttpContext();
        context.Request.Headers["X-User-Id"] = Guid.NewGuid().ToString();
        context.SetEndpoint(new Endpoint(_ => Task.CompletedTask,
            new EndpointMetadataCollection(new AuthorizeAttribute { Roles = "Owner,Manager" }), "financial-action"));
        var actor = new AccountingActorContext();
        var authority = new Mock<IAdminTenantAuthority>();
        authority.Setup(x => x.ValidateTenantActorAsync(context.User, tenant,
            It.IsAny<IReadOnlyCollection<IReadOnlySet<string>>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(allowed ? AdminTenantAuthorityResult.Allow(user, tenant, "Owner")
                : AdminTenantAuthorityResult.Deny("inactive_actor", user, tenant));
        var tenantContext = new Mock<ITenantContext>(); tenantContext.SetupGet(x => x.TenantId).Returns(tenant);
        var calls = 0;
        var middleware = new AdminTenantAuthorityMiddleware(_ =>
        {
            calls++;
            Assert.Equal(user, actor.ResolveUserId(tenant));
            return Task.CompletedTask;
        }, NullLogger<AdminTenantAuthorityMiddleware>.Instance);
        await middleware.InvokeAsync(context, authority.Object, tenantContext.Object, actor);
        Assert.Equal(allowed ? 1 : 0, calls);
        Assert.Null(actor.ResolveUserId(tenant));
        if (!allowed) Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
    }

    [Fact]
    public async Task PlainAuthorize_CashierIsSqlRevalidatedAndBecomesAccountingActor()
    {
        var tenant = Guid.NewGuid(); var cashier = Guid.NewGuid();
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, cashier.ToString()),
                new Claim(ClaimTypes.Role, "Cashier"),
                new Claim("tenant_id", tenant.ToString())
            ], "test"))
        };
        context.SetEndpoint(new Endpoint(_ => Task.CompletedTask,
            new EndpointMetadataCollection(new AuthorizeAttribute()), "pos-sale"));
        var actor = new AccountingActorContext();
        var authority = new Mock<IAdminTenantAuthority>();
        authority.Setup(x => x.ValidateTenantActorAsync(
                context.User,
                tenant,
                It.Is<IReadOnlyCollection<IReadOnlySet<string>>>(sets =>
                    sets.Count == 1 && sets.Single().SetEquals(AdminTenantAuthority.TenantActorRoles)),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(AdminTenantAuthorityResult.Allow(cashier, tenant, "Cashier"));
        var tenantContext = new Mock<ITenantContext>();
        tenantContext.SetupGet(x => x.TenantId).Returns(tenant);
        var nextCalled = false;
        var middleware = new AdminTenantAuthorityMiddleware(_ =>
        {
            nextCalled = true;
            Assert.Equal(cashier, actor.ResolveUserId(tenant));
            return Task.CompletedTask;
        }, NullLogger<AdminTenantAuthorityMiddleware>.Instance);

        await middleware.InvokeAsync(context, authority.Object, tenantContext.Object, actor);

        Assert.True(nextCalled);
        Assert.Null(actor.ResolveUserId(tenant));
        authority.VerifyAll();
    }

    [Fact]
    public async Task PlainAuthorize_NonStaffPrincipalBypassesTenantStaffAuthority()
    {
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.Role, "Customer")], "test"))
        };
        context.SetEndpoint(new Endpoint(_ => Task.CompletedTask,
            new EndpointMetadataCollection(new AuthorizeAttribute()), "storefront-customer"));
        var authority = new Mock<IAdminTenantAuthority>(MockBehavior.Strict);
        var tenantContext = new Mock<ITenantContext>();
        var called = false;
        var middleware = new AdminTenantAuthorityMiddleware(_ =>
        {
            called = true;
            return Task.CompletedTask;
        }, NullLogger<AdminTenantAuthorityMiddleware>.Instance);

        await middleware.InvokeAsync(context, authority.Object, tenantContext.Object, new AccountingActorContext());

        Assert.True(called);
        authority.VerifyNoOtherCalls();
    }
}
