using System.Data.Common;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using PointOfSale.API.Controllers;
using PointOfSale.Core.Entities;
using PointOfSale.Core.Enums;
using PointOfSale.Infrastructure.Data;
using PointOfSale.Infrastructure.Accounting;
using PointOfSale.Tests.Infrastructure;

namespace PointOfSale.Tests;

/// <summary>
/// Synthetic-only contract checks. Never starts Program, opens PostgreSQL, loads
/// deployment configuration, or contacts any external provider.
/// </summary>
public sealed class DashboardRecentSalesProviderAndSerializationTests
{
    [Fact]
    public async Task ProductionSaveChanges_StillRefusesInMemoryFinancialPosting()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options;
        await using var context = new ApplicationDbContext(options);
        context.SalesInvoices.Add(Invoice(Guid.NewGuid(), "GUARD-ONLY"));

        var error = await Assert.ThrowsAsync<AccountingPeriodException>(() => context.SaveChangesAsync());

        Assert.Equal("PERIOD_RELATIONAL_REQUIRED", error.Code);
        Assert.Empty(await context.SalesInvoices.IgnoreQueryFilters().ToListAsync());
    }

    [Fact]
    public void ActualEndpointQuery_TranslatesUsingNpgsqlWithoutOpeningAnyConnection()
    {
        var tenant = Guid.NewGuid();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql("Host=127.0.0.1;Port=1;Database=g4_translation_only;Username=g4_not_a_real_identity")
            .AddInterceptors(new RefuseDatabaseConnection())
            .Options;
        using var context = new ApplicationDbContext(options, new TestTenantContext(tenant));

        var sql = DashboardController.BuildRecentSalesQuery(context.SalesInvoices, tenant, 8).ToQueryString();

        Assert.Contains("LEFT JOIN", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"CustomerId\" IS NULL", sql, StringComparison.Ordinal);
        Assert.Contains("\"IsWalkInCustomer\"", sql, StringComparison.Ordinal);
        Assert.Contains("\"CustomerName\"", sql, StringComparison.Ordinal);
        Assert.Contains("\"TenantId\"", sql, StringComparison.Ordinal);
        Assert.Contains("\"IsDeleted\"", sql, StringComparison.Ordinal);
        Assert.Contains("LIMIT", sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ActualEndpointQuery_WithRelationalForeignKeys_KeepsFilteredAssociationsDistinctFromWalkIns()
    {
        // Reduced relational fixture: proves the actual query's optional join,
        // FK, soft-delete and tenant semantics, not Production schema compatibility.
        await using var connection = new SqliteConnection("Data Source=:memory:;Foreign Keys=True");
        await connection.OpenAsync();
        var tenant = Guid.NewGuid();
        var otherTenant = Guid.NewGuid();
        var options = new DbContextOptionsBuilder<RecentSalesRelationalFixture>().UseSqlite(connection).Options;
        await using var context = new RecentSalesRelationalFixture(options, tenant);
        await context.Database.EnsureCreatedAsync();
        var named = new Customer { Id = Guid.NewGuid(), TenantId = tenant, Name = "شركة اختبار" };
        var hidden = new Customer { Id = Guid.NewGuid(), TenantId = tenant, Name = "Hidden fixture", IsDeleted = true };
        var foreign = new Customer { Id = Guid.NewGuid(), TenantId = otherTenant, Name = "Foreign fixture" };
        context.Customers.AddRange(named, hidden, foreign);
        context.Invoices.AddRange(
            Invoice(tenant, "WALKIN"),
            Invoice(tenant, "NAMED", named.Id),
            Invoice(tenant, "HIDDEN", hidden.Id),
            Invoice(tenant, "FOREIGN_ASSOCIATION", foreign.Id),
            Invoice(otherTenant, "FOREIGN_INVOICE", foreign.Id),
            Invoice(tenant, "CANCELLED", status: InvoiceStatus.Cancelled));
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        var rows = await DashboardController.BuildRecentSalesQuery(context.Invoices, tenant, 50).ToListAsync();

        Assert.Equal(4, rows.Count);
        var walkIn = rows.Single(row => row.InvoiceNumber == "WALKIN");
        Assert.True(walkIn.IsWalkInCustomer);
        Assert.Null(walkIn.CustomerName);
        var associated = rows.Single(row => row.InvoiceNumber == "NAMED");
        Assert.False(associated.IsWalkInCustomer);
        Assert.Equal(named.Name, associated.CustomerName);
        foreach (var number in new[] { "HIDDEN", "FOREIGN_ASSOCIATION" })
        {
            var row = rows.Single(row => row.InvoiceNumber == number);
            Assert.False(row.IsWalkInCustomer);
            Assert.Null(row.CustomerName);
        }
        Assert.DoesNotContain(rows, row => row.InvoiceNumber is "CANCELLED" or "FOREIGN_INVOICE");
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData(null, false)]
    [InlineData("شركة اختبار", false)]
    public async Task MvcObjectResult_EmitsCamelCaseBooleanFalseAndOmitsNullName(string? name, bool walkIn)
    {
        // Source guard prevents this isolated MVC setup silently drifting from
        // the production entry point's actual serialization policy.
        var program = File.ReadAllText(Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "PointOfSale.API", "Program.cs")));
        Assert.Contains("JsonIgnoreCondition.WhenWritingNull", program, StringComparison.Ordinal);
        Assert.Contains("PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase", program, StringComparison.Ordinal);

        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { EnvironmentName = "Test" });
        builder.Configuration.Sources.Clear();
        builder.WebHost.UseTestServer();
        builder.Services.AddControllers().AddJsonOptions(options =>
        {
            options.JsonSerializerOptions.ReferenceHandler = ReferenceHandler.IgnoreCycles;
            options.JsonSerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
            options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
            options.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
            options.JsonSerializerOptions.PropertyNameCaseInsensitive = true;
        });
        await using var app = builder.Build();
        app.MapGet("/synthetic-contract", async (HttpContext http) =>
        {
            var action = new ActionContext(http, new RouteData(), new ActionDescriptor());
            await new OkObjectResult(new RecentSaleResponse
            {
                InvoiceNumber = "FIXTURE-ONLY", CustomerName = name, IsWalkInCustomer = walkIn
            }).ExecuteResultAsync(action);
        });
        await app.StartAsync();
        using var client = app.GetTestClient();
        using var response = await client.GetAsync("/synthetic-contract");
        response.EnsureSuccessStatusCode();
        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var row = payload.RootElement;
        Assert.Equal(walkIn, row.GetProperty("isWalkInCustomer").GetBoolean());
        Assert.False(row.TryGetProperty("IsWalkInCustomer", out _));
        Assert.Equal(name is not null, row.TryGetProperty("customerName", out var customer));
        if (name is not null)
            Assert.Equal(name, customer.GetString());
    }

    private static SalesInvoice Invoice(Guid tenant, string number, Guid? customer = null,
        InvoiceStatus status = InvoiceStatus.Completed) => new()
    {
        Id = Guid.NewGuid(), TenantId = tenant, CustomerId = customer, InvoiceNumber = number,
        InvoiceDate = DateTime.UtcNow, Status = status, TotalAmount = 10m, PaidAmount = 10m
    };

    private sealed class RefuseDatabaseConnection : DbConnectionInterceptor
    {
        public override InterceptionResult ConnectionOpening(DbConnection connection, ConnectionEventData eventData,
            InterceptionResult result) => throw new InvalidOperationException("Translation-only test must never connect.");

        public override ValueTask<InterceptionResult> ConnectionOpeningAsync(DbConnection connection,
            ConnectionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Translation-only test must never connect.");
    }

    private sealed class RecentSalesRelationalFixture(
        DbContextOptions<RecentSalesRelationalFixture> options, Guid tenant) : DbContext(options)
    {
        public DbSet<SalesInvoice> Invoices => Set<SalesInvoice>();
        public DbSet<Customer> Customers => Set<Customer>();

        protected override void OnModelCreating(ModelBuilder model)
        {
            model.Entity<SalesInvoice>().Ignore(x => x.Warehouse).Ignore(x => x.SalesRepresentative)
                .Ignore(x => x.Items).Ignore(x => x.Payments);
            model.Entity<Customer>().Ignore(x => x.Transactions).Ignore(x => x.Addresses);
            model.Entity<SalesInvoice>().HasOne(x => x.Customer).WithMany(x => x.SalesInvoices)
                .HasForeignKey(x => x.CustomerId).OnDelete(DeleteBehavior.Restrict);
            model.Entity<Customer>().HasQueryFilter(x => x.TenantId == tenant && !x.IsDeleted);
        }
    }
}
