#if SHOPIXY_QA_HEADED
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PointOfSale.Core.Enums;
using PointOfSale.Core.Security;
using PointOfSale.Tests.Infrastructure;

namespace PointOfSale.Tests.Integration;

public sealed partial class PosLoopbackHttpAcceptanceTests
{
    // Compiled only by PosHeadedAcceptanceTests.csproj, never a silently skipped
    // browser test in the ordinary HTTP project. The full real Admin build runs.
    [Fact]
    public async Task ActualHeadedAdminUi_CommittedResponseLoss_ReloadRetainsIntent_NoWriteUntilExplicitExactReplay()
    {
        using var database = TestDatabaseScope.Open();
        var fixture = await Seed(database);
        await using var host = await Host.Start(database, fixture,
            builder => builder.Services.Configure<Microsoft.AspNetCore.Mvc.JsonOptions>(options =>
                options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter())),
            app => MapBrowserReadAdapters(app, database, fixture));
        var directory = Environment.GetEnvironmentVariable("SHOPIXY_QA_EVIDENCE_DIR")!;
        try
        {
            var configPath = Path.Combine(directory, "browser-config.json");
            var config = new
            {
                backend = host.Client.BaseAddress!.GetLeftPart(UriPartial.Authority), fixture,
                attempt = Environment.GetEnvironmentVariable("SHOPIXY_QA_BROWSER_ATTEMPT")!,
                uiDirectory = Environment.GetEnvironmentVariable("SHOPIXY_QA_BROWSER_UI_DIST")!,
                sessionUser = new
                {
                    id = fixture.Cashier, tenantId = fixture.Tenant, role = "Cashier", firstName = "QA", lastName = "LocalCashier", emailVerified = true,
                    effectivePermissions = PosRolePermissionCatalog.GetPermissions(UserRole.Cashier), navigationCapabilities = Array.Empty<string>(),
                    vendorLifecycleAuthority = new { vendorId = fixture.Tenant, status = "Active", governanceStatus = "Active", tenantActive = true,
                        marketplaceApproved = true, kycVerified = true, debtStatus = "Clear", isDebtBlocked = false,
                        blocksCatalog = false, blocksOrders = false, blocksStorefront = false, blocksAnalytics = false,
                        blocksPayments = false, blocksPayouts = false, blocksLogin = false }
                }
            };
            await File.WriteAllTextAsync(configPath, JsonSerializer.Serialize(config));
            static string WindowsPath(string path) => path.StartsWith("/mnt/d/") ? "D:/" + path[7..] : path;
            var start = new ProcessStartInfo
            {
                FileName = "/mnt/c/Users/fadya/AppData/Local/Python/pythoncore-3.14-64/python.exe",
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false
            };
            start.ArgumentList.Add(Environment.GetEnvironmentVariable("SHOPIXY_QA_BROWSER_RUNNER")!);
            start.ArgumentList.Add(WindowsPath(configPath));
            using var runner = Process.Start(start)!;
            var stdout = runner.StandardOutput.ReadToEndAsync(); var stderr = runner.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(7));
            await runner.WaitForExitAsync(timeout.Token);
            await File.WriteAllTextAsync(Path.Combine(directory, "browser-runner.log"), await stdout + await stderr);
            Assert.Equal(0, runner.ExitCode);
            using var receipt = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(directory, "headed-browser-result.json")));
            Assert.True(receipt.RootElement.GetProperty("passed").GetBoolean());
            var saleId = receipt.RootElement.GetProperty("invoiceId").GetGuid();
            await AssertSingleSale(database, fixture, saleId);
        }
        finally
        {
            // A browser failure after commit must not strand internal money/stock.
            // Fence any late browser write before owner-only canonical compensation.
            host.CashierWritesEnabled = false;
            await host.CompensateAll();
            await using var db = database.CreateDbContext(fixture.Tenant);
            if (await db.SalesInvoices.AnyAsync())
                await host.AssertCompensatedAndWriteReceipt("headed-browser");
            else
            {
                Assert.Empty(await db.JournalEntries.ToListAsync());
                Assert.Empty(await db.StockMovements.ToListAsync());
                Assert.Empty(await db.Returns.ToListAsync());
                Assert.Equal(1, (await db.ProductVariantStocks.SingleAsync()).Quantity);
                Assert.Equal(0, (await db.ProductVariantStocks.SingleAsync()).ReservedQuantity);
                Assert.All(await db.LedgerAccounts.ToListAsync(), x => Assert.Equal(0, x.CurrentBalance));
                Assert.Equal(0, (await db.Customers.SingleAsync()).Balance);
                await File.WriteAllTextAsync(Path.Combine(directory, "no-sale-side-effects.json"), JsonSerializer.Serialize(new
                    { localOnly = true, production = false, sales = 0, returns = 0, journals = 0, stockMovements = 0, stock = 1, reserved = 0 }));
            }
        }
    }

    private static void MapBrowserReadAdapters(WebApplication app, TestDatabaseScope database, Fixture f)
    {
        // Test-only SQL projection adapters, not certification of production read
        // controllers. Normal cart/sale/return writes use the real MVC controllers.
        app.MapGet("/qa/browser-state", async () =>
        {
            await using var db = database.CreateDbContext(f.Tenant);
            return Results.Json(new { sales = await db.SalesInvoices.CountAsync(), stock = (await db.ProductVariantStocks.SingleAsync()).Quantity,
                saleMovements = await db.StockMovements.CountAsync(x => x.QuantityChange < 0), journals = await db.JournalEntries.CountAsync() });
        }).RequireAuthorization();
        app.MapGet("/api/products/quick-access", async () =>
        {
            await using var db = database.CreateDbContext(f.Tenant);
            var p = await db.Products.SingleAsync(); var v = await db.ProductVariants.SingleAsync(); var s = await db.ProductVariantStocks.SingleAsync();
            return Results.Json(new[] { new { id = v.Id, productId = p.Id, variantId = v.Id, name = p.Name, displayName = p.Name, sku = v.SKU,
                price = v.Price, stockQuantity = s.AvailableQuantity, taxRate = p.TaxRate, priceIncludesTax = false, unit = p.Unit, isVariant = true } });
        }).RequireAuthorization();
        app.MapGet("/api/products/{productId:guid}/units", async (Guid productId) =>
        {
            if (productId != f.Product) return Results.NotFound();
            await using var db = database.CreateDbContext(f.Tenant);
            var units = await db.ProductUnits.Where(x => x.ProductId == productId).Select(x => new { x.Id, x.UnitId, unitName = x.Unit.Name,
                unitSymbol = x.Unit.Symbol, x.ConversionFactor, x.SellingPrice, x.PurchasePrice, x.IsDefault, x.IsBaseUnit }).ToListAsync();
            return Results.Json(units);
        }).RequireAuthorization();
        app.MapGet("/api/products/{productId:guid}/variants/{variantId:guid}/stock", async (Guid productId, Guid variantId) =>
        {
            if (productId != f.Product || variantId != f.Variant) return Results.NotFound();
            await using var db = database.CreateDbContext(f.Tenant);
            var stocks = await db.ProductVariantStocks.AsNoTracking().ToListAsync();
            return Results.Json(stocks.Select(x => new { x.WarehouseId, x.Quantity, x.AvailableQuantity }));
        }).RequireAuthorization();
        app.MapGet("/api/settings", () => Results.Json(new { currencyCode = "EGP", enableTax = false, taxRate = 0, companyName = Prefix + "local-only" })).RequireAuthorization();
        app.MapGet("/api/salesrepresentatives", () => Results.Json(new { items = Array.Empty<object>(), totalCount = 0 })).RequireAuthorization();
    }
}
#endif
