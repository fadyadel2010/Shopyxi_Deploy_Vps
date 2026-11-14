using Microsoft.AspNetCore.Authorization;
using PointOfSale.Api.Controllers;
using PointOfSale.API.Controllers;
using PointOfSale.API.Security;
using PointOfSale.Core.Enums;
using PointOfSale.Core.Security;

namespace PointOfSale.Tests;

public sealed class AdminAccountingAuthorizationTests
{
    [Fact]
    public void CashboxAdministration_RequiresFinanceOperate_AndInitializationAuthority()
    {
        Assert.Contains(typeof(CashboxController)
            .GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true)
            .Cast<AuthorizeAttribute>(), attribute => attribute.Policy == PosPermissions.FinanceOperate);

        var openingBalance = typeof(CashboxController).GetMethod(nameof(CashboxController.SetOpeningBalance));
        Assert.NotNull(openingBalance);
        Assert.Contains(openingBalance!.GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true)
            .Cast<AuthorizeAttribute>(), attribute => attribute.Policy == PosPermissions.FinanceInitialize);

        Assert.False(PosRolePermissionCatalog.IsGranted(UserRole.Cashier, PosPermissions.FinanceOperate));
        Assert.False(PosRolePermissionCatalog.IsGranted(UserRole.Cashier, PosPermissions.FinanceInitialize));
    }

    [Theory]
    [InlineData(nameof(AdminAccountingController.SeedAccounts), PosPermissions.FinanceInitialize)]
    [InlineData(nameof(AdminAccountingController.SeedAccountsOnly), PosPermissions.FinanceInitialize)]
    [InlineData(nameof(AdminAccountingController.SeedPaymentMethodsOnly), PosPermissions.FinanceInitialize)]
    [InlineData(nameof(AdminAccountingController.SeedTreasuryOnly), PosPermissions.FinanceInitialize)]
    [InlineData(nameof(AdminAccountingController.TreasuryTransfer), PosPermissions.FinanceOperate)]
    [InlineData(nameof(AdminAccountingController.SettleClearing), PosPermissions.FinanceOperate)]
    [InlineData(nameof(AdminAccountingController.DepositToBank), PosPermissions.FinanceOperate)]
    public void FinancialMutation_RequiresExplicitPermissionPolicy(string action, string policy)
    {
        var method = typeof(AdminAccountingController).GetMethod(action);
        Assert.NotNull(method);
        Assert.Contains(method!.GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true)
            .Cast<AuthorizeAttribute>(), attribute => attribute.Policy == policy);
        Assert.True(PosEndpointPermissionCatalog.TryResolve(
            "AdminAccounting", action, ["POST"], out var catalogPolicy));
        Assert.Equal(policy, catalogPolicy);
        Assert.False(PosRolePermissionCatalog.IsGranted(UserRole.Cashier, policy));
    }
}
