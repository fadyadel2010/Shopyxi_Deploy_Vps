using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using PointOfSale.API.Controllers;
using PointOfSale.API.DTOs.PosCart;
using PointOfSale.API.Middleware;
using PointOfSale.API.Security;
using PointOfSale.API.Services;
using PointOfSale.API.Services.Audit;
using PointOfSale.API.Services.Pos;
using PointOfSale.Core.Entities;
using PointOfSale.Core.Enums;
using PointOfSale.Core.Interfaces;
using PointOfSale.Core.Interfaces.Accounting;
using PointOfSale.Core.Interfaces.Treasury;
using PointOfSale.Core.Security;
using PointOfSale.Infrastructure.Accounting;
using PointOfSale.Infrastructure.Data;
using PointOfSale.Infrastructure.Treasury;
using PointOfSale.Tests.Infrastructure;
using Tender = PointOfSale.Core.Enums.PaymentMethod;

namespace PointOfSale.Tests.Integration;

/// <summary>
/// Real loopback Kestrel + MVC + PostgreSQL, not a Production/browser acceptance.
/// Only transport/event publishing and synthetic local authentication are substituted.
/// No gateway, SMTP, shipping, queue transport, database reset or external host is used.
/// </summary>
public sealed partial class PosLoopbackHttpAcceptanceTests
{
    private const string Prefix = "QA-ERP-R10-20261113-POS-HTTP-";

