using Microsoft.EntityFrameworkCore;
using Moq;
using PointOfSale.API.Services.Admin;
using PointOfSale.Core.Interfaces;
using PointOfSale.Infrastructure.Data;

namespace PointOfSale.Tests.Controllers;

public sealed class PaymentProviderDashboardQueryTests
{
    [Fact]
    public void DashboardAuditQuery_TranslatesWithPostgresAndKeepsPlatformScopeAndBound()
    {
        using var db = new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseNpgsql("Host=localhost;Database=translation_only;Username=unused").Options,
            Mock.Of<ITenantContext>());

        // ToQueryString translates the actual service query without connecting or writing.
        var sql = PaymentProviderDashboardService.BuildDashboardAuditQuery(db).ToQueryString();

        Assert.Contains("IS NULL", sql);
        Assert.Contains("SuperAdminAPI", sql);
        Assert.Contains("PAYMENT_PROVIDER_CONFIGURATION_VIEWED", sql);
        Assert.Contains("ORDER BY", sql);
        Assert.Contains("LIMIT", sql);
        Assert.Contains("25", sql);
    }
}
