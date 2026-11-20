using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PointOfSale.API.Controllers;
using PointOfSale.API.Infrastructure.Mvc;
using PointOfSale.API.Security;
using PointOfSale.Core.Entities;
using PointOfSale.Core.Interfaces;
using PointOfSale.Core.Security;
using PointOfSale.Infrastructure.Data;
using PointOfSale.Tests.Infrastructure;

namespace PointOfSale.Tests;

public sealed class Group5CApiAuthorizationTests
{
    internal static readonly Guid TenantId = Guid.Parse("10000000-0000-4000-8000-000000000001");

    [Theory]
    [InlineData("Owner", true, true)]
    [InlineData("Admin", true, true)]
    [InlineData("Manager", true, true)]
    [InlineData("Cashier", false, true)]
    [InlineData("Warehouse", false, false)]
    public async Task MvcEnforcesEndpointAndFieldPermissions_WhileOperationalAccessRemains(string role, bool finance, bool sales)
    {
        await using var db = DashboardReadQueryFixture.Create();
        db.Tenants.Add(new Tenant { Id = TenantId, Name = "Synthetic", Slug = "synthetic-api", Currency = "EGP" });
        await db.SeedReadSnapshotAsync();
        await using var app = BuildHost(db);
        await app.StartAsync();
        using var client = app.GetTestClient();
        client.DefaultRequestHeaders.Add("X-Synthetic-Role", role);
        using var stats = await client.GetAsync("/api/dashboard/stats");
        Assert.Equal(200, (int)stats.StatusCode);
        using var json = JsonDocument.Parse(await stats.Content.ReadAsStringAsync());
        Assert.Equal(finance, json.RootElement.GetProperty("financialSummaryAvailable").GetBoolean());
        foreach (var key in new[] { "todaySales", "inventoryValue", "totalRevenue", "outstandingReceivables", "outstandingPayables" })
            Assert.Equal(finance, json.RootElement.TryGetProperty(key, out _));
        Assert.True(json.RootElement.TryGetProperty("lowStockCount", out _));
        foreach (var route in new[] { "sales-trend", "top-products", "category-sales", "payment-methods" })
        {
            using var response = await client.GetAsync("/api/dashboard/" + route);
            Assert.Equal(finance ? 200 : 403, (int)response.StatusCode);
        }
        using var recent = await client.GetAsync("/api/dashboard/recent-sales");
        Assert.Equal(sales ? 200 : 403, (int)recent.StatusCode);
        using var stock = await client.GetAsync("/api/dashboard/low-stock");
        Assert.Equal(200, (int)stock.StatusCode);
        using var costs = await client.GetAsync("/api/group5c-synthetic-cost");
        using var costJson = JsonDocument.Parse(await costs.Content.ReadAsStringAsync());
        Assert.Equal(finance, costJson.RootElement.GetProperty("variant").TryGetProperty("costPrice", out _));
        Assert.Equal(20m, costJson.RootElement.GetProperty("variant").GetProperty("price").GetDecimal());
        using var entity = await client.GetAsync("/api/group5c-synthetic-cost/entity");
        Assert.Equal(500, (int)entity.StatusCode); // Redaction must never conceal an entity from the existing guard.
    }

    [Fact]
    public async Task MvcRejectsUnauthenticatedRequests()
    {
        await using var db = DashboardReadQueryFixture.Create();
        await using var app = BuildHost(db);
        await app.StartAsync();
        using var client = app.GetTestClient();
        using var response = await client.GetAsync("/api/dashboard/stats");
        Assert.Equal(401, (int)response.StatusCode);
    }

    private static WebApplication BuildHost(ApplicationDbContext db)
    {
        // Synthetic MVC pipeline only. Never runs the production entry point,
        // PosSessionAuthority, seeders, workers or external service registrations.
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { EnvironmentName = "Test" });
        builder.Configuration.Sources.Clear();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(db);
        builder.Services.AddSingleton<ITenantContext>(new TestTenantContext(TenantId));
        builder.Services.AddMemoryCache();
        builder.Services.AddAuthentication("Synthetic").AddScheme<AuthenticationSchemeOptions, SyntheticRoleHandler>("Synthetic", _ => { });
        builder.Services.AddAuthorization(options =>
        {
            foreach (var permission in PosPermissions.All)
                options.AddPolicy(permission, policy => policy.RequireAuthenticatedUser().AddRequirements(new PosPermissionRequirement(permission)));
        });
        builder.Services.AddSingleton<IAuthorizationHandler, PosPermissionAuthorizationHandler>();
        builder.Services.AddScoped<FinancialCostResponseFilter>();
        builder.Services.AddScoped<PreventEntityExposureFilter>();
        builder.Services.AddControllers(options =>
        {
            options.Conventions.Add(new PosPermissionApplicationModelConvention());
            options.Filters.AddService<PreventEntityExposureFilter>();
            options.Filters.AddService<FinancialCostResponseFilter>(order: 1000);
        }).AddApplicationPart(typeof(DashboardController).Assembly)
          .AddApplicationPart(typeof(Group5CSyntheticCostController).Assembly)
          .AddJsonOptions(options =>
          {
              options.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
              options.JsonSerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
          });
        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapControllers();
        return app;
    }

    private sealed class SyntheticRoleHandler(IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger, UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var role = Request.Headers["X-Synthetic-Role"].ToString();
            if (!PosRolePermissionCatalog.TryParseKnownRole(role, out _))
                return Task.FromResult(AuthenticateResult.NoResult());
            var principal = new ClaimsPrincipal(new ClaimsIdentity([
                new Claim(ClaimTypes.Role, role), new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
                new Claim("tenant_id", TenantId.ToString()), new Claim(PosSecurityConstants.SessionBindingClaim, "synthetic"),
                new Claim(PosSecurityConstants.SqlAuthorityClaim, PosSecurityConstants.SqlAuthorityValue)
            ], Scheme.Name));
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name)));
        }
    }
}

[ApiController]
[Route("api/group5c-synthetic-cost")]
[Authorize(Policy = PosPermissions.DashboardView)]
public sealed class Group5CSyntheticCostController : ControllerBase
{
    [HttpGet]
    public IActionResult Read() => Ok(new { variant = new { CostPrice = 12m, PurchasePrice = 12m, Price = 20m, Quantity = 5m } });

    [HttpGet("entity")]
    public IActionResult EntityGuardProbe() => Ok(new Product { Name = "Synthetic entity must be blocked" });
}
