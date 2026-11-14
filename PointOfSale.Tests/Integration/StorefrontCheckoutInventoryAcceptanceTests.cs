#if SHOPIXY_QA_STOREFRONT
using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MongoDB.Driver;
using Moq;
using PointOfSale.API.Configuration;
using PointOfSale.API.Controllers;
using PointOfSale.API.DTOs.Admin;
using PointOfSale.API.DTOs.Cart;
using PointOfSale.API.DTOs.Checkout;
using PointOfSale.API.Security;
using PointOfSale.API.Services;
using PointOfSale.API.Services.Admin;
using PointOfSale.API.Services.Cart;
using PointOfSale.API.Services.CommerceHub;
using PointOfSale.API.Services.Payments;
using PointOfSale.API.Services.Payments.Fawry;
using PointOfSale.API.Services.Payments.ManualPayment;
using PointOfSale.API.Services.Payments.Paymob;
using PointOfSale.API.Services.Payments.Stripe;
using PointOfSale.API.Services.PhoneRisk;
using PointOfSale.API.Services.Pricing;
using PointOfSale.API.Services.Promotions;
using PointOfSale.API.Services.Storefront;
using PointOfSale.API.Services.Checkout;
using PointOfSale.Core.Entities;
using PointOfSale.Core.Enums;
using PointOfSale.Core.Interfaces;
using PointOfSale.Core.Interfaces.Accounting;
using PointOfSale.Core.Interfaces.Marketplace;
using PointOfSale.Core.Interfaces.Payments;
using PointOfSale.Core.Interfaces.Treasury;
using PointOfSale.Infrastructure.Accounting;
using PointOfSale.Infrastructure.Data;
using PointOfSale.Infrastructure.Data.Repositories;
using PointOfSale.Infrastructure.Discounts;
using PointOfSale.Infrastructure.Instrumentation;
using PointOfSale.Infrastructure.MongoDB.Services;
using PointOfSale.Infrastructure.Observability;
using PointOfSale.Infrastructure.Payment;
using PointOfSale.Infrastructure.Payments;
using PointOfSale.Infrastructure.Services;
using PointOfSale.Infrastructure.Tenancy;
using PointOfSale.Infrastructure.Treasury;
using PointOfSale.Tests.Infrastructure;
using Gateway = PointOfSale.API.Services.Payments.ProviderContracts.IPaymentGateway;
using Tender = PointOfSale.Core.Enums.PaymentMethod;

namespace PointOfSale.Tests.Integration;

