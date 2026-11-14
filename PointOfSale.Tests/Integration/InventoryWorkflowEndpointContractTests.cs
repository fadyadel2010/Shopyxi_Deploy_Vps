using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PointOfSale.API.Controllers;
using PointOfSale.API.Security;
using PointOfSale.Core.Security;

namespace PointOfSale.Tests.Integration;

public sealed class InventoryWorkflowEndpointContractTests
{
    [Theory]
    [InlineData("List", "GET", PosPermissions.FinanceView)]
    [InlineData("Detail", "GET", PosPermissions.FinanceView)]
    [InlineData("Sources", "GET", PosPermissions.FinanceOperate)]
    [InlineData("Issue", "POST", PosPermissions.FinanceOperate)]
    [InlineData("Reverse", "POST", PosPermissions.FinanceOperate)]
    [InlineData("Journal", "GET", PosPermissions.ReportsFinancialView)]
    public void VoucherEndpointsAreClassifiedAndHaveConjunctiveFinanceReadAuthority(string action, string verb, string permission)
    {
        Assert.True(PosEndpointPermissionCatalog.TryResolve("FinancialVouchers", action, [verb], out var classified));
        Assert.Equal(permission, classified);
        Assert.Contains(typeof(FinancialVouchersController).GetCustomAttributes<AuthorizeAttribute>(), x => x.Policy == PosPermissions.FinanceView);
        if (permission != PosPermissions.FinanceView)
            Assert.Contains(typeof(FinancialVouchersController).GetMethod(action)!.GetCustomAttributes<AuthorizeAttribute>(), x => x.Policy == permission);
        Assert.DoesNotContain(typeof(FinancialVouchersController).GetMethods(), method => method.GetCustomAttributes<HttpDeleteAttribute>().Any());
    }

    [Theory]
    [InlineData("Create", PosPermissions.InventoryAdjust)]
    [InlineData("Save", PosPermissions.InventoryAdjust)]
    [InlineData("Cancel", PosPermissions.InventoryAdjust)]
    [InlineData("Post", PosPermissions.InventoryApprove)]
    [InlineData("Reverse", PosPermissions.InventoryApprove)]
    [InlineData("Journal", PosPermissions.ReportsFinancialView)]
    public void CountEndpointsHaveExplicitPermissionsAndCatalogClassification(string action, string permission)
    {
        var method = typeof(StockCountsController).GetMethod(action)!;
        Assert.Contains(method.GetCustomAttributes<AuthorizeAttribute>(), x => x.Policy == permission);
        Assert.True(PosEndpointPermissionCatalog.TryResolve("StockCounts", action, [action == "Journal" ? "GET" : "POST"], out var classified));
        Assert.Equal(permission, classified);
        Assert.Contains(typeof(StockCountsController).GetCustomAttributes<AuthorizeAttribute>(), x => x.Policy == PosPermissions.InventoryView);
        if (action is "Post" or "Reverse")
            Assert.Contains(method.GetCustomAttributes<AuthorizeAttribute>(), x => x.Policy == PosPermissions.FinanceOperate);
    }

    [Theory]
    [InlineData("DispatchTransfer", PosPermissions.InventoryTransfer)]
    [InlineData("ReceiveTransfer", PosPermissions.InventoryTransfer)]
    [InlineData("CancelTransfer", PosPermissions.InventoryTransfer)]
    [InlineData("ReverseTransfer", PosPermissions.InventoryApprove)]
    [InlineData("WorkflowHistory", PosPermissions.InventoryView)]
    public void TransferLifecycleAndHistoryAreClassifiedByLeastPrivilege(string action, string permission)
    {
        var method = typeof(StockTransfersController).GetMethod(action)!;
        Assert.True(PosEndpointPermissionCatalog.TryResolve("StockTransfers", action, [action == "WorkflowHistory" ? "GET" : "POST"], out var classified));
        Assert.Equal(permission, classified);
        if (action == "ReverseTransfer") Assert.Contains(method.GetCustomAttributes<AuthorizeAttribute>(), x => x.Policy == PosPermissions.InventoryApprove);
    }

    [Fact]
    public void CountResponsesContainOnlyScalarDtoValues_NotCoreEntitiesOrNavigationGraphs()
    {
        foreach (var type in new[] { typeof(StockCountResponse), typeof(StockCountLineResponse) })
            Assert.All(type.GetProperties(), property =>
            {
                var declared = property.PropertyType;
                Assert.False(declared.Namespace?.StartsWith("PointOfSale.Core.Entities", StringComparison.Ordinal) == true && !declared.IsEnum,
                    $"{type.Name}.{property.Name} exposes a Core entity");
                if (declared.IsGenericType)
                    Assert.All(declared.GetGenericArguments(), argument => Assert.False(argument.Namespace?.StartsWith("PointOfSale.Core.Entities", StringComparison.Ordinal) == true && !argument.IsEnum));
            });
        Assert.DoesNotContain(typeof(StockCountsController).GetMethods(), method => method.GetCustomAttributes<HttpDeleteAttribute>().Any());
    }
}
