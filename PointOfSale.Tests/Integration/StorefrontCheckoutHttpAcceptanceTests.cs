#if SHOPIXY_QA_STOREFRONT_HTTP
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using PointOfSale.API.Controllers;
using PointOfSale.API.DTOs.Cart;
using PointOfSale.API.DTOs.Checkout;
using PointOfSale.API.Middleware;
using PointOfSale.API.Security;
using PointOfSale.API.Services.Cart;
using PointOfSale.API.Services.Checkout;
using PointOfSale.API.Services.Storefront;
using PointOfSale.Core.Enums;
using PointOfSale.Tests.Infrastructure;
using Tender = PointOfSale.Core.Enums.PaymentMethod;

namespace PointOfSale.Tests.Integration;

public sealed partial class PosLoopbackHttpAcceptanceTests
{
    [Fact]
    public async Task StorefrontHttp_CommittedResponseLoss_ExplicitExactReplay_GuestIsolationAndPrivateCookies()
    {
        using var database = TestDatabaseScope.Open(); var f = await Seed(database);
        await using var runtime = await StorefrontRuntime.Create(database, f, true, false, 1);
        await using var host = await StorefrontHttpHost.Start(runtime, database, f);
        try
        {
            var request = await runtime.Prepare(); // Real SQL session/resolution services; not HTTP preparation coverage.
            var bytes = JsonSerializer.SerializeToUtf8Bytes(request, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            using (var missingTenant = await host.Send(bytes, runtime.Cart.OwnerId, includeTenant: false))
                Assert.Equal(HttpStatusCode.BadRequest, missingTenant.StatusCode);
            host.LoseNextCommittedResponse = true;
            await Assert.ThrowsAsync<HttpRequestException>(() => host.Send(bytes, runtime.Cart.OwnerId));
            Guid original;
            string number;
            await using (var db = database.CreateDbContext(f.Tenant))
            {
                var order = Assert.Single(await db.Orders.ToListAsync()); original = order.Id; number = order.OrderNumber;
                Assert.Null(order.CustomerId); Assert.Null(order.OnlineCustomerId); Assert.False(order.IsPaid);
                Assert.Equal(PaymentStatus.Pending, order.PaymentStatus);
                Assert.Equal(1, (await db.ProductVariantStocks.SingleAsync()).ReservedQuantity);
                Assert.Empty(await db.JournalEntries.ToListAsync()); Assert.Empty(await db.PaymentIntents.ToListAsync());
            }
            Assert.Equal(1, host.LostCommittedResponses);
            // A different guest gets their own empty transport cart. Do not mock
            // SQL order ownership or return the original cart to the attacker.
            var attacker = Prefix + "different-guest";
            Mock.Get(runtime.Services.GetRequiredService<ICartService>())
                .Setup(x => x.GetCartAsync(f.Tenant, It.Is<CartOwner>(o => o.OwnerType == "session" && o.OwnerId == attacker), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new CartDto { CartId = Guid.NewGuid(), TenantId = f.Tenant, OwnerType = "session", OwnerId = attacker, Currency = "EGP" });
            using (var denied = await host.Send(bytes, attacker))
            {
                Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
                Assert.False(denied.Headers.Contains("Set-Cookie"));
            }
            runtime.CartReadable = false;
            using var replayResponse = await host.Send(bytes, runtime.Cart.OwnerId);
            var replay = await Success<CheckoutResponseDto>(replayResponse);
            Assert.Equal(original, replay.OrderId); Assert.Equal(number, replay.OrderNumber);
            Assert.Null(replay.CheckoutToken); Assert.Null(replay.TrackingToken);
            var cookies = SafeCookieProof(replayResponse);
            using var secondResponse = await host.Send(bytes, runtime.Cart.OwnerId);
            var second = await Success<CheckoutResponseDto>(secondResponse);
            Assert.Equal(original, second.OrderId); Assert.Equal(number, second.OrderNumber);
            Assert.Null(second.CheckoutToken); Assert.Null(second.TrackingToken);
            Assert.Equal(5, host.Posts); // One missing-header request, one loss, one denial, two explicit replays.
            await using (var db = database.CreateDbContext(f.Tenant))
            {
                Assert.Single(await db.Orders.ToListAsync()); Assert.Single(await db.OrderRequestIdempotencyRecords.ToListAsync());
                Assert.Single(await db.InventoryReservations.ToListAsync());
                Assert.Equal(1, (await db.ProductVariantStocks.SingleAsync()).ReservedQuantity);
            }
            await host.WriteReceipt("commit-loss-replay-guest-isolation", new
            {
                fixture = f, originalOrderId = original, originalOrderNumber = number, replayOrderId = replay.OrderId,
                request.CartId, payloadSha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
                explicitReplayOnly = true, cartExpiredBeforeReplay = true, guestIsolation403 = true,
                unauthenticatedCustomerHeaderIgnored = true, noTokensInBodyOrReceipts = true, cookies
            });
        }
        finally { await runtime.CancelAndVerify("http-commit-loss-replay-guest-isolation"); }
    }

    [Fact]
    public async Task StorefrontHttp_PosSaleInvalidatesPreview_ControllerReturns409_ThenCanonicalReturnAndFreshPreviewPermitOrder()
    {
        using var database = TestDatabaseScope.Open(); var f = await Seed(database);
        await using var runtime = await StorefrontRuntime.Create(database, f, true, false, 1);
        await using var web = await StorefrontHttpHost.Start(runtime, database, f);
        await using var pos = await Host.Start(database, f);
        try
        {
            var request = await runtime.Prepare();
            var cart = await pos.Cart(f.Cashier);
            await Success<SaleResponse>(await pos.Send(HttpMethod.Post, "/api/sales", f.Cashier, Sale(f, Tender.Cash), cart.CartId));
            using (var denied = await web.Send(JsonSerializer.SerializeToUtf8Bytes(request), runtime.Cart.OwnerId))
            {
                Assert.Equal(HttpStatusCode.Conflict, denied.StatusCode);
                var body = await denied.Content.ReadFromJsonAsync<JsonElement>();
                Assert.Equal(WarehouseResolutionErrorCodes.InsufficientStockAtResolvedWarehouse, body.GetProperty("code").GetString());
                Assert.False(denied.Headers.Contains("Set-Cookie"));
            }
            await using (var db = database.CreateDbContext(f.Tenant))
            {
                Assert.Empty(await db.Orders.ToListAsync()); Assert.Empty(await db.InventoryReservations.ToListAsync());
                Assert.Equal(0, (await db.ProductVariantStocks.SingleAsync()).Quantity);
            }
            await pos.CompensateAll();
            var fresh = await runtime.Prepare();
            using var response = await web.Send(JsonSerializer.SerializeToUtf8Bytes(fresh), runtime.Cart.OwnerId);
            var order = await Success<CheckoutResponseDto>(response);
            Assert.Null(order.CheckoutToken); Assert.Null(order.TrackingToken);
            var cookies = SafeCookieProof(response);
            await using (var db = database.CreateDbContext(f.Tenant))
            {
                Assert.Equal(order.OrderId, (await db.Orders.SingleAsync()).Id);
                Assert.Equal(1, (await db.ProductVariantStocks.SingleAsync()).ReservedQuantity);
            }
            await web.WriteReceipt("pos-invalidates-preview", new
            {
                fixture = f, order.OrderId, order.OrderNumber, initialControllerStatus = 409,
                initialCode = WarehouseResolutionErrorCodes.InsufficientStockAtResolvedWarehouse,
                explicitFreshPreviewAfterCanonicalReturn = true, noTokensInBodyOrReceipts = true, cookies
            });
        }
        finally
        {
            try { await pos.CompensateAll(); }
            finally { await runtime.CancelAndVerify("http-pos-invalidates-preview", allowCompensatedPos: true); }
        }
    }

    private static object[] SafeCookieProof(HttpResponseMessage response)
    {
        var values = response.Headers.GetValues("Set-Cookie").ToList();
        var names = new[] { GuestOrderAccessCookie.CheckoutTokenCookieName, GuestOrderAccessCookie.TrackingTokenCookieName };
        return names.Select(name =>
        {
            var cookie = Assert.Single(values, x => x.StartsWith(name + "=", StringComparison.Ordinal));
            var attributes = cookie.Split(';').Skip(1).Select(x => x.Trim().ToLowerInvariant()).ToArray();
            Assert.Contains("httponly", attributes); Assert.Contains("secure", attributes);
            Assert.Contains("samesite=strict", attributes); Assert.Contains("path=/api", attributes);
            return (object)new { name, httpOnly = true, secure = true, sameSite = "Strict", path = "/api", valueNotExported = true };
        }).ToArray();
    }

    private sealed class StorefrontHttpHost(WebApplication app, StorefrontRuntime runtime, Fixture f, TestDatabaseScope database) : IAsyncDisposable
    {
        public HttpClient Client = null!;
        public bool LoseNextCommittedResponse;
        public int LostCommittedResponses;
        public int Posts;
        private readonly ConcurrentQueue<object> events = new();

        public static async Task<StorefrontHttpHost> Start(StorefrontRuntime runtime, TestDatabaseScope database, Fixture f)
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "LocalRetainedQa" });
            builder.Logging.ClearProviders(); builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
            builder.Services.AddHttpContextAccessor();
            builder.Services.AddScoped<IServiceScope>(_ => runtime.Services.CreateScope());
            builder.Services.AddScoped(s => s.GetRequiredService<IServiceScope>().ServiceProvider.GetRequiredService<ICheckoutService>());
            builder.Services.AddSingleton(Mock.Of<ICheckoutAttachmentService>(MockBehavior.Strict));
            var authentication = builder.Services.AddAuthentication("GuestOnlyQa");
            foreach (var name in new[] { "GuestOnlyQa", CustomerSecurityConstants.AuthenticationScheme, "StorefrontJwt", "Bearer" })
                authentication.AddScheme<AuthenticationSchemeOptions, GuestOnlyHttpAuthentication>(name, _ => { });
            builder.Services.AddAuthorization();
            builder.Services.AddRateLimiter(options => options.AddFixedWindowLimiter("checkout", limiter =>
            { limiter.PermitLimit = 20; limiter.Window = TimeSpan.FromMinutes(1); limiter.QueueLimit = 0; limiter.QueueProcessingOrder = QueueProcessingOrder.OldestFirst; }));
            builder.Services.AddControllers().AddApplicationPart(typeof(CheckoutController).Assembly)
                .ConfigureApplicationPartManager(manager => manager.FeatureProviders.Add(new OnlyStorefrontCheckoutController()));
            var app = builder.Build(); var host = new StorefrontHttpHost(app, runtime, f, database);
            app.UseMiddleware<GlobalExceptionMiddleware>(); app.UseRouting(); app.UseRateLimiter(); app.UseAuthentication(); app.UseAuthorization();
            app.Use(async (context, next) =>
            {
                string? payloadSha256 = null;
                string? ownerHash = null;
                string? key = null;
                if (context.Request.Path == "/api/storefront/checkout" && context.Request.Method == "POST")
                {
                    Interlocked.Increment(ref host.Posts);
                    context.Request.EnableBuffering();
                    await using var body = new MemoryStream();
                    await context.Request.Body.CopyToAsync(body);
                    payloadSha256 = Convert.ToHexString(SHA256.HashData(body.ToArray())).ToLowerInvariant();
                    context.Request.Body.Position = 0;
                    ownerHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(context.Request.Headers["X-Session-Id"].ToString()))).ToLowerInvariant();
                    key = context.Request.Headers["Idempotency-Key"].ToString();
                    if (host.LoseNextCommittedResponse)
                    {
                        host.LoseNextCommittedResponse = false;
                        var original = context.Response.Body; await using var discard = new MemoryStream(); context.Response.Body = discard;
                        try
                        {
                            await next(context);
                            Assert.Equal(200, context.Response.StatusCode);
                            await using var db = database.CreateDbContext(f.Tenant);
                            Assert.Single(await db.Orders.ToListAsync()); Assert.Single(await db.InventoryReservations.ToListAsync());
                            host.LostCommittedResponses++;
                            host.events.Enqueue(new { method = "POST", path = "/api/storefront/checkout", status = "discarded-after-verified-commit", payloadSha256, ownerHash, key });
                            context.Abort();
                        }
                        finally { context.Response.Body = original; }
                        return;
                    }
                }
                await next(context);
                host.events.Enqueue(new { method = context.Request.Method, path = context.Request.Path.Value, status = context.Response.StatusCode.ToString(), payloadSha256, ownerHash, key });
            });
            app.MapControllers(); await app.StartAsync();
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            var uri = new Uri(address); Assert.True(IPAddress.IsLoopback(IPAddress.Parse(uri.Host)));
            host.Client = new HttpClient(new HttpClientHandler { UseCookies = false, AllowAutoRedirect = false }) { BaseAddress = uri, Timeout = TimeSpan.FromSeconds(60) };
            return host;
        }

        public async Task<HttpResponseMessage> Send(byte[] payload, string guest, bool includeTenant = true)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/storefront/checkout");
            request.Content = new ByteArrayContent(payload); request.Content.Headers.ContentType = new("application/json");
            if (includeTenant) request.Headers.Add("X-Tenant-Id", f.Tenant.ToString());
            request.Headers.Add("X-Session-Id", guest);
            request.Headers.Add("X-Customer-Id", f.Customer.ToString()); // Untrusted; must not convert this guest to a POS customer.
            request.Headers.Add("Idempotency-Key", Prefix + runtime.Cart.CartId.ToString("N"));
            return await Client.SendAsync(request);
        }

        public async Task WriteReceipt(string scenario, object proof)
        {
            var directory = Environment.GetEnvironmentVariable("SHOPIXY_QA_EVIDENCE_DIR")!;
            await using var file = new FileStream(Path.Combine(directory, $"storefront-http-{scenario}-{f.Tenant:N}.json"), FileMode.CreateNew);
            await JsonSerializer.SerializeAsync(file, new
            {
                localOnly = true, production = false, browser = false, realCheckoutController = true, realKestrelHttp = true,
                guestOnlyAuthenticationHandlers = true, fullProductionPipeline = false, sqlSessionAndPreviewPreparation = "direct real services, not HTTP endpoints",
                transportSubstitutions = "Existing bounded cart/lease/events/velocity adapters; no broker/Redis/registered-customer JWT or Production host/tenant resolution certification",
                hostBoundary = "HTTP loopback cookie flags only; not TLS transport or browser secure-cookie persistence", scenario,
                lostCommittedResponses = LostCommittedResponses, posts = Posts, events = events.ToArray(), proof
            }, new JsonSerializerOptions { WriteIndented = true });
        }

        public async ValueTask DisposeAsync() { Client.Dispose(); await app.StopAsync(); await app.DisposeAsync(); }
    }

    private sealed class OnlyStorefrontCheckoutController : IApplicationFeatureProvider<ControllerFeature>
    {
        public void PopulateFeature(IEnumerable<ApplicationPart> parts, ControllerFeature feature)
        {
            foreach (var type in feature.Controllers.ToArray())
                if (type.AsType() != typeof(CheckoutController)) feature.Controllers.Remove(type);
        }
    }

    private sealed class GuestOnlyHttpAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync() => Task.FromResult(AuthenticateResult.NoResult());
    }
}
#endif
