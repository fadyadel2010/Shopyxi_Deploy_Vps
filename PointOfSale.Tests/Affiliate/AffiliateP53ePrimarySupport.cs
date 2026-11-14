using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Npgsql;
using PointOfSale.Infrastructure.Data;
using PointOfSale.Infrastructure.MultiTenancy;
using PointOfSale.Tests.Infrastructure;

namespace PointOfSale.Tests.Affiliate;

internal static class AffiliateP53ePrimarySupport
{
    internal static readonly Guid Store = Guid.Parse("c9c5c1ae-949e-498e-bc63-07242daeb49d");
    internal static readonly Guid Actor = Guid.Parse("1bb114d3-a9a3-4a9d-ace3-3690361637a8");
    internal static string EvidenceDirectory => Path.Combine(TestConfiguration.GetSolutionRoot(), ".ai/evidence/affiliate-p53e-20261023");
    internal static IConfiguration Config => new ConfigurationBuilder()
        .SetBasePath(Path.Combine(TestConfiguration.GetSolutionRoot(), "PointOfSale.API"))
        .AddJsonFile("appsettings.json").AddJsonFile("appsettings.Development.json")
        .AddJsonFile("appsettings.Development.local.json").Build();

    internal static async Task<ApplicationDbContext> Open(string application = "P53E-primary-only", params IInterceptor[] interceptors)
    {
        Assert.Equal("1", Environment.GetEnvironmentVariable("AFFILIATE_PRIMARY_DEVELOPMENT_TESTS"));
        var cs = new NpgsqlConnectionStringBuilder(Config.GetConnectionString("DefaultConnection"));
        Assert.Equal("192.168.1.5", cs.Host); Assert.Equal("PosEcommerceDb_Baseline", cs.Database);
        cs.ApplicationName = application;
        var tenant = new TenantContext(); tenant.SetTenantId(Store);
        var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseNpgsql(cs.ConnectionString)
            .AddInterceptors(interceptors).Options, tenant);
        await db.Database.OpenConnectionAsync();
        await using var cmd = db.Database.GetDbConnection().CreateCommand();
        cmd.CommandText = "SELECT current_database(), host(inet_server_addr()), current_setting('transaction_read_only')";
        await using var reader = await cmd.ExecuteReaderAsync(); Assert.True(await reader.ReadAsync());
        Assert.Equal("PosEcommerceDb_Baseline", reader.GetString(0)); Assert.Equal("192.168.1.5", reader.GetString(1));
        Assert.Equal("off", reader.GetString(2));
        return db;
    }
}