public sealed partial class PosLoopbackHttpAcceptanceTests
{
    [Theory]
    [InlineData(true, false, 1)]
    [InlineData(false, false, 0)]
    [InlineData(true, true, 0)]
    public async Task StorefrontPublicCheckout_SqlInventoryPolicy_RealOrderReplayAndCanonicalCancellation(bool tracked, bool backorder, int initialStock)
    {
        using var database = TestDatabaseScope.Open();
        var f = await Seed(database);
        await using var runtime = await StorefrontRuntime.Create(database, f, tracked, backorder, initialStock);
        try
        {
            var request = await runtime.Prepare();
            var result = await runtime.Checkout(request);
            await using (var db = database.CreateDbContext(f.Tenant))
            {
                var order = Assert.Single(await db.Orders.Include(x => x.Items).ToListAsync());
                Assert.Equal(result.OrderId, order.Id); Assert.Equal(OrderSource.Ecommerce, order.Source);
                Assert.False(order.IsPaid); Assert.Equal(PaymentStatus.Pending, order.PaymentStatus);
                Assert.Equal(10, order.Total); Assert.Equal("EGP", order.Currency); Assert.Equal(f.Warehouse, order.WarehouseId);
                var item = Assert.Single(order.Items); Assert.Equal(f.Variant, item.VariantId); Assert.Equal(10, item.UnitPrice); Assert.Equal(4, item.CostPrice);
                var stock = await db.ProductVariantStocks.SingleAsync();
                Assert.Equal(initialStock, stock.Quantity);
                Assert.Equal(tracked && !backorder ? 1 : 0, stock.ReservedQuantity);
                Assert.Equal(tracked && !backorder ? 1 : 0, await db.InventoryReservations.CountAsync());
                Assert.Empty(await db.JournalEntries.ToListAsync()); Assert.Empty(await db.PaymentIntents.ToListAsync());
                var reservationMovements = await db.StockMovements.ToListAsync();
                Assert.Equal(tracked && !backorder ? 1 : 0, reservationMovements.Count);
                Assert.All(reservationMovements, movement =>
                {
                    Assert.Equal(0, movement.QuantityChange); Assert.Equal(1, movement.ReservedQuantityChange);
                    Assert.Equal(InventoryEventType.StockReserved, movement.InventoryEventType);
                    Assert.Equal(order.Id, movement.ReferenceId); Assert.NotNull(movement.ReservationId); Assert.NotNull(movement.InventoryEventId);
                });
            }
            // A fresh service/context must recover the durable original order, not
            // re-run the now-consumed last-unit reservation or create a POS sale.
            runtime.CartReadable = false;
            var replay = await runtime.Checkout(request);
            Assert.Equal(result.OrderId, replay.OrderId); Assert.Equal(result.OrderNumber, replay.OrderNumber);
            await using var verify = database.CreateDbContext(f.Tenant);
            Assert.Single(await verify.Orders.ToListAsync());
            Assert.Equal(tracked && !backorder ? 1 : 0, await verify.InventoryReservations.CountAsync());
        }
        finally { await runtime.CancelAndVerify("policy-" + tracked + "-" + backorder); }
    }

    [Fact]
    public async Task StorefrontPublicCheckout_TrackedZeroStockRejectsWithoutOrderOrReservation()
    {
        using var database = TestDatabaseScope.Open(); var f = await Seed(database);
        await using var runtime = await StorefrontRuntime.Create(database, f, true, false, 0);
        try
        {
            var error = await Assert.ThrowsAsync<WarehouseResolutionException>(() => runtime.Prepare());
            Assert.Equal(WarehouseResolutionErrorCodes.NoEligibleWarehouse, error.ErrorCode);
            Assert.Equal(StatusCodes.Status422UnprocessableEntity, error.StatusCode);
            await using var db = database.CreateDbContext(f.Tenant);
            Assert.Empty(await db.Orders.ToListAsync()); Assert.Empty(await db.InventoryReservations.ToListAsync());
            Assert.Empty(await db.OrderRequestIdempotencyRecords.ToListAsync());
        }
        finally { await runtime.CancelAndVerify("tracked-zero-stock-rejection"); }
    }

    [Fact]
    public async Task StorefrontPublicCheckout_ReservesLastUnit_PosHttpCannotSpendIt_ExplicitCancellationMakesSamePosCartSellable()
    {
        using var database = TestDatabaseScope.Open(); var f = await Seed(database);
        await using var runtime = await StorefrontRuntime.Create(database, f, true, false, 1);
        await using var host = await Host.Start(database, f);
        try
        {
            var request = await runtime.Prepare();
            var posCart = await host.Cart(f.Cashier);
            var order = await runtime.Checkout(request);
            var denied = await host.Send(HttpMethod.Post, "/api/sales", f.Cashier, Sale(f, Tender.Cash), posCart.CartId);
            Assert.Equal(HttpStatusCode.Conflict, denied.StatusCode);
            await using (var db = database.CreateDbContext(f.Tenant))
            {
                Assert.Empty(await db.SalesInvoices.ToListAsync()); Assert.Empty(await db.JournalEntries.ToListAsync());
                var stock = await db.ProductVariantStocks.SingleAsync(); Assert.Equal(1, stock.Quantity); Assert.Equal(1, stock.ReservedQuantity);
                Assert.Equal(order.OrderId, (await db.InventoryReservations.SingleAsync()).OrderId);
                Assert.Equal(PosCartStatus.Active, (await db.PosCarts.SingleAsync()).Status);
            }
            await runtime.CancelOrders(); // Real admin cancellation + durable lifecycle worker.
            var sale = await Success<SaleResponse>(await host.Send(HttpMethod.Post, "/api/sales", f.Cashier, Sale(f, Tender.Cash), posCart.CartId));
            await using var verify = database.CreateDbContext(f.Tenant);
            Assert.Equal(sale.InvoiceId, (await verify.SalesInvoices.SingleAsync()).Id);
            Assert.Equal(0, (await verify.ProductVariantStocks.SingleAsync()).Quantity);
            Assert.Equal(0, (await verify.ProductVariantStocks.SingleAsync()).ReservedQuantity);
        }
        finally { await host.CompensateAll(); await runtime.CancelAndVerify("storefront-reservation-excludes-pos", allowCompensatedPos: true); }
    }