    [Theory]
    [InlineData(Tender.Cash)]
    [InlineData(Tender.Card)]
    [InlineData(Tender.Mixed)]
    [InlineData(Tender.Credit)]
    public async Task CommittedResponseLoss_ReloadAndExplicitReplayRetainOneSale_ThenCanonicalFullReturnRestoresMoneyAndStock(Tender tender)
    {
        using var database = TestDatabaseScope.Open(); var fixture = await Seed(database);
        await using var host = await Host.Start(database, fixture);
        try
        {
            var cart = await host.Cart(fixture.Cashier);
            var sale = Sale(fixture, tender);
            // The transport fault is armed in test memory only, never via a public API/header.
            host.LoseNextSaleResponse = true;
            await Assert.ThrowsAsync<HttpRequestException>(() => host.Send(HttpMethod.Post, "/api/sales", fixture.Cashier, sale, cart.CartId));
            await using (var db = database.CreateDbContext(fixture.Tenant))
            {
                Assert.Single(await db.SalesInvoices.ToListAsync());
                Assert.Equal(0, (await db.ProductVariantStocks.SingleAsync()).Quantity);
                Assert.Equal(1, host.LostCommittedResponses);
            }
            // A fresh HTTP client models a reload/reconnect. No write occurs on read recovery.
            using var reloaded = host.NewClient();
            var recovered = await host.Read<PosCartResponse>(reloaded, $"/api/pos-carts/{cart.CartId}", fixture.Cashier);
            Assert.Equal(PosCartStatus.Completed, recovered.Status); Assert.NotNull(recovered.CompletedSaleId);
            var replay = await host.Send(HttpMethod.Post, "/api/sales", fixture.Cashier, sale, cart.CartId);
            var result = await Success<SaleResponse>(replay);
            Assert.Equal(recovered.CompletedSaleId, result.InvoiceId);
            var changed = await host.Send(HttpMethod.Post, "/api/sales", fixture.Cashier, sale with { Notes = Prefix + "changed-request" }, cart.CartId);
            Assert.Equal(HttpStatusCode.BadRequest, changed.StatusCode);
            Assert.Equal("Idempotency key reuse with different payload", (await changed.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("message").GetString());
            var foreign = await host.Send(HttpMethod.Get, $"/api/pos-carts/{cart.CartId}", fixture.SecondCashier);
            Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
            var denied = await host.Send(HttpMethod.Post, "/api/salesreturns", fixture.Cashier, new CreateSalesReturnRequest
                { WarehouseId = fixture.Warehouse, OriginalInvoiceId = result.InvoiceId, ReturnDate = DateTime.UtcNow,
                    Items = [new() { ProductId = fixture.Product, ProductVariantId = fixture.Variant, UnitId = fixture.Unit, Quantity = 1 }] });
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
            await AssertSingleSale(database, fixture, result.InvoiceId);
        }
        finally { await host.CompensateAll(); await host.AssertCompensatedAndWriteReceipt("lost-response-" + tender); }
    }

    [Fact]
    public async Task TwoRealCashierCartsContendForLastUnit_OnlyOneHttpSaleCommits_AndLoserRemainsRecoverable()
    {
        using var database = TestDatabaseScope.Open(); var fixture = await Seed(database);
        await using var host = await Host.Start(database, fixture);
        try
        {
            var first = await host.Cart(fixture.Cashier); var second = await host.Cart(fixture.SecondCashier);
            var results = await Task.WhenAll(host.Send(HttpMethod.Post, "/api/sales", fixture.Cashier, Sale(fixture, Tender.Cash), first.CartId),
                host.Send(HttpMethod.Post, "/api/sales", fixture.SecondCashier, Sale(fixture, Tender.Cash), second.CartId));
            Assert.Single(results, x => x.StatusCode == HttpStatusCode.OK);
            Assert.Single(results, x => x.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.BadRequest);
            var result = await Success<SaleResponse>(results.Single(x => x.StatusCode == HttpStatusCode.OK));
            await AssertSingleSale(database, fixture, result.InvoiceId);
            var loser = results[0].StatusCode == HttpStatusCode.OK ? (fixture.SecondCashier, second) : (fixture.Cashier, first);
            var cart = await host.Read<PosCartResponse>(host.Client, $"/api/pos-carts/{loser.Item2.CartId}", loser.Item1);
            Assert.Null(cart.CompletedSaleId); Assert.Equal(PosCartStatus.Active, cart.Status);
            await Success<PosCartResponse>(await host.Send(HttpMethod.Post, $"/api/pos-carts/{cart.CartId}/cancel", loser.Item1,
                new PosCartLifecycleRequest { ExpectedVersion = cart.Version, Reason = Prefix + "losing-cart-compensation" }));
        }
        finally { await host.CompensateAll(); await host.AssertCompensatedAndWriteReceipt("last-unit-two-cashiers"); }
    }

    [Fact]
    public async Task HeldCartRejectsCheckout_StaleResumeDoesNotChangeIt_ThenConcurrentExactSubmitConvergesToOneSale()
    {
        using var database = TestDatabaseScope.Open(); var fixture = await Seed(database);
        await using var host = await Host.Start(database, fixture);
        try
        {
            var cart = await host.Cart(fixture.Cashier);
            var held = await Success<PosCartResponse>(await host.Send(HttpMethod.Post, $"/api/pos-carts/{cart.CartId}/hold", fixture.Cashier,
                new PosCartLifecycleRequest { ExpectedVersion = cart.Version, Reason = Prefix + "hold" }));
            Assert.Equal(PosCartStatus.Held, held.Status);
            var denied = await host.Send(HttpMethod.Post, "/api/sales", fixture.Cashier, Sale(fixture, Tender.Cash), cart.CartId);
            Assert.Equal(HttpStatusCode.Conflict, denied.StatusCode);
            var stale = await host.Send(HttpMethod.Post, $"/api/pos-carts/{cart.CartId}/resume", fixture.Cashier,
                new PosCartLifecycleRequest { ExpectedVersion = cart.Version, Reason = Prefix + "stale-resume" });
            Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
            var retained = await host.Read<PosCartResponse>(host.Client, $"/api/pos-carts/{cart.CartId}", fixture.Cashier);
            Assert.Equal(held.Version, retained.Version); Assert.Equal(PosCartStatus.Held, retained.Status);
            var resumed = await Success<PosCartResponse>(await host.Send(HttpMethod.Post, $"/api/pos-carts/{cart.CartId}/resume", fixture.Cashier,
                new PosCartLifecycleRequest { ExpectedVersion = held.Version, Reason = Prefix + "explicit-resume" }));
            Assert.Equal(cart.Items.Single().ItemId, resumed.Items.Single().ItemId);
            Assert.Equal(cart.TotalAmount, resumed.TotalAmount);
            var sale = Sale(fixture, Tender.Cash);
            var results = await Task.WhenAll(host.Send(HttpMethod.Post, "/api/sales", fixture.Cashier, sale, cart.CartId),
                host.Send(HttpMethod.Post, "/api/sales", fixture.Cashier, sale, cart.CartId));
            var accepted = await Success<SaleResponse>(results[0]); var replay = await Success<SaleResponse>(results[1]);
            Assert.Equal(accepted.InvoiceId, replay.InvoiceId); await AssertSingleSale(database, fixture, accepted.InvoiceId);
        }
        finally { await host.CompensateAll(); await host.AssertCompensatedAndWriteReceipt("hold-resume-double-submit"); }
    }

    [Fact]
    public async Task ClosedPeriodHttpSaleRollsBackCartStockPartyAndMoney_ThenExplicitReopenAllowsTheSameUncommittedIntent()
    {
        using var database = TestDatabaseScope.Open(); var fixture = await Seed(database);
        await using var host = await Host.Start(database, fixture);
        try
        {
            var cart = await host.Cart(fixture.Cashier);
            await using (var db = database.CreateDbContext(fixture.Tenant))
            {
                var period = await db.AccountingPeriods.SingleAsync();
                await new AccountingPeriodService(db).TransitionAsync(fixture.Tenant, period.Id, fixture.Owner, "close", period.Version, Prefix + "close-before-payment");
            }
            var denied = await host.Send(HttpMethod.Post, "/api/sales", fixture.Cashier, Sale(fixture, Tender.Cash), cart.CartId);
            Assert.Equal(HttpStatusCode.Conflict, denied.StatusCode);
            Assert.Equal("PERIOD_NOT_OPEN", (await denied.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
            await AssertNoFinancialCommit(database, fixture, cart.CartId, cart.Version);
            await using (var db = database.CreateDbContext(fixture.Tenant))
            {
                var period = await db.AccountingPeriods.SingleAsync();
                await new AccountingPeriodService(db).TransitionAsync(fixture.Tenant, period.Id, fixture.Owner, "reopen", period.Version, Prefix + "explicit-reopen");
            }
            var accepted = await Success<SaleResponse>(await host.Send(HttpMethod.Post, "/api/sales", fixture.Cashier, Sale(fixture, Tender.Cash), cart.CartId));
            await AssertSingleSale(database, fixture, accepted.InvoiceId);
        }
        finally { await host.CompensateAll(); await host.AssertCompensatedAndWriteReceipt("closed-period-rollback"); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StaleSyntheticCashierPrincipalCannotPostAfterSqlAuthorityRevocation(bool disabled)
    {
        using var database = TestDatabaseScope.Open(); var fixture = await Seed(database);
        await using var host = await Host.Start(database, fixture);
        try
        {
            // This first authenticated request freezes the synthetic principal's
            // original claims. Later requests keep them, like an unexpired JWT.
            var cart = await host.Cart(fixture.Cashier);
            await using (var db = database.CreateDbContext(fixture.Tenant))
            {
                var user = await db.Users.SingleAsync(x => x.Id == fixture.Cashier);
                if (disabled) user.IsActive = false; else user.Role = UserRole.Warehouse;
                await db.SaveChangesAsync();
            }
            var denied = await host.Send(HttpMethod.Post, "/api/sales", fixture.Cashier, Sale(fixture, Tender.Cash), cart.CartId);
            Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
            var body = await denied.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("LOCAL_FIXTURE_SESSION_DENIED", body.GetProperty("code").GetString());
            Assert.Equal(disabled ? "inactive_or_deleted_actor" : "stale_actor_role_claim", body.GetProperty("reason").GetString());
            await AssertNoFinancialCommit(database, fixture, cart.CartId, cart.Version);
            await using (var db = database.CreateDbContext(fixture.Tenant))
            {
                var user = await db.Users.SingleAsync(x => x.Id == fixture.Cashier);
                user.IsActive = true; user.Role = UserRole.Cashier; await db.SaveChangesAsync();
            }
            var accepted = await Success<SaleResponse>(await host.Send(HttpMethod.Post, "/api/sales", fixture.Cashier, Sale(fixture, Tender.Cash), cart.CartId));
            await AssertSingleSale(database, fixture, accepted.InvoiceId);
        }
        finally { await host.CompensateAll(); await host.AssertCompensatedAndWriteReceipt("stale-cashier-" + (disabled ? "disabled" : "downgraded")); }
    }

    private static async Task AssertNoFinancialCommit(TestDatabaseScope database, Fixture f, Guid cartId, int expectedVersion)
    {
        await using var db = database.CreateDbContext(f.Tenant);
        Assert.Empty(await db.SalesInvoices.ToListAsync()); Assert.Empty(await db.JournalEntries.ToListAsync());
        Assert.Empty(await db.StockMovements.ToListAsync()); Assert.Empty(await db.CustomerTransactions.ToListAsync());
        Assert.Empty(await db.AccountLedgers.ToListAsync()); Assert.Empty(await db.AccountingOutboxMessages.ToListAsync());
        Assert.Empty(await db.SalesRequestIdempotencyRecords.ToListAsync());
        Assert.All(await db.LedgerAccounts.ToListAsync(), x => Assert.Equal(0, x.CurrentBalance));
        Assert.Equal(0, (await db.Customers.SingleAsync()).Balance);
        Assert.Equal(1, (await db.ProductVariantStocks.SingleAsync()).Quantity);
        var cart = await db.PosCarts.SingleAsync(x => x.Id == cartId);
        Assert.Equal(PosCartStatus.Active, cart.Status); Assert.Equal(expectedVersion, cart.Version); Assert.Null(cart.CompletedSaleId);
    }

    private static async Task AssertSingleSale(TestDatabaseScope database, Fixture f, Guid saleId)
    {
        await using var db = database.CreateDbContext(f.Tenant);
        var sale = Assert.Single(await db.SalesInvoices.Include(x => x.Items).ToListAsync()); Assert.Equal(saleId, sale.Id);
        var item = Assert.Single(sale.Items); Assert.Equal(f.Variant, item.ProductVariantId); Assert.Equal(10, sale.TotalAmount);
        Assert.Equal(0, (await db.ProductVariantStocks.SingleAsync()).Quantity);
        Assert.Single(await db.StockMovements.Where(x => x.ReferenceId == saleId).ToListAsync());
        Assert.Single(await db.PosCarts.Where(x => x.Status == PosCartStatus.Completed && x.CompletedSaleId == saleId).ToListAsync());
        Assert.Equal(2, await db.JournalEntries.CountAsync()); // Revenue and frozen-cost COGS.
        Assert.False(await db.AccountingOutboxMessages.AnyAsync(x => x.ProcessedAtUtc == null));
    }

    private sealed record Fixture(Guid Tenant, Guid Owner, Guid Cashier, Guid SecondCashier, Guid Warehouse, Guid Product, Guid Variant, Guid Unit, Guid Customer);
    private static async Task<Fixture> Seed(TestDatabaseScope database)
    {
        var f = new Fixture(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        await using var db = database.CreateDbContext(f.Tenant);
        db.Tenants.Add(new Tenant { Id = f.Tenant, Name = Prefix + "fixture", Slug = "qa-pos-" + f.Tenant.ToString("N"), Currency = "EGP", IsActive = true, Status = TenantStatus.Active });
        foreach (var (id, role) in new[] { (f.Owner, UserRole.Owner), (f.Cashier, UserRole.Cashier), (f.SecondCashier, UserRole.Cashier) })
            db.Users.Add(new User { Id = id, TenantId = f.Tenant, Username = "qa-" + id.ToString("N"), Email = id.ToString("N") + "@example.invalid", Role = role, IsActive = true, PasswordHash = "not-a-login",
                RefreshToken = "local-fixture-session-" + id.ToString("N"), RefreshTokenExpiryTime = DateTime.UtcNow.AddHours(1) });
        var category = Guid.NewGuid();
        db.Categories.Add(new Category { Id = category, TenantId = f.Tenant, Name = Prefix + "category" });
        db.Warehouses.Add(new Warehouse { Id = f.Warehouse, TenantId = f.Tenant, Name = Prefix + "warehouse", Code = "QA-POS", IsActive = true, IsDefault = true });
        db.Customers.Add(new Customer { Id = f.Customer, TenantId = f.Tenant, Name = Prefix + "customer", IsActive = true });
        db.Units.Add(new PointOfSale.Core.Entities.Unit { Id = f.Unit, TenantId = f.Tenant, Name = "Piece", Symbol = "pc", IsActive = true });
        db.Products.Add(new Product { Id = f.Product, TenantId = f.Tenant, CategoryId = category, Name = Prefix + "variant-product", Slug = "qa-pos-product", Unit = "Piece",
            IsActive = true, HasVariants = true, TrackStock = true, SalePrice = 10, PurchasePrice = 4, TaxRate = 0 });
        db.ProductVariants.Add(new ProductVariant { Id = f.Variant, TenantId = f.Tenant, ProductId = f.Product, SKU = Prefix + "BLUE", Price = 10, CostPrice = 4, IsDefault = true, IsActive = true });
        db.ProductUnits.Add(new ProductUnit { Id = Guid.NewGuid(), ProductId = f.Product, UnitId = f.Unit, ConversionFactor = 1, SellingPrice = 10, PurchasePrice = 4, IsDefault = true, IsBaseUnit = true });
        // Isolated initial stock fixture, never an adjustment to existing Production stock.
        db.ProductVariantStocks.Add(new ProductVariantStock { Id = Guid.NewGuid(), TenantId = f.Tenant, WarehouseId = f.Warehouse, VariantId = f.Variant, Quantity = 1 });
        await db.SaveChangesAsync();
        await new TenantAccountSeeder(db, NullLogger<TenantAccountSeeder>.Instance).SeedAsync(f.Tenant);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        await new AccountingPeriodService(db).CreateAsync(f.Tenant, f.Owner, "QA-POS", Prefix + "period", today.AddDays(-1), today.AddDays(1), true, Prefix + "explicit-period");
        var cashbox = new Cashbox { Id = Guid.NewGuid(), TenantId = f.Tenant, WarehouseId = f.Warehouse, Name = Prefix + "cashbox", IsActive = true };
        db.Cashboxes.Add(cashbox); await db.SaveChangesAsync();
        await new CashboxLedgerAccountService(db, NullLogger<CashboxLedgerAccountService>.Instance).EnsureCashboxAccountAsync(cashbox);
        return f;
    }

    private static CreateSaleRequest Sale(Fixture f, Tender tender) => new()
    {
        WarehouseId = f.Warehouse, CustomerId = f.Customer, PaymentMethod = tender, PaidAmount = tender == Tender.Credit ? 0 : 10,
        CashAmount = tender == Tender.Cash ? 10 : tender == Tender.Mixed ? 6 : 0, CardAmount = tender == Tender.Card ? 10 : tender == Tender.Mixed ? 4 : 0,
        Notes = Prefix + "synthetic-" + tender,
        Items = [new() { ProductId = f.Product, ProductVariantId = f.Variant, UnitId = f.Unit, Quantity = 1, UnitPrice = 10 }]
    };

    private static async Task<T> Success<T>(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"HTTP {(int)response.StatusCode}: {body}");
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
        return JsonSerializer.Deserialize<T>(body, options)!;
    }

    private sealed class Host(WebApplication app, TestDatabaseScope database, Fixture f) : IAsyncDisposable
    {
        public HttpClient Client { get; private set; } = null!;
        public bool LoseNextSaleResponse;
        public bool CashierWritesEnabled = true;
        public int LostCommittedResponses;
        private readonly ConcurrentQueue<object> requests = new();
        public HttpClient NewClient() => new() { BaseAddress = Client.BaseAddress, Timeout = TimeSpan.FromSeconds(60) };

        public static async Task<Host> Start(TestDatabaseScope database, Fixture f,
            Action<WebApplicationBuilder>? configureServices = null, Action<WebApplication>? configureEndpoints = null)
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "LocalRetainedQa" });
            builder.Logging.ClearProviders(); builder.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Loopback, 0));
            builder.Services.AddSingleton(f);
            builder.Services.AddSingleton<FixturePrincipalCache>();
            builder.Services.AddScoped(_ => database.CreateDbContext(f.Tenant));
            builder.Services.AddScoped<ITenantContext>(_ => new TestTenantContext(f.Tenant));
            builder.Services.AddScoped<IPosCartRuntimeService, PosCartRuntimeService>();
            builder.Services.AddScoped<LedgerService>();
            builder.Services.AddScoped<ILedgerService>(s => s.GetRequiredService<LedgerService>());
            builder.Services.AddScoped<IPartyBalanceMaintenanceService>(s => s.GetRequiredService<LedgerService>());
            builder.Services.AddScoped<ISaleFinancialIntegrityService, SaleFinancialIntegrityService>();
            builder.Services.AddScoped<IValidationService>(_ => Mock.Of<IValidationService>());
            builder.Services.AddScoped<IAuditService>(_ => Mock.Of<IAuditService>());
            var publisher = new Mock<IEventPublisher>(); publisher.SetReturnsDefault(Task.CompletedTask);
            builder.Services.AddSingleton(publisher.Object); // No external event transport installed.
            builder.Services.AddScoped<ICorrelationIdAccessor>(_ => Mock.Of<ICorrelationIdAccessor>(x => x.GetCorrelationId() == Prefix + "loopback"));
            builder.Services.AddScoped<IAccountingOutboxService, AccountingOutboxService>();
            builder.Services.AddScoped<IFinancialPostingService, FinancialPostingService>();
            builder.Services.AddScoped<IAccountResolver, AccountResolver>();
            builder.Services.AddScoped<ICashboxLedgerAccountService, CashboxLedgerAccountService>();
            builder.Services.AddScoped<ICommerceAccountingService, CommerceAccountingService>();
            builder.Services.AddScoped<AccountingActorContext>();
            builder.Services.AddScoped<IAdminTenantAuthority, AdminTenantAuthority>();
            builder.Services.AddScoped<IPosSessionAuthority, PosSessionAuthority>();
            builder.Services.AddAuthentication("LocalFixture").AddScheme<AuthenticationSchemeOptions, FixtureAuthentication>("LocalFixture", _ => { });
            builder.Services.AddSingleton<IAuthorizationHandler, PosPermissionAuthorizationHandler>();
            builder.Services.AddAuthorization(o => { foreach (var permission in PosPermissions.All) o.AddPolicy(permission, p => p.RequireAuthenticatedUser().AddRequirements(new PosPermissionRequirement(permission))); });
            builder.Services.AddControllers(o => o.Conventions.Add(new PosPermissionApplicationModelConvention()))
                .AddApplicationPart(typeof(SalesController).Assembly).ConfigureApplicationPartManager(m => m.FeatureProviders.Add(new OnlyPosControllers()));
            configureServices?.Invoke(builder);
            var app = builder.Build(); var host = new Host(app, database, f);
            app.UseMiddleware<GlobalExceptionMiddleware>(); app.UseAuthentication();
            app.UseMiddleware<AdminTenantAuthorityMiddleware>(); app.UseAuthorization();
            app.Use(async (context, next) =>
            {
                if (!host.CashierWritesEnabled && context.Request.Method is not ("GET" or "HEAD" or "OPTIONS") &&
                    context.User.FindFirstValue(ClaimTypes.NameIdentifier) != f.Owner.ToString())
                {
                    context.Response.StatusCode = StatusCodes.Status409Conflict;
                    await context.Response.WriteAsJsonAsync(new { code = "LOCAL_QA_CLEANUP_STARTED" });
                    return;
                }
                if (context.Request.Path == "/api/sales" && context.Request.Method == "POST" && host.LoseNextSaleResponse)
                {
                    host.LoseNextSaleResponse = false;
                    var originalBody = context.Response.Body; await using var discard = new MemoryStream(); context.Response.Body = discard;
                    try
                    {
                        await next(context);
                        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
                        await using var verify = database.CreateDbContext(f.Tenant);
                        Assert.Single(await verify.SalesInvoices.ToListAsync());
                        host.LostCommittedResponses++; context.Abort();
                    }
                    finally { context.Response.Body = originalBody; }
                    return;
                }
                await next(context);
            });
            app.MapControllers(); configureEndpoints?.Invoke(app); await app.StartAsync();
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            Assert.True(Uri.TryCreate(address, UriKind.Absolute, out var uri) && IPAddress.IsLoopback(IPAddress.Parse(uri.Host)));
            host.Client = new HttpClient { BaseAddress = uri, Timeout = TimeSpan.FromSeconds(60) }; return host;
        }

        public async Task<HttpResponseMessage> Send(HttpMethod method, string path, Guid actor, object? body = null, Guid? cart = null)
        {
            using var request = new HttpRequestMessage(method, path);
            request.Headers.Add("X-Local-Fixture-Actor", actor.ToString());
            request.Headers.Add("Idempotency-Key", Prefix + Guid.NewGuid().ToString("N"));
            if (cart.HasValue) { request.Headers.Add("X-Pos-Cart-Id", cart.Value.ToString()); request.Headers.Add("X-Payment-Attempt-Id", Prefix + cart.Value.ToString("N")); }
            if (body != null) request.Content = JsonContent.Create(body);
            try { var response = await Client.SendAsync(request); requests.Enqueue(new { method = method.Method, path, actor, status = (int)response.StatusCode }); return response; }
            catch (HttpRequestException) { requests.Enqueue(new { method = method.Method, path, actor, status = "transport-response-lost" }); throw; }
        }

        public async Task<T> Read<T>(HttpClient client, string path, Guid actor)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, path); request.Headers.Add("X-Local-Fixture-Actor", actor.ToString());
            return await Success<T>(await client.SendAsync(request));
        }

        public async Task<PosCartResponse> Cart(Guid actor)
        {
            var created = await Success<PosCartResponse>(await Send(HttpMethod.Post, "/api/pos-carts", actor, new CreatePosCartRequest { WarehouseId = f.Warehouse, CustomerId = f.Customer }));
            return await Success<PosCartResponse>(await Send(HttpMethod.Post, $"/api/pos-carts/{created.CartId}/items", actor,
                new UpsertPosCartItemRequest { ProductId = f.Product, ProductVariantId = f.Variant, UnitId = f.Unit, Quantity = 1, ExpectedVersion = created.Version }));
        }

        public async Task CompensateAll()
        {
            await using var db = database.CreateDbContext(f.Tenant);
            foreach (var invoice in await db.SalesInvoices.Include(x => x.Items).Where(x => x.Status != InvoiceStatus.FullyReturned).ToListAsync())
            {
                var response = await Send(HttpMethod.Post, "/api/salesreturns", f.Owner, new CreateSalesReturnRequest
                {
                    WarehouseId = invoice.WarehouseId, CustomerId = invoice.CustomerId, OriginalInvoiceId = invoice.Id, ReturnDate = DateTime.UtcNow,
                    Reason = Prefix + "canonical-full-compensation", Notes = Prefix + "retained-history",
                    RefundMethod = invoice.PaymentMethod switch { Tender.Cash => RefundMethod.Cash, Tender.Card => RefundMethod.Card,
                        Tender.Mixed => RefundMethod.Mixed, _ => RefundMethod.AccountCredit },
                    CashAmount = invoice.PaymentMethod == Tender.Cash ? invoice.PaidAmount : invoice.PaymentMethod == Tender.Mixed ? invoice.CashAmount : 0,
                    CardAmount = invoice.PaymentMethod == Tender.Card ? invoice.PaidAmount : invoice.PaymentMethod == Tender.Mixed ? invoice.CardAmount : 0,
                    Items = invoice.Items.Select(x => new SalesReturnItemRequest { ProductId = x.ProductId, ProductVariantId = x.ProductVariantId, UnitId = x.UnitId, Quantity = x.Quantity }).ToList()
                });
                await Success<SalesReturnResponse>(response);
            }
        }

        public async Task AssertCompensatedAndWriteReceipt(string scenario)
        {
            await using var db = database.CreateDbContext(f.Tenant);
            Assert.Equal(1, (await db.ProductVariantStocks.SingleAsync()).Quantity); Assert.Equal(0, (await db.ProductVariantStocks.SingleAsync()).ReservedQuantity);
            Assert.All(await db.LedgerAccounts.ToListAsync(), x => Assert.Equal(0, x.CurrentBalance));
            Assert.Equal(0, (await db.Customers.SingleAsync()).Balance);
            Assert.Single(await db.SalesInvoices.ToListAsync()); Assert.Single(await db.Returns.ToListAsync());
            Assert.False(await db.AccountingOutboxMessages.AnyAsync(x => x.ProcessedAtUtc == null));
            var journals = await db.JournalEntries.Include(x => x.LedgerEntries).ToListAsync();
            Assert.All(journals, x => { Assert.NotNull(x.CreatedByUserId); Assert.Equal(x.TotalDebits, x.TotalCredits); });
            var directory = Environment.GetEnvironmentVariable("SHOPIXY_QA_EVIDENCE_DIR")!;
            var receipt = new { reference = Prefix + scenario, localOnly = true, loopbackHttp = true, production = false, browser = scenario == "headed-browser",
                authentication = "cached synthetic fixture claims validated by real PosSessionAuthority and AdminTenantAuthority; no real login/JWT signature certification", fixture = f, lostCommittedResponses = LostCommittedResponses,
                requests = requests.ToArray(), stock = 1, reserved = 0, netLedgerBalances = 0, customerBalance = 0,
                sales = await db.SalesInvoices.Select(x => new { x.Id, x.InvoiceNumber, x.CreatedBy, x.PaymentMethod, x.TotalAmount, x.Status }).ToListAsync(),
                returns = await db.Returns.Select(x => new { x.Id, x.ReturnNumber, x.OriginalInvoiceId, x.CreatedBy }).ToListAsync(),
                journals = journals.Select(x => new { x.Id, x.SourceType, x.SourceId, x.CreatedByUserId, x.AccountingPeriodId, x.TotalDebits, x.TotalCredits }),
                stockMovements = await db.StockMovements.Select(x => new { x.Id, x.ReferenceId, x.ProductVariantId, x.QuantityChange }).ToListAsync() };
            await using var file = new FileStream(Path.Combine(directory, $"pos-loopback-{scenario}-{f.Tenant:N}.json"), FileMode.CreateNew);
            await JsonSerializer.SerializeAsync(file, receipt, new JsonSerializerOptions { WriteIndented = true });
        }

        public async ValueTask DisposeAsync() { Client.Dispose(); await app.StopAsync(); await app.DisposeAsync(); }
    }

    private sealed class OnlyPosControllers : IApplicationFeatureProvider<ControllerFeature>
    {
        public void PopulateFeature(IEnumerable<ApplicationPart> parts, ControllerFeature feature)
        {
            foreach (var type in feature.Controllers.ToArray())
                if (type.AsType() != typeof(SalesController) && type.AsType() != typeof(PosCartsController) && type.AsType() != typeof(SalesReturnsController)) feature.Controllers.Remove(type);
        }
    }

    private sealed class FixturePrincipalCache
    {
        public ConcurrentDictionary<Guid, string> Roles { get; } = new();
    }

    private sealed class FixtureAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder,
        ApplicationDbContext db, Fixture fixture, FixturePrincipalCache cache, IPosSessionAuthority sessionAuthority) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Guid.TryParse(Request.Headers["X-Local-Fixture-Actor"], out var id) || (id != fixture.Owner && id != fixture.Cashier && id != fixture.SecondCashier))
                return AuthenticateResult.NoResult();
            if (!cache.Roles.TryGetValue(id, out var role))
            {
                var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id && x.TenantId == fixture.Tenant && x.IsActive && !x.IsDeleted);
                if (user == null) return AuthenticateResult.Fail("Local fixture actor unavailable");
                role = cache.Roles.GetOrAdd(id, user.Role.ToString());
            }
            var claims = new[] { new Claim(ClaimTypes.NameIdentifier, id.ToString()), new Claim(ClaimTypes.Role, role),
                new Claim("tenant_id", fixture.Tenant.ToString()), new Claim(PosSecurityConstants.SessionBindingClaim, "local-fixture-session-" + id.ToString("N")),
                new Claim(PosSecurityConstants.SqlAuthorityClaim, PosSecurityConstants.SqlAuthorityValue) };
            var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme.Name));
            var authority = await sessionAuthority.ValidateAccessTokenAsync(principal, Context.RequestAborted);
            if (!authority.Authorized)
            {
                Context.Items["LocalFixture.SessionDenial"] = authority.Reason;
                return AuthenticateResult.Fail(authority.Reason);
            }
            return AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name));
        }

        protected override async Task HandleChallengeAsync(AuthenticationProperties properties)
        {
            Response.StatusCode = StatusCodes.Status401Unauthorized;
            await Response.WriteAsJsonAsync(new { code = "LOCAL_FIXTURE_SESSION_DENIED", reason = Context.Items["LocalFixture.SessionDenial"] ?? "unauthenticated_fixture_actor" });
        }
    }
}
