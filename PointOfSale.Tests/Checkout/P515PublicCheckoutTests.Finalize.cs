using System.Diagnostics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PointOfSale.Core.Enums;
using PointOfSale.Core.Interfaces;
using PointOfSale.Core.Interfaces.Payments;
using PointOfSale.Infrastructure.Data;
using PointOfSale.Infrastructure.MultiTenancy;
using PointOfSale.Tests.Infrastructure;

namespace PointOfSale.Tests.Checkout;

public sealed partial class P515PublicCheckoutTests
{
    [P515FinalizeFact]
    public async Task RetainedFixtures_ExpireOnlyUninitializedUnpaidOrder_AndSuspendTestTenants()
    {
        Assert.Equal("1", Environment.GetEnvironmentVariable("P515_API_PAUSED"));
        Assert.Empty(Process.GetProcessesByName("PointOfSale.API"));
        await using var identity = await Affiliate.AffiliateP53ePrimarySupport.Open("P515-isolate-retained-fixtures");
        using var factory = new IsolatedFactory();
        var results = new List<object>();
        foreach (var tenant in new[] { Guid.Parse("e481890f-d9c7-4f4e-abe6-4920e319fffd"), Guid.Parse("6d6e7167-8b22-4de8-a1cf-ed09589e5264") })
        {
            var fixture = JsonSerializer.Deserialize<Fixture>(await File.ReadAllTextAsync(Path.Combine(TestConfiguration.GetSolutionRoot(), $".ai/reports/p515-fixture-{tenant}.json")))!;
            Assert.Equal(tenant, fixture.Tenant);
            using var scope = factory.Services.CreateScope();
            ((TenantContext)scope.ServiceProvider.GetRequiredService<ITenantContext>()).SetTenantId(tenant);
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var store = await db.Tenants.IgnoreQueryFilters().SingleAsync(t => t.Id == tenant && t.Slug == fixture.Slug && t.Name.StartsWith("P515 INTERNAL NO DELIVERY NO EXTERNAL PAYMENT "));
            var uninitialized = await db.Orders.IgnoreQueryFilters().Where(o => o.TenantId == tenant && o.PaymentStatus == PaymentStatus.Pending &&
                !db.PaymentIntents.IgnoreQueryFilters().Any(p => p.OrderId == o.Id && p.TenantId == tenant && p.ProviderIntentId != null)).Select(o => o.Id).ToListAsync();
            foreach (var orderId in uninitialized)
            {
                var expiration = await scope.ServiceProvider.GetRequiredService<IPaymentLifecycleService>().MarkPaymentExpiredAsync(tenant, orderId,
                    "P515 INTERNAL TEST ended; no provider handoff or customer funds existed.");
                Assert.True(expiration.Success);
            }
            // Retain all money, stock, orders, immutable journals and mismatch suspense.
            // Only the task-owned tenant's lifecycle is suspended, using the same
            // domain boundary as the platform tenant-status endpoint.
            store = await db.Tenants.IgnoreQueryFilters().SingleAsync(t => t.Id == tenant);
            store.SetLifecycleStatus(TenantStatus.Suspended, "P515 retained internal test fixture; no external payment or delivery permitted.");
            await db.SaveChangesAsync();
            await DrainFixtureOutboxes(factory, fixture);
            results.Add(new { tenant, expiredUninitializedOrders = uninitialized, status = store.Status.ToString() });
        }
        await File.WriteAllTextAsync(Path.Combine(TestConfiguration.GetSolutionRoot(), $".ai/reports/p515-fixture-isolation-{DateTime.UtcNow:yyyyMMddHHmmss}.json"), JsonSerializer.Serialize(results));
    }
}

public sealed class P515FinalizeFactAttribute : FactAttribute
{
    public P515FinalizeFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("AFFILIATE_PRIMARY_DEVELOPMENT_TESTS") != "1" || Environment.GetEnvironmentVariable("P515_FINALIZE_FIXTURES") != "1")
            Skip = "Explicit P515 fixture isolation only; never touches other tenants or paid/mismatch state.";
    }
}
