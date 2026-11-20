using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using PointOfSale.API.Controllers;
using PointOfSale.Core.Entities;
using PointOfSale.Core.Enums;
using PointOfSale.Infrastructure.Data;
using PointOfSale.Tests.Infrastructure;

namespace PointOfSale.Tests;

/// <summary>
/// Group 5B defect D-3 — recent-sales customer identity must be semantic.
///
/// The endpoint must never send a display-language literal such as
/// "Walk-in Customer". A walk-in sale is identified by the nullable customer
/// foreign key, the customer name is null when no name is available, and real
/// customer names are returned unchanged.
/// </summary>
public sealed class DashboardRecentSalesCustomerContractTests
{
    [Fact]
    public async Task GetRecentSales_WithoutCustomerAssociation_FlagsWalkInAndOmitsName()
    {
        var tenantId = Guid.NewGuid();
        var warehouseId = Guid.NewGuid();

        await using var dbContext = CreateUnscopedDbContext();
        dbContext.Warehouses.Add(new Warehouse
        {
            Id = warehouseId,
            TenantId = tenantId,
            Name = "Local Warehouse",
            CreatedAt = DateTime.UtcNow
        });
        dbContext.SalesInvoices.Add(new SalesInvoice
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            WarehouseId = warehouseId,
            CustomerId = null,
            InvoiceNumber = "WALKIN-001",
            InvoiceDate = DateTime.UtcNow.AddMinutes(-3),
            Status = InvoiceStatus.Completed,
            TotalAmount = 120m,
            PaidAmount = 120m,
            CreatedAt = DateTime.UtcNow
        });

        await dbContext.SeedReadSnapshotAsync();

        var controller = CreateController(dbContext, tenantId);
        var result = await controller.GetRecentSales(10, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var sales = Assert.IsAssignableFrom<List<RecentSaleResponse>>(ok.Value);
        var sale = Assert.Single(sales);

        Assert.True(sale.IsWalkInCustomer);
        Assert.Null(sale.CustomerName);
        Assert.DoesNotContain("Walk-in Customer", sale.CustomerName ?? string.Empty);
    }

    [Fact]
    public async Task GetRecentSales_WithAssociatedCustomer_ReturnsNameUnchangedAndNoWalkInFlag()
    {
        var tenantId = Guid.NewGuid();
        var warehouseId = Guid.NewGuid();
        var customerId = Guid.NewGuid();

        await using var dbContext = CreateUnscopedDbContext();
        dbContext.Warehouses.Add(new Warehouse
        {
            Id = warehouseId,
            TenantId = tenantId,
            Name = "Local Warehouse",
            CreatedAt = DateTime.UtcNow
        });
        dbContext.Customers.Add(new Customer
        {
            Id = customerId,
            TenantId = tenantId,
            Name = "شركة النيل للتجارة",
            CreatedAt = DateTime.UtcNow
        });
        dbContext.SalesInvoices.Add(new SalesInvoice
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            WarehouseId = warehouseId,
            CustomerId = customerId,
            InvoiceNumber = "NAMED-001",
            InvoiceDate = DateTime.UtcNow.AddMinutes(-2),
            Status = InvoiceStatus.Completed,
            TotalAmount = 80m,
            PaidAmount = 80m,
            CreatedAt = DateTime.UtcNow
        });

        await dbContext.SeedReadSnapshotAsync();

        var controller = CreateController(dbContext, tenantId);
        var result = await controller.GetRecentSales(10, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var sales = Assert.IsAssignableFrom<List<RecentSaleResponse>>(ok.Value);
        var sale = Assert.Single(sales);

        Assert.False(sale.IsWalkInCustomer);
        Assert.Equal("شركة النيل للتجارة", sale.CustomerName);
    }

    [Fact]
    public async Task GetRecentSales_WithCustomerIdButUnavailableNavigation_IsNotClassifiedAsWalkIn()
    {
        var tenantId = Guid.NewGuid();
        var warehouseId = Guid.NewGuid();

        await using var dbContext = CreateUnscopedDbContext();
        dbContext.Warehouses.Add(new Warehouse
        {
            Id = warehouseId,
            TenantId = tenantId,
            Name = "Local Warehouse",
            CreatedAt = DateTime.UtcNow
        });
        // A customer id is present on the invoice but no matching customer row exists,
        // so the navigation is unavailable while the association is authoritative.
        dbContext.SalesInvoices.Add(new SalesInvoice
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            WarehouseId = warehouseId,
            CustomerId = Guid.NewGuid(),
            InvoiceNumber = "ORPHAN-001",
            InvoiceDate = DateTime.UtcNow.AddMinutes(-1),
            Status = InvoiceStatus.Completed,
            TotalAmount = 40m,
            PaidAmount = 40m,
            CreatedAt = DateTime.UtcNow
        });

        await dbContext.SeedReadSnapshotAsync();

        var controller = CreateController(dbContext, tenantId);
        var result = await controller.GetRecentSales(10, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var sales = Assert.IsAssignableFrom<List<RecentSaleResponse>>(ok.Value);
        var sale = Assert.Single(sales);

        Assert.False(sale.IsWalkInCustomer);
        Assert.Null(sale.CustomerName);
    }

    [Fact]
    public void RecentSaleResponse_CarriesNoServerSideDisplayLiteral()
    {
        var source = ReadRepoFile("PointOfSale.API/Controllers/DashboardController.cs");

        Assert.DoesNotContain("Walk-in Customer", source, StringComparison.Ordinal);
        Assert.Contains("IsWalkInCustomer = invoice.CustomerId == null", source, StringComparison.Ordinal);
    }

    private static string ReadRepoFile(string relativePath)
    {
        var path = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "..",
            relativePath));

        return File.ReadAllText(path);
    }

    private static DashboardReadQueryFixture CreateUnscopedDbContext() =>
        DashboardReadQueryFixture.Create();

    private static DashboardController CreateController(ApplicationDbContext dbContext, Guid tenantId) =>
        new(
            dbContext,
            new TestTenantContext(tenantId),
            new MemoryCache(new MemoryCacheOptions()),
            NullLogger<DashboardController>.Instance);

}