    [Fact]
    public async Task StorefrontPublicCheckout_PosHttpSaleInvalidatesPreview_NoOrderUntilReturnAndExplicitFreshPreview()
    {
        using var database = TestDatabaseScope.Open(); var f = await Seed(database);
        await using var runtime = await StorefrontRuntime.Create(database, f, true, false, 1);
        await using var host = await Host.Start(database, f);
        try
        {
            var stale = await runtime.Prepare();
            var posCart = await host.Cart(f.Cashier);
            await Success<SaleResponse>(await host.Send(HttpMethod.Post, "/api/sales", f.Cashier, Sale(f, Tender.Cash), posCart.CartId));
            var denied = await Assert.ThrowsAsync<WarehouseResolutionException>(() => runtime.Checkout(stale));
            Assert.Equal(WarehouseResolutionErrorCodes.InsufficientStockAtResolvedWarehouse, denied.ErrorCode);
            Assert.Equal(StatusCodes.Status409Conflict, denied.StatusCode);
            await using (var db = database.CreateDbContext(f.Tenant))
            {
                Assert.Empty(await db.Orders.ToListAsync()); Assert.Empty(await db.InventoryReservations.ToListAsync());
                Assert.Equal(0, (await db.ProductVariantStocks.SingleAsync()).Quantity);
            }
            await host.CompensateAll(); // Supported return, not SQL replenishment.
            var fresh = await runtime.Prepare(); // Explicit Storefront preview refresh, never automatic replay.
            var order = await runtime.Checkout(fresh);
            await using var verify = database.CreateDbContext(f.Tenant);
            Assert.Equal(order.OrderId, (await verify.Orders.SingleAsync()).Id);
            Assert.Equal(1, (await verify.ProductVariantStocks.SingleAsync()).ReservedQuantity);
        }
        finally { await host.CompensateAll(); await runtime.CancelAndVerify("pos-sale-invalidates-storefront-preview", allowCompensatedPos: true); }
    }

    private sealed class StorefrontRuntime(TestDatabaseScope database, Fixture f, int initialStock) : IAsyncDisposable
    {
        public ServiceProvider Services = null!;
        public CartDto Cart = null!;
        public bool CartReadable = true;
        private Guid shipping;
        private readonly ConcurrentQueue<object> operations = new();
        private Mock<Gateway> gateway = new(MockBehavior.Strict);
        private Mock<IMongoDatabase> mongo = new(MockBehavior.Strict);
        private Mock<IProductVariantReadRepository> variantProjection = new(MockBehavior.Strict);
        private Mock<IProductReadRepository> productProjection = new(MockBehavior.Strict);

