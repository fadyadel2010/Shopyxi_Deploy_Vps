using System.Net.Http.Json;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MassTransit;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PointOfSale.Core.Entities;
using PointOfSale.Core.Entities.Accounting;
using PointOfSale.Core.Enums;
using PointOfSale.Core.Events;
using PointOfSale.Core.Interfaces;
using PointOfSale.Core.Interfaces.Accounting;
using PointOfSale.Core.Interfaces.Marketplace;
using PointOfSale.Infrastructure.Accounting;
using PointOfSale.Infrastructure.Consumers;
using PointOfSale.Infrastructure.Data;
using PointOfSale.Infrastructure.EventBus;
using PointOfSale.Infrastructure.Eventing;
using PointOfSale.Infrastructure.Jobs;
using PointOfSale.Infrastructure.MultiTenancy;
using PointOfSale.Tests.Infrastructure;
using StripeConfiguration = Stripe.StripeConfiguration;
using Moq;

namespace PointOfSale.Tests.Checkout;

public sealed partial class P515PublicCheckoutTests
{
    [P515BrowserHostFact]
    public async Task PublicBrowserCheckout_RealHttpSqlStockPaymentAndAccounting_IsolatedProvider()
    {
        await using var identity = await Affiliate.AffiliateP53ePrimarySupport.Open("P515-browser-before-mutation");
        using var factory = new IsolatedFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false, HandleCookies = false });
        Fixture fixture;
        try { fixture = await CreateFixture(factory); }
        catch
        {
            if (factory.FixtureTenant is Guid tenant)
                await DrainFixtureOutboxes(factory, new(tenant, Guid.Empty, Guid.Empty, Guid.Empty, Guid.Empty, Guid.Empty, Guid.Empty, "", ""));
            throw;
        }
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var events = new Dictionary<string, string>();
        var callbacks = new List<object>();
        var projected = new HashSet<Guid>();
        var builder = WebApplication.CreateBuilder(); builder.Logging.ClearProviders();
        var bridge = builder.Build();
        bridge.Urls.Add("http://127.0.0.1:5285");
        bridge.Run(async context =>
        {
            if (context.Request.Host.Host != fixture.Slug + ".localhost") { context.Response.StatusCode = 404; return; }
            var path = context.Request.Path.Value ?? "";
            try
            {
                if (path == "/api/__p515/state")
                {
                    await ProjectOrders(factory, fixture, projected);
                    using var scope = factory.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                    var orders = await db.Orders.IgnoreQueryFilters().Where(o => o.TenantId == fixture.Tenant).OrderBy(o => o.CreatedAt).Select(o => new { o.Id, o.OrderNumber, o.Status, o.PaymentStatus, o.Total, o.Currency, o.FulfillmentStatus }).ToListAsync();
                    await context.Response.WriteAsJsonAsync(new { fixture, orders }); return;
                }
                if (path == "/api/__p515/provider-event" && context.Request.Method == "POST")
                {
                    var id = Guid.Parse(context.Request.Query["orderId"]!); var scenario = context.Request.Query["scenario"].ToString();
                    Assert.Contains(scenario, new[] { "success", "failure", "bad-signature", "wrong-amount", "wrong-currency" });
                    using var scope = factory.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                    var intent = await db.PaymentIntents.IgnoreQueryFilters().SingleAsync(p => p.OrderId == id && p.TenantId == fixture.Tenant);
                    var key = id + ":" + scenario + ":" + context.Request.Query["newEvent"];
                    if (!events.TryGetValue(key, out var payload))
                    {
                        var amount = checked((long)(intent.Amount * 100m));
                        payload = JsonSerializer.Serialize(new { id = "evt_p515_" + Guid.NewGuid().ToString("N"), @object = "event", created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                            api_version = StripeConfiguration.ApiVersion, livemode = false, type = scenario == "failure" ? "payment_intent.payment_failed" : "payment_intent.succeeded",
                            data = new { @object = new { id = intent.ProviderIntentId, @object = "payment_intent", amount, amount_received = scenario == "wrong-amount" ? amount + 1 : amount,
                                currency = scenario == "wrong-currency" ? "usd" : intent.Currency.ToLowerInvariant(), status = scenario == "failure" ? "requires_payment_method" : "succeeded", livemode = false,
                                latest_charge = "ch_p515_" + id.ToString("N"), metadata = new { orderId = id.ToString(), tenantId = fixture.Tenant.ToString() } } } });
                        events[key] = payload;
                    }
                    var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
                    var signature = Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(factory.WebhookSecret), Encoding.UTF8.GetBytes(timestamp + "." + payload)));
                    using var request = new HttpRequestMessage(HttpMethod.Post, "/api/webhooks/stripe") { Content = new StringContent(payload, Encoding.UTF8, "application/json") };
                    request.Headers.Host = fixture.Slug + ".localhost:5184";
                    request.Headers.Add("Stripe-Signature", $"t={timestamp},v1={(scenario == "bad-signature" ? new string('0', 64) : signature)}");
                    using var response = await client.SendAsync(request);
                    var body = await response.Content.ReadAsStringAsync();
                    callbacks.Add(new { orderId = id, scenario, status = (int)response.StatusCode, body });
                    await DrainFixtureOutboxes(factory, fixture);
                    await ProjectOrders(factory, fixture, projected);
                    context.Response.StatusCode = (int)response.StatusCode; context.Response.ContentType = "application/json"; await context.Response.WriteAsync(body); return;
                }
                if (path == "/api/__p515/finish" && context.Request.Method == "POST")
                {
                    await context.Response.WriteAsJsonAsync(new { finished = true }); finished.TrySetResult(); return;
                }
                if (!path.StartsWith("/api/storefront/", StringComparison.Ordinal)) { context.Response.StatusCode = 404; return; }
                using var forwarded = new HttpRequestMessage(new HttpMethod(context.Request.Method), path + context.Request.QueryString);
                if (context.Request.ContentLength > 0 || context.Request.Headers.ContainsKey("Transfer-Encoding")) forwarded.Content = new StreamContent(context.Request.Body);
                foreach (var header in context.Request.Headers)
                {
                    if (new[] { "Host", "Connection", "Content-Length", "Transfer-Encoding" }.Contains(header.Key, StringComparer.OrdinalIgnoreCase)) continue;
                    if (!forwarded.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray())) forwarded.Content?.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
                }
                forwarded.Headers.Host = context.Request.Host.Value;
                using var result = await client.SendAsync(forwarded);
                context.Response.StatusCode = (int)result.StatusCode;
                foreach (var header in result.Headers.Concat(result.Content.Headers))
                    if (!new[] { "Transfer-Encoding", "Connection", "Content-Length" }.Contains(header.Key, StringComparer.OrdinalIgnoreCase)) context.Response.Headers[header.Key] = header.Value.ToArray();
                await result.Content.CopyToAsync(context.Response.Body);
            }
            catch (Exception ex)
            {
                context.Response.StatusCode = 500;
                await context.Response.WriteAsJsonAsync(new { error = ex.GetType().Name, message = ex.Message });
            }
        });
        await bridge.StartAsync();
        await File.WriteAllTextAsync(Path.Combine(TestConfiguration.GetSolutionRoot(), $".ai/reports/p515-browser-ready-{fixture.Tenant}-{DateTime.UtcNow:yyyyMMddHHmmss}.json"), JsonSerializer.Serialize(new { fixture, url = $"http://{fixture.Slug}.localhost:5184/products/{fixture.ProductSlug}", port = 5285 }, new JsonSerializerOptions { WriteIndented = true }));
        try
        {
            await finished.Task.WaitAsync(TimeSpan.FromMinutes(20));
            await DrainFixtureOutboxes(factory, fixture);
            using var scope = factory.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var orders = await db.Orders.IgnoreQueryFilters().Where(o => o.TenantId == fixture.Tenant).ToListAsync();
            Assert.Contains(orders, o => o.PaymentStatus == PaymentStatus.Collected);
            Assert.DoesNotContain(orders, o => o.Status == OrderStatus.Delivered || o.Status == OrderStatus.Completed || o.FulfillmentStatus == FulfillmentStatus.Delivered);
            await File.WriteAllTextAsync(Path.Combine(TestConfiguration.GetSolutionRoot(), $".ai/reports/p515-callbacks-{fixture.Tenant}.json"), JsonSerializer.Serialize(callbacks, new JsonSerializerOptions { WriteIndented = true }));
        }
        finally
        {
            await DrainFixtureOutboxes(factory, fixture);
            await bridge.StopAsync(); await bridge.DisposeAsync();
        }
    }

    private static async Task ProjectOrders(IsolatedFactory factory, Fixture fixture, HashSet<Guid> projected)
    {
        foreach (var evt in InMemoryEventPublisher.GetPublishedEvents().Where(e => e.TenantId == fixture.Tenant && !projected.Contains(e.EventId)).ToArray())
        {
            using var scope = factory.Services.CreateScope(); var consumer = scope.ServiceProvider.GetRequiredService<OrderProjectionConsumer>();
            switch (evt)
            {
                case OrderCreatedEvent e: await consumer.Consume(Mock.Of<ConsumeContext<OrderCreatedEvent>>(c => c.Message == e && c.CancellationToken == CancellationToken.None)); break;
                case OrderStatusChangedEvent e: await consumer.Consume(Mock.Of<ConsumeContext<OrderStatusChangedEvent>>(c => c.Message == e && c.CancellationToken == CancellationToken.None)); break;
                case OrderPaymentStatusChangedEvent e: await consumer.Consume(Mock.Of<ConsumeContext<OrderPaymentStatusChangedEvent>>(c => c.Message == e && c.CancellationToken == CancellationToken.None)); break;
            }
            projected.Add(evt.EventId);
        }
    }

    private static async Task DrainFixtureOutboxes(IsolatedFactory factory, Fixture fixture)
    {
        using var scope = factory.Services.CreateScope(); var services = scope.ServiceProvider;
        ((TenantContext)services.GetRequiredService<ITenantContext>()).SetTenantId(fixture.Tenant);
        var db = services.GetRequiredService<ApplicationDbContext>();
        var outbox = new OutboxPublisherJob(factory.Services, services.GetRequiredService<ILogger<OutboxPublisherJob>>());
        foreach (var id in await db.OutboxMessages.Where(m => m.TenantId == fixture.Tenant && m.ProcessedAtUtc == null).OrderBy(m => m.EventVersion).ThenBy(m => m.CreatedAtUtc).Select(m => m.Id).ToListAsync())
            await (Task)typeof(OutboxPublisherJob).GetMethod("ProcessMessageAsync", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(outbox, [id])!;
        var market = services.GetRequiredService<IMarketplaceEventOutboxProcessor>();
        foreach (var id in await db.Set<MarketplaceEventOutboxMessage>().IgnoreQueryFilters().Where(m => m.TenantId == fixture.Tenant && m.PublishedAtUtc == null).OrderBy(m => m.Version).ThenBy(m => m.OccurredAtUtc).Select(m => m.Id).ToListAsync())
            await (Task)typeof(MarketplaceEventOutboxProcessor).GetMethod("ProcessMessageAsync", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(market, [id, CancellationToken.None])!;
        var accounting = services.GetRequiredService<IAccountingOutboxProcessor>();
        foreach (var message in await db.AccountingOutboxMessages.IgnoreQueryFilters().Where(m => m.TenantId == fixture.Tenant && m.ProcessedAtUtc == null).OrderBy(m => m.AggregateVersion).ThenBy(m => m.OccurredAtUtc).ToListAsync())
            await (Task)typeof(AccountingOutboxProcessor).GetMethod("ProcessMessageAsync", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(accounting, [services, db, message, CancellationToken.None])!;
        db.ChangeTracker.Clear();
        Assert.False(await db.OutboxMessages.AnyAsync(m => m.TenantId == fixture.Tenant && m.ProcessedAtUtc == null));
        Assert.False(await db.Set<MarketplaceEventOutboxMessage>().IgnoreQueryFilters().AnyAsync(m => m.TenantId == fixture.Tenant && m.PublishedAtUtc == null));
        Assert.False(await db.AccountingOutboxMessages.IgnoreQueryFilters().AnyAsync(m => m.TenantId == fixture.Tenant && m.ProcessedAtUtc == null));
    }
}

public sealed class P515BrowserHostFactAttribute : FactAttribute
{
    public P515BrowserHostFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("AFFILIATE_PRIMARY_DEVELOPMENT_TESTS") != "1" || Environment.GetEnvironmentVariable("P515_PUBLIC_CHECKOUT_HOST") != "1")
            Skip = "Explicit isolated P515 browser host only; requires paused shared API and safe provider transport.";
    }
}
