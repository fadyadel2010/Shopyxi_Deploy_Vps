using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using PointOfSale.API.Controllers;
using PointOfSale.Core.Entities;

namespace PointOfSale.Tests.Architecture;

public sealed class PartyArchivalLifecycleAuthorityTests
{
    [Fact]
    public void CustomerAndSupplierLifecycle_MustPersistActorAndTimeWithoutSoftDeletingHistory()
    {
        Assert.NotNull(typeof(Customer).GetProperty(nameof(Customer.IsActive)));
        Assert.NotNull(typeof(Customer).GetProperty(nameof(Customer.LastArchivedAtUtc)));
        Assert.NotNull(typeof(Customer).GetProperty(nameof(Customer.LastArchivedByUserId)));
        Assert.NotNull(typeof(Customer).GetProperty(nameof(Customer.LastReactivatedAtUtc)));
        Assert.NotNull(typeof(Customer).GetProperty(nameof(Customer.LastReactivatedByUserId)));
        Assert.NotNull(typeof(Supplier).GetProperty(nameof(Supplier.IsActive)));
        Assert.NotNull(typeof(Supplier).GetProperty(nameof(Supplier.LastArchivedAtUtc)));
        Assert.NotNull(typeof(Supplier).GetProperty(nameof(Supplier.LastArchivedByUserId)));
        Assert.NotNull(typeof(Supplier).GetProperty(nameof(Supplier.LastReactivatedAtUtc)));
        Assert.NotNull(typeof(Supplier).GetProperty(nameof(Supplier.LastReactivatedByUserId)));

        var migration = ReadRepoFile(
            "PointOfSale.Infrastructure", "Migrations", "20261113090000_PartyArchivalLifecycle.cs");
        Assert.Contains("defaultValue: true", migration, StringComparison.Ordinal);
        Assert.Contains("CK_Customers_LastArchivedActorTime", migration, StringComparison.Ordinal);
        Assert.Contains("CK_Suppliers_LastArchivedActorTime", migration, StringComparison.Ordinal);
        Assert.DoesNotContain("IsDeleted = true", migration, StringComparison.Ordinal);
    }

    [Fact]
    public void LifecycleEndpoints_MustRequirePrivilegedRolesAndSqlRevalidateTenantActor()
    {
        AssertPrivilegedRoles(typeof(CustomersController), nameof(CustomersController.ArchiveCustomer));
        AssertPrivilegedRoles(typeof(CustomersController), nameof(CustomersController.ReactivateCustomer));
        AssertPrivilegedRoles(typeof(SuppliersController), nameof(SuppliersController.ArchiveSupplier));
        AssertPrivilegedRoles(typeof(SuppliersController), nameof(SuppliersController.ReactivateSupplier));

        var customerController = ReadRepoFile("PointOfSale.API", "Controllers", "CustomersController.cs");
        var supplierController = ReadRepoFile("PointOfSale.API", "Controllers", "SuppliersController.cs");
        foreach (var source in new[] { customerController, supplierController })
        {
            Assert.Contains("AdminTenantAuthority.UserId", source, StringComparison.Ordinal);
            Assert.Contains("user.TenantId == _tenantContext.RequiredTenantId", source, StringComparison.Ordinal);
            Assert.Contains("user.IsActive", source, StringComparison.Ordinal);
            Assert.Contains("ExecuteUpdateAsync", source, StringComparison.Ordinal);
            Assert.Contains("PartyLifecycleAudit.Append", source, StringComparison.Ordinal);
            Assert.Contains("FOR SHARE", source, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ArchivedParties_MustBeExcludedFromNewDocumentAdmissionButRemainReadable()
    {
        var sales = ReadRepoFile("PointOfSale.API", "Controllers", "SalesController.cs");
        var purchases = ReadRepoFile("PointOfSale.API", "Controllers", "PurchasesController.cs");
        var orders = ReadRepoFile("PointOfSale.API", "Controllers", "OrdersController.cs");
        var expenses = ReadRepoFile("PointOfSale.API", "Controllers", "ExpensesController.cs");
        var salesReturns = ReadRepoFile("PointOfSale.API", "Controllers", "SalesReturnsController.cs");
        var purchaseReturns = ReadRepoFile("PointOfSale.API", "Controllers", "PurchaseReturnsController.cs");
        var posCarts = ReadRepoFile("PointOfSale.API", "Services", "Pos", "PosCartRuntimeService.cs");
        var statement = ReadRepoFile("PointOfSale.API", "Controllers", "StatementOfAccountController.cs");
        var customers = ReadRepoFile("PointOfSale.API", "Controllers", "CustomersController.cs");
        var suppliers = ReadRepoFile("PointOfSale.API", "Controllers", "SuppliersController.cs");

        Assert.Contains("customer.IsActive", sales, StringComparison.Ordinal);
        Assert.Contains("!supplier.IsActive", purchases, StringComparison.Ordinal);
        Assert.Contains("customer.IsActive", orders, StringComparison.Ordinal);
        Assert.Contains("supplier.IsActive", expenses, StringComparison.Ordinal);
        Assert.Contains("customer.IsActive", posCarts, StringComparison.Ordinal);
        Assert.DoesNotContain("customer.IsActive", salesReturns, StringComparison.Ordinal);
        Assert.DoesNotContain("supplier.IsActive", purchaseReturns, StringComparison.Ordinal);
        var openingBalanceStart = statement.IndexOf("CreateOpeningBalance(", StringComparison.Ordinal);
        var openingBalanceEnd = statement.IndexOf("CreateAdjustment(", openingBalanceStart, StringComparison.Ordinal);
        Assert.True(openingBalanceStart >= 0 && openingBalanceEnd > openingBalanceStart);
        var openingBalanceAdmission = statement[openingBalanceStart..openingBalanceEnd];
        Assert.Equal(2, openingBalanceAdmission.Split("item.IsActive", StringSplitOptions.None).Length - 1);
        Assert.Contains("[FromQuery] bool? isActive = null", customers, StringComparison.Ordinal);
        Assert.Contains("[FromQuery] bool? isActive = null", suppliers, StringComparison.Ordinal);
        Assert.DoesNotContain("HasQueryFilter(e => !e.IsDeleted && e.IsActive", ReadRepoFile(
            "PointOfSale.Infrastructure", "Data", "ApplicationDbContext.cs"), StringComparison.Ordinal);
    }

    private static void AssertPrivilegedRoles(Type controllerType, string methodName)
    {
        var method = controllerType.GetMethod(methodName)
                     ?? throw new InvalidOperationException($"{controllerType.Name}.{methodName} was not found.");
        var authorize = method.GetCustomAttribute<AuthorizeAttribute>()
                        ?? throw new InvalidOperationException($"{controllerType.Name}.{methodName} is missing AuthorizeAttribute.");
        Assert.Equal("Owner,Admin,Manager", authorize.Roles);
    }

    private static string ReadRepoFile(params string[] segments)
    {
        var path = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "..",
            Path.Combine(segments)));
        return File.ReadAllText(path);
    }
}