        public static async Task<StorefrontRuntime> Create(TestDatabaseScope database, Fixture f, bool tracked, bool backorder, int initialStock)
        {
            var runtime = new StorefrontRuntime(database, f, initialStock) { shipping = Guid.NewGuid() };
            await using (var db = database.CreateDbContext(f.Tenant))
            {
                var product = await db.Products.SingleAsync();
                product.TrackStock = tracked; product.AllowBackorder = backorder; product.IsOnlineVisible = true; product.PublishStatus = PublishStatus.Published;
                // Initial isolated fixture state only, before any checkout authority.
                (await db.ProductVariantStocks.SingleAsync()).Quantity = initialStock;
                db.StoreConfigs.Add(new StoreConfig { Id = Guid.NewGuid(), TenantId = f.Tenant, StoreName = Prefix + "STOREFRONT", StorefrontStatus = "live",
                    Behavior = new StoreBehaviorConfig { EnableGuestCheckout = true } });
                db.ShippingMethods.Add(new ShippingMethod { Id = runtime.shipping, TenantId = f.Tenant, Name = Prefix + "manual-no-carrier", FulfillmentWarehouseId = f.Warehouse,
                    IsActive = true, CarrierCode = "manual", BaseRate = 0, PerKgRate = 0 });
                db.TenantPaymentMethods.Add(new TenantPaymentMethod { Id = Guid.NewGuid(), TenantId = f.Tenant, Code = "COD", DisplayName = Prefix + "uncollected-COD",
                    IsEnabled = true, SupportsOffline = true, SupportsOnline = false });
                await db.SaveChangesAsync();
            }
            var owner = Prefix + "guest-" + Guid.NewGuid().ToString("N");
            runtime.Cart = new CartDto { CartId = Guid.NewGuid(), TenantId = f.Tenant, OwnerType = "session", OwnerId = owner, Currency = "EGP",
                LastUpdatedUtc = DateTime.UtcNow, ExpiresAtUtc = DateTime.UtcNow.AddHours(1),
                Items = [new() { ProductId = f.Product, VariantId = f.Variant, Name = Prefix + "cart-projection-name", SKU = "stale-projection-sku", Quantity = 1, UnitPrice = 10 }] };
            runtime.Cart.RecalculateTotals();
            runtime.Services = runtime.BuildServices();
            return runtime;
        }

