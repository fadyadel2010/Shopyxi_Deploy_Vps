using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using PointOfSale.API.Controllers;
using PointOfSale.API.DTOs;
using PointOfSale.API.Middleware;
using PointOfSale.API.Services;
using PointOfSale.Core.Entities;
using PointOfSale.Core.Enums;
using PointOfSale.Core.Financial;
using PointOfSale.Core.Interfaces;
using PointOfSale.Infrastructure.Data;
using PointOfSale.Tests.Infrastructure;

namespace PointOfSale.Tests.Integration;

public sealed class AccountingCurrencyGovernanceIntegrationTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task PersistedCurrencyCannotBeRelabeled_ThroughSyncAsyncOrForgedDetachedUpdates(bool sync, bool detached)
    {
        using var scope = TestDatabaseScope.Open(); var id = await Seed(scope, "EGP");
        await using (var db = scope.CreateDbContext(id))
        {
            Tenant tenant;
            if (detached)
            {
                // A caller can forge both original and current values. SQL remains authority.
                tenant = new Tenant { Id = id, Name = "Forged replacement name", Currency = "USD", Status = TenantStatus.Active, IsActive = true };
                db.Attach(tenant); db.Entry(tenant).State = EntityState.Modified;
                db.Entry(tenant).Property(x => x.Currency).OriginalValue = "USD";
            }
            else
            {
                tenant = await db.Tenants.SingleAsync(); tenant.Currency = "USD"; tenant.Name = "Must roll back";
            }
            db.SystemSettings.Add(new SystemSetting { Id = Guid.NewGuid(), TenantId = id, Key = "QA.MustNotPersist", Value = "blocked" });
            if (sync) Assert.Throws<AccountingCurrencyChangeException>(() => db.SaveChanges(false));
            else await Assert.ThrowsAsync<AccountingCurrencyChangeException>(() => db.SaveChangesAsync(false, default));
        }
        await using var verify = scope.CreateDbContext(id);
        Assert.Equal("EGP", (await verify.Tenants.SingleAsync()).Currency);
        Assert.Equal("QA-ERP-R10-20261113-CURRENCY", (await verify.Tenants.SingleAsync()).Name);
        Assert.Empty(await verify.SystemSettings.ToListAsync()); Assert.Empty(await verify.OutboxMessages.ToListAsync());
    }

    [Theory]
    [InlineData("EGP", " egp ")]
    [InlineData("USD", "usd")]
    public async Task SameCurrencyMetadataUpdatesNormalizeWithoutInventingConversionOrEvents(string currency, string requested)
    {
        using var scope = TestDatabaseScope.Open(); var id = await Seed(scope, currency);
        await using (var db = scope.CreateDbContext(id))
        {
            var tenant = await db.Tenants.SingleAsync(); tenant.Currency = requested; tenant.Name = "QA updated company metadata";
            await db.SaveChangesAsync();
        }
        await using var verify = scope.CreateDbContext(id);
        Assert.Equal(currency, (await verify.Tenants.SingleAsync()).Currency);
        Assert.Equal("QA updated company metadata", (await verify.Tenants.SingleAsync()).Name);
        Assert.Empty(await verify.OutboxMessages.ToListAsync()); Assert.Empty(await verify.JournalEntries.ToListAsync());
    }

    [Fact]
    public async Task SettingsCannotConvertALegacyUsdStoreToEgp_OrPersistCompanyTaxAndProjectionSideEffects()
    {
        using var scope = TestDatabaseScope.Open(); var id = await Seed(scope, "USD");
        await using (var db = scope.CreateDbContext(id))
        {
            var url = new StorefrontUrlResolver(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
                { ["Storefront:UrlTemplate"] = "https://{slug}.example.test" }).Build(), NullLogger<StorefrontUrlResolver>.Instance, Mock.Of<IServiceScopeFactory>());
            var controller = new SettingsController(db, NullLogger<SettingsController>.Instance, new TestTenantContext(id), Mock.Of<IEventPublisher>(), url)
                { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() } };
            var result = Assert.IsType<ConflictObjectResult>((await controller.UpdateSettings(new SettingsDto
                { CompanyName = "Do not persist", CurrencyCode = "EGP", CurrencySymbol = "E£", TaxRate = 14 })).Result);
            Assert.Equal(AccountingCurrencyChangeException.ErrorCode, JsonSerializer.SerializeToElement(result.Value).GetProperty("code").GetString());
        }
        await using var verify = scope.CreateDbContext(id);
        Assert.Equal("USD", (await verify.Tenants.SingleAsync()).Currency);
        Assert.Empty(await verify.SystemSettings.ToListAsync()); Assert.Empty(await verify.OutboxMessages.ToListAsync());
    }

    [Fact]
    public async Task CurrencyChangeIsANormalized409Conflict_NotAnUnexpectedServerError()
    {
        using var services = new ServiceCollection().BuildServiceProvider();
        var http = new DefaultHttpContext { RequestServices = services }; http.Response.Body = new MemoryStream();
        var middleware = new GlobalExceptionMiddleware(_ => throw new AccountingCurrencyChangeException(), NullLogger<GlobalExceptionMiddleware>.Instance);
        await middleware.InvokeAsync(http);
        Assert.Equal(StatusCodes.Status409Conflict, http.Response.StatusCode);
        http.Response.Body.Position = 0;
        using var json = await JsonDocument.ParseAsync(http.Response.Body);
        Assert.Equal(AccountingCurrencyChangeException.ErrorCode, json.RootElement.GetProperty("code").GetString());
    }

    private static async Task<Guid> Seed(TestDatabaseScope scope, string currency)
    {
        var id = Guid.NewGuid(); await using var db = scope.CreateDbContext(id);
        db.Tenants.Add(new Tenant { Id = id, Name = "QA-ERP-R10-20261113-CURRENCY", Slug = $"qa-currency-{id:N}",
            Currency = currency, IsActive = true, Status = TenantStatus.Active });
        await db.SaveChangesAsync(); return id;
    }
}