        private ServiceProvider BuildServices()
        {
            var services = new ServiceCollection(); services.AddLogging();
            services.AddScoped(_ => database.CreateDbContext(f.Tenant));
            services.AddScoped<ITenantContext>(_ => new TestTenantContext(f.Tenant));
            var publisher = new Mock<IEventPublisher>(); publisher.SetReturnsDefault(Task.CompletedTask); services.AddSingleton(publisher.Object);
            var marketplace = new Mock<IMarketplaceEventOutboxService>(); marketplace.SetReturnsDefault(Task.CompletedTask); services.AddSingleton(marketplace.Object);
            services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());
            services.AddSingleton<CommerceReliabilityTelemetry>();
            var carts = new Mock<ICartService>(MockBehavior.Strict);
            carts.Setup(x => x.GetCartAsync(f.Tenant, It.Is<CartOwner>(o => o.OwnerId == Cart.OwnerId && o.OwnerType == "session"), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => CartReadable ? JsonSerializer.Deserialize<CartDto>(JsonSerializer.Serialize(Cart))! : throw new InvalidOperationException("Redis cart expired after commit; replay must not read it."));
            carts.Setup(x => x.SetActiveCheckoutIdempotencyAsync(f.Tenant, It.IsAny<CartOwner>(), Cart.CartId, It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);
            services.AddSingleton(carts.Object);
            services.AddScoped<IProductVariantStockRepository, ProductVariantStockRepository>();
            services.AddScoped<ICouponValidationService, CouponValidationService>();
            services.AddScoped<IPromotionEngine, PromotionEngine>();
            services.AddScoped<IShippingCalculationService, ShippingCalculationService>();
            services.Configure<StorefrontPricingOptions>(_ => { }); services.AddScoped<ITaxCalculationService, TaxCalculationService>();
            services.AddScoped<IStorefrontPricingService, StorefrontPricingService>();
            services.AddScoped<IWarehouseResolutionService, WarehouseResolutionService>();
            services.AddScoped<IPhoneNumberNormalizer>(s => new PhoneNumberNormalizer(s.GetRequiredService<ApplicationDbContext>(), null, NullLogger<PhoneNumberNormalizer>.Instance));
            var velocity = new Mock<IOrderVelocityService>();
            velocity.Setup(x => x.TrackOrderAsync(It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync(new OrderVelocityAssessment());
            services.AddSingleton(velocity.Object); services.AddScoped<IPhoneRiskProfileService, PhoneRiskProfileService>();
            services.Configure<CheckoutSessionSecurityOptions>(_ => { }); services.AddScoped<ICheckoutSessionTokenService, CheckoutSessionTokenService>();
            services.Configure<CheckoutExecutionLockOptions>(_ => { });
            services.AddScoped<ICheckoutRequestIdempotencyCoordinator, CheckoutRequestIdempotencyCoordinator>();
            services.AddScoped<CheckoutSessionService>();
            services.AddScoped<IStorefrontPaymentRuntimeReadinessService>(s => new StorefrontPaymentRuntimeReadinessService(
                s.GetRequiredService<ApplicationDbContext>(), s.GetRequiredService<IDataProtectionProvider>(),
                Mock.Of<IStripeTenantConfigurationResolver>(MockBehavior.Strict), Mock.Of<IPaymobTenantConfigurationResolver>(MockBehavior.Strict),
                Mock.Of<IFawryTenantConfigurationResolver>(MockBehavior.Strict), Mock.Of<IManualPaymentCapabilityAdmissionAuthority>(MockBehavior.Strict),
                Options.Create(new StripeOptions()), Options.Create(new PaymobOptions()), Options.Create(new FawryOptions()), NullLogger<StorefrontPaymentRuntimeReadinessService>.Instance));
            services.AddScoped<ICheckoutService>(s => new CheckoutService(
                s.GetRequiredService<ApplicationDbContext>(), variantProjection.Object, productProjection.Object,
                Mock.Of<IShippingMethodReadRepository>(MockBehavior.Strict), Mock.Of<IPaymentMethodReadRepository>(MockBehavior.Strict),
                s.GetRequiredService<IProductVariantStockRepository>(), mongo.Object, s.GetRequiredService<IEventPublisher>(),
                s.GetRequiredService<ICouponValidationService>(), s.GetRequiredService<IPromotionEngine>(), gateway.Object,
                s.GetRequiredService<IStorefrontPaymentRuntimeReadinessService>(), s.GetRequiredService<IStorefrontPricingService>(), new LocalCheckoutLeaseManager(),
                s.GetRequiredService<ICheckoutRequestIdempotencyCoordinator>(), s.GetRequiredService<IMarketplaceEventOutboxService>(),
                s.GetRequiredService<IPhoneRiskProfileService>(), s.GetRequiredService<IOrderVelocityService>(), s.GetRequiredService<IDataProtectionProvider>(),
                Mock.Of<IPaymentIntentClientSecretProtector>(MockBehavior.Strict), Mock.Of<IPaymobSensitiveArtifactProtector>(MockBehavior.Strict),
                s.GetRequiredService<ICheckoutSessionTokenService>(), Mock.Of<ICheckoutAttachmentService>(MockBehavior.Strict), s.GetRequiredService<IWarehouseResolutionService>(),
                Options.Create(new R2Options()), Options.Create(new PaymentLifecycleSettings { RequireRowLevelLocking = true }), NullLogger<CheckoutService>.Instance,
                new HttpContextAccessor(), s.GetRequiredService<ICartService>()));
            services.AddScoped<IFinancialPostingService, FinancialPostingService>(); services.AddScoped<IAccountResolver, AccountResolver>();
            services.AddScoped<ICashboxLedgerAccountService, CashboxLedgerAccountService>(); services.AddScoped<ICommerceAccountingService, CommerceAccountingService>();
            services.AddScoped<IAccountingOutboxService, AccountingOutboxService>();
            services.AddScoped<ICorrelationIdAccessor>(_ => Mock.Of<ICorrelationIdAccessor>(x => x.GetCorrelationId() == Prefix + "storefront"));
            services.AddScoped<IPaymentLifecycleService>(s => new PaymentLifecycleService(s.GetRequiredService<ApplicationDbContext>(),
                s.GetRequiredService<IAccountingOutboxService>(), s.GetRequiredService<ICommerceAccountingService>(), s.GetRequiredService<IEventPublisher>(),
                s.GetRequiredService<IMarketplaceEventOutboxService>(), s.GetRequiredService<ICorrelationIdAccessor>(), s.GetRequiredService<IProductVariantStockRepository>(),
                Mock.Of<IInventoryReservationMetricsService>(), s.GetRequiredService<ICouponValidationService>(), NullLogger<PaymentLifecycleService>.Instance,
                Options.Create(new PaymentLifecycleSettings { RequireRowLevelLocking = true })));
            services.AddScoped<AdminOrderService>(s => new AdminOrderService(s.GetRequiredService<ApplicationDbContext>(), s.GetRequiredService<IEventPublisher>(),
                Mock.Of<IRefundEvidenceService>(MockBehavior.Strict), s.GetRequiredService<ICouponValidationService>(), s.GetRequiredService<IPaymentLifecycleService>(),
                s.GetRequiredService<IProductVariantStockRepository>(), Mock.Of<IR2StorageService>(MockBehavior.Strict), NullLogger<AdminOrderService>.Instance));
            return services.BuildServiceProvider();
        }

        public async Task<CheckoutRequestDto> Prepare()
        {
            await using var scope = Services.CreateAsyncScope();
            var session = await scope.ServiceProvider.GetRequiredService<CheckoutSessionService>().CreateOrResumeSessionAsync(
                f.Tenant, Cart.CartId, Cart.OwnerId, null, "[]", 10, "EGP");
            var resolution = await scope.ServiceProvider.GetRequiredService<IWarehouseResolutionService>().ResolveForSelectedShippingMethodAsync(
                f.Tenant, new CartOwner { OwnerType = "session", OwnerId = Cart.OwnerId }, session.Id, shipping, Prefix + "preview");
            return new CheckoutRequestDto { CartId = Cart.CartId, CheckoutSessionId = session.Id, ShippingMethodId = shipping,
                WarehouseResolutionId = resolution.Resolution.Id, WarehouseResolutionVersion = resolution.Resolution.ResolutionVersion, PaymentMethod = "COD",
                CustomerNotes = Prefix + "storefront-no-fulfillment-no-money",
                ShippingAddress = new() { Name = Prefix + "synthetic-guest", Phone = "+12025550123", Email = "qa@example.invalid", Street = "QA fixture only", City = "Cairo", Country = "EG" },
                ExpectedPricing = new() { Subtotal = 10, Total = 10 } };
        }

        public async Task<CheckoutResponseDto> Checkout(CheckoutRequestDto request)
        {
            await using var scope = Services.CreateAsyncScope();
            var result = await scope.ServiceProvider.GetRequiredService<ICheckoutService>().ProcessCheckoutAsync(f.Tenant, Cart.OwnerId, "session", Cart.OwnerId,
                null, Prefix + Cart.CartId.ToString("N"), request, Prefix + "checkout");
            // Do not export guest tracking/payment tokens, even for local fixtures.
            operations.Enqueue(new { operation = "real-public-storefront-checkout", result.OrderId, result.OrderNumber, result.TotalAmount, result.Currency });
            return result;
        }

        public async Task CancelOrders()
        {
            await using (var scope = Services.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                foreach (var id in await db.Orders.Where(x => x.Status != OrderStatus.Cancelled).Select(x => x.Id).ToListAsync())
                    await scope.ServiceProvider.GetRequiredService<AdminOrderService>().CancelOrderAsync(f.Tenant, id,
                        new AdminCancelOrderRequest { Reason = Prefix + "canonical-unpaid-order-compensation" }, Prefix + "cancel");
            }
            var processor = new PaymentCancellationOutboxProcessor(Services, NullLogger<PaymentCancellationOutboxProcessor>.Instance, Options.Create(new PaymentCancellationOutboxOptions()));
            await processor.ProcessPendingMessagesAsync();
        }

        public async Task CancelAndVerify(string scenario, bool allowCompensatedPos = false)
        {
            await CancelOrders();
            await using var verify = database.CreateDbContext(f.Tenant);
            Assert.All(await verify.Orders.ToListAsync(), x => Assert.Equal(OrderStatus.Cancelled, x.Status));
            var stock = await verify.ProductVariantStocks.SingleAsync(); Assert.Equal(initialStock, stock.Quantity); Assert.Equal(0, stock.ReservedQuantity);
            Assert.All(await verify.InventoryReservations.ToListAsync(), x => Assert.Equal(0, x.RemainingReservedQuantity));
            var movements = await verify.StockMovements.ToListAsync();
            var journals = await verify.JournalEntries.Include(x => x.LedgerEntries).ToListAsync();
            var reservations = await verify.InventoryReservations.ToListAsync();
            var reservationMovements = movements.Where(x => x.ReservationId.HasValue).ToList();
            Assert.Equal(reservations.Count * 2, reservationMovements.Count);
            Assert.All(reservationMovements, x => { Assert.Equal(0, x.QuantityChange); Assert.NotNull(x.InventoryEventId); Assert.Equal(f.Variant, x.ProductVariantId); });
            foreach (var reservation in reservations)
            {
                var pair = reservationMovements.Where(x => x.ReservationId == reservation.Id).ToList();
                Assert.Equal(2, pair.Count); Assert.Equal(0, pair.Sum(x => x.ReservedQuantityChange));
                Assert.Single(pair, x => x.InventoryEventType == InventoryEventType.StockReserved && x.ReservedQuantityChange == 1);
                Assert.Single(pair, x => x.InventoryEventType == InventoryEventType.StockReleased && x.ReservedQuantityChange == -1);
                Assert.All(pair, x => Assert.Equal(reservation.OrderId, x.ReferenceId));
            }
            if (!allowCompensatedPos)
            {
                Assert.Empty(journals);
                Assert.All(movements, x => Assert.Equal(0, x.QuantityChange));
                Assert.Empty(await verify.SalesInvoices.ToListAsync()); Assert.Empty(await verify.Returns.ToListAsync());
            }
            else
            {
                Assert.Equal(await verify.SalesInvoices.CountAsync(), await verify.Returns.CountAsync());
                Assert.All(await verify.SalesInvoices.ToListAsync(), x => Assert.Equal(InvoiceStatus.FullyReturned, x.Status));
                Assert.All(journals, x =>
                { Assert.True(x.TotalDebits > 0); Assert.True(x.LedgerEntries.Count >= 2); Assert.Equal(x.TotalDebits, x.TotalCredits); Assert.NotNull(x.CreatedByUserId); Assert.NotNull(x.AccountingPeriodId); });
                Assert.Equal(0, movements.Sum(x => x.QuantityChange));
            }
            Assert.All(await verify.LedgerAccounts.ToListAsync(), x => Assert.Equal(0, x.CurrentBalance));
            Assert.Equal(0, (await verify.Customers.SingleAsync()).Balance);
            Assert.Empty(await verify.PaymentIntents.ToListAsync());
            if (allowCompensatedPos)
                Assert.False(await verify.AccountingOutboxMessages.AnyAsync(x => x.ProcessedAtUtc == null));
            else
                Assert.Empty(await verify.AccountingOutboxMessages.ToListAsync());
            Assert.False(await verify.PaymentCancellationOutboxMessages.AnyAsync(x => x.ProcessedAtUtc == null || x.IsFailed || x.LastError != null));
            gateway.VerifyNoOtherCalls(); mongo.VerifyNoOtherCalls(); variantProjection.VerifyNoOtherCalls(); productProjection.VerifyNoOtherCalls();
            var directory = Environment.GetEnvironmentVariable("SHOPIXY_QA_EVIDENCE_DIR")!;
            var receipt = new { reference = Prefix + "STOREFRONT-" + scenario, localOnly = true, production = false, browser = false, publicCheckoutService = true,
                substitutions = "Redis/cart+execution-lease transport, event transport and velocity observation only; no provider clients executed; no MassTransit retry certification",
                fixture = f, initialStock, finalStock = stock.Quantity, reserved = stock.ReservedQuantity, operations = operations.ToArray(),
                orders = await verify.Orders.Select(x => new { x.Id, x.OrderNumber, x.Source, x.Status, x.PaymentStatus, x.CheckoutState, x.Total, x.Currency, x.WarehouseId }).ToListAsync(),
                reservations = await verify.InventoryReservations.Select(x => new { x.Id, x.OrderId, x.OrderItemId, x.VariantId, x.ReservedQuantity, x.ReleasedQuantity, x.CommittedQuantity }).ToListAsync(),
                sales = await verify.SalesInvoices.Select(x => new { x.Id, x.CreatedBy, x.Status, x.TotalAmount }).ToListAsync(),
                returns = await verify.Returns.Select(x => new { x.Id, x.OriginalInvoiceId, x.CreatedBy }).ToListAsync(),
                journals = journals.Select(x => new { x.Id, x.SourceId, x.CreatedByUserId, x.AccountingPeriodId, x.TotalDebits, x.TotalCredits }),
                movements = movements.Select(x => new { x.Id, x.ReferenceId, x.ReservationId, x.InventoryEventId, x.InventoryEventType, x.ProductVariantId, x.QuantityChange, x.ReservedQuantityChange }),
                accountingOutbox = await verify.AccountingOutboxMessages.Select(x => new { x.Id, x.BusinessEntityId, x.ProcessedAtUtc }).ToListAsync(),
                cancellationOutbox = await verify.PaymentCancellationOutboxMessages.Select(x => new { x.Id, x.OrderId, x.ProcessedAtUtc, x.IsFailed }).ToListAsync() };
            await using var file = new FileStream(Path.Combine(directory, $"storefront-checkout-{scenario}-{f.Tenant:N}.json"), FileMode.CreateNew);
            await JsonSerializer.SerializeAsync(file, receipt, new JsonSerializerOptions { WriteIndented = true });
        }

        public ValueTask DisposeAsync() => Services.DisposeAsync();
    }

    // Test-only substitute for distributed lock transport; database stock locking,
    // SQL idempotency and final SERIALIZABLE checkout transaction are not replaced.
    private sealed class LocalCheckoutLeaseManager : ICheckoutExecutionLockManager
    {
        private static readonly ConcurrentDictionary<Guid, byte> Active = new();
        public Task<ICheckoutExecutionLockLease?> TryAcquireAsync(Guid tenantId, Guid cartId, string checkoutId, string correlationId, CancellationToken cancellationToken = default)
            => Task.FromResult<ICheckoutExecutionLockLease?>(Active.TryAdd(cartId, 0) ? new Lease(cartId, checkoutId) : null);
        private sealed record Lease(Guid CartId, string CheckoutId) : ICheckoutExecutionLockLease
        {
            public bool OwnershipLost => false;
            public void ThrowIfOwnershipLost() { }
            public ValueTask DisposeAsync() { Active.TryRemove(CartId, out _); return ValueTask.CompletedTask; }
        }
    }
}
#endif
