using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using PointOfSale.API.Controllers;
using PointOfSale.API.DTOs.PosCart;
using PointOfSale.API.Services.Pos;
using PointOfSale.Core.Entities;
using PointOfSale.Core.Enums;
using PointOfSale.Infrastructure.Data;
using PointOfSale.Tests.Infrastructure;
using System.Security.Claims;

namespace PointOfSale.Tests;

public sealed class PosCartRuntimeServiceTests
{
    [Theory]
    [InlineData(10, true, false, 0, 60)]
    [InlineData(0, true, false, 0, 0)]
    [InlineData(15, true, false, 0, 90)]
    [InlineData(10, false, false, 0, 0)]
    [InlineData(10, true, true, 0, 0)]
    [InlineData(10, true, false, 50, 55)]
    public async Task TaxPolicy_PersistsPosSnapshotAndMatchesStorefrontForSameBase(
        decimal rate, bool enabled, bool inclusive, decimal discount, decimal expectedTax)
    {
        var fixture = await CreateFixtureAsync();
        await using var db = fixture.Db;
        var product = await db.Products.SingleAsync();
        product.TaxRate = rate; product.PriceIncludesTax = inclusive;
        (await db.ProductUnits.SingleAsync()).SellingPrice = 300m;
        db.SystemSettings.AddRange(new SystemSetting { TenantId = fixture.TenantId, Key = "Tax.Enable", Value = enabled.ToString() },
            new SystemSetting { TenantId = fixture.TenantId, Key = "Tax.Rate", Value = "23" });
        await db.SaveChangesAsync();
        var service = CreateService(db, fixture.TenantId);
        var cart = (await service.CreateAsync(new() { WarehouseId = fixture.WarehouseId }, fixture.CashierId, "p519-create")).Value!;
        var result = await service.AddItemAsync(cart.CartId, new()
        { ProductId = product.Id, ProductVariantId = fixture.VariantId, UnitId = fixture.UnitId,
            Quantity = 2, DiscountAmount = discount, ExpectedVersion = cart.Version }, fixture.CashierId, "p519-add");
        Assert.Equal(StatusCodes.Status200OK, result.StatusCode);
        var saved = await db.PosCartItems.AsNoTracking().SingleAsync();
        Assert.Equal(expectedTax, saved.TaxAmount);
        Assert.Equal(enabled ? rate : 0m, saved.TaxRateSnapshot);
        Assert.Equal(600m - discount + expectedTax, saved.TotalAmount);
        var tax = new PointOfSale.API.Services.Pricing.TaxCalculationService(db,
            Microsoft.Extensions.Options.Options.Create(new PointOfSale.API.Services.Pricing.StorefrontPricingOptions()),
            NullLogger<PointOfSale.API.Services.Pricing.TaxCalculationService>.Instance);
        var storefront = await tax.CalculateLinesAsync(fixture.TenantId,
            [new(product.Id, fixture.VariantId, 600m - discount)], "EG");
        Assert.Equal(saved.TaxAmount, storefront.TaxAmount);
    }

    [Fact]
    public async Task CreateAndAddItem_SnapshotsPriceValidatesInventoryAndStagesOutbox()
    {
        var fixture = await CreateFixtureAsync();
        var service = CreateService(fixture.Db, fixture.TenantId);

        var create = await service.CreateAsync(
            new CreatePosCartRequest { WarehouseId = fixture.WarehouseId },
            fixture.CashierId,
            "cart-create-1");
        Assert.Equal(StatusCodes.Status201Created, create.StatusCode);
        Assert.NotNull(create.Value);

        var added = await service.AddItemAsync(
            create.Value!.CartId,
            new UpsertPosCartItemRequest
            {
                ProductId = fixture.ProductId,
                ProductVariantId = fixture.VariantId,
                UnitId = fixture.UnitId,
                Quantity = 2m,
                DiscountAmount = 1m,
                ExpectedVersion = create.Value.Version
            },
            fixture.CashierId,
            "cart-add-1");

        Assert.Equal(StatusCodes.Status200OK, added.StatusCode);
        var item = Assert.Single(added.Value!.Items);
        Assert.Equal(15m, item.UnitPriceSnapshot);
        Assert.Equal(30m, added.Value.Subtotal);
        Assert.Equal(1m, added.Value.DiscountAmount);
        Assert.Equal(2.90m, added.Value.TaxAmount);
        Assert.Equal(31.90m, added.Value.TotalAmount);

        var outbox = await fixture.Db.PosCartOutboxMessages
            .Where(message => message.CartId == create.Value.CartId)
            .OrderBy(message => message.OccurredAtUtc)
            .ToListAsync();
        Assert.Equal(2, outbox.Count);
        Assert.Contains(outbox, message => message.EventType == "PosCartCreated");
        Assert.Contains(outbox, message => message.EventType == "PosCartItemAdded");
    }

    [Fact]
    public async Task AddItem_ReplaysSameIdempotencyKeyAndRejectsDifferentPayload()
    {
        var fixture = await CreateFixtureAsync();
        var service = CreateService(fixture.Db, fixture.TenantId);
        var cart = (await service.CreateAsync(
            new CreatePosCartRequest { WarehouseId = fixture.WarehouseId },
            fixture.CashierId,
            "cart-create-idem")).Value!;

        var request = new UpsertPosCartItemRequest
        {
            ProductId = fixture.ProductId,
            ProductVariantId = fixture.VariantId,
            UnitId = fixture.UnitId,
            Quantity = 1m,
            ExpectedVersion = cart.Version
        };

        var first = await service.AddItemAsync(cart.CartId, request, fixture.CashierId, "cart-add-idem");
        var replay = await service.AddItemAsync(cart.CartId, request, fixture.CashierId, "cart-add-idem");
        var conflict = await service.AddItemAsync(
            cart.CartId,
            request with { Quantity = 2m },
            fixture.CashierId,
            "cart-add-idem");

        Assert.Equal(StatusCodes.Status200OK, first.StatusCode);
        Assert.Equal(first.Value!.Version, replay.Value!.Version);
        Assert.Equal(StatusCodes.Status409Conflict, conflict.StatusCode);
        Assert.Single(fixture.Db.PosCartItems);
    }

    [Fact]
    public async Task Lifecycle_HoldResumeAbandonRecover_RequiresExpectedVersion()
    {
        var fixture = await CreateFixtureAsync();
        var service = CreateService(fixture.Db, fixture.TenantId);
        var cart = (await service.CreateAsync(
            new CreatePosCartRequest { WarehouseId = fixture.WarehouseId },
            fixture.CashierId,
            "cart-create-lifecycle")).Value!;

        var stale = await service.TransitionAsync(
            cart.CartId,
            "Hold",
            new PosCartLifecycleRequest { ExpectedVersion = cart.Version + 1 },
            fixture.CashierId,
            "cart-hold-stale");
        Assert.Equal(StatusCodes.Status409Conflict, stale.StatusCode);

        var held = await service.TransitionAsync(
            cart.CartId,
            "Hold",
            new PosCartLifecycleRequest { ExpectedVersion = cart.Version },
            fixture.CashierId,
            "cart-hold");
        Assert.Equal(PosCartStatus.Held, held.Value!.Status);

        var resume = await service.TransitionAsync(
            cart.CartId,
            "Resume",
            new PosCartLifecycleRequest { ExpectedVersion = held.Value.Version },
            fixture.CashierId,
            "cart-resume");
        Assert.Equal(PosCartStatus.Active, resume.Value!.Status);

        var abandoned = await service.TransitionAsync(
            cart.CartId,
            "Abandon",
            new PosCartLifecycleRequest { ExpectedVersion = resume.Value.Version },
            fixture.CashierId,
            "cart-abandon");
        Assert.Equal(PosCartStatus.Abandoned, abandoned.Value!.Status);

        var recovered = await service.TransitionAsync(
            cart.CartId,
            "Recover",
            new PosCartLifecycleRequest { ExpectedVersion = abandoned.Value.Version, Reason = "terminal reboot" },
            fixture.CashierId,
            "cart-recover");
        Assert.Equal(PosCartStatus.Recovered, recovered.Value!.Status);
    }

    [Fact]
    public async Task CashierIsolation_BlocksOtherCashierAccess()
    {
        var fixture = await CreateFixtureAsync();
        var service = CreateService(fixture.Db, fixture.TenantId);
        var cart = (await service.CreateAsync(
            new CreatePosCartRequest { WarehouseId = fixture.WarehouseId },
            fixture.CashierId,
            "cart-create-isolation")).Value!;

        var otherCashierId = Guid.NewGuid();
        fixture.Db.Users.Add(new User
        {
            Id = otherCashierId,
            TenantId = fixture.TenantId,
            Username = "cashier2",
            Email = "cashier2@example.test",
            Role = UserRole.Cashier,
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        });
        await fixture.Db.SaveChangesAsync();

        var read = await service.GetAsync(cart.CartId, otherCashierId);
        var add = await service.AddItemAsync(
            cart.CartId,
            new UpsertPosCartItemRequest
            {
                ProductId = fixture.ProductId,
                ProductVariantId = fixture.VariantId,
                UnitId = fixture.UnitId,
                Quantity = 1m,
                ExpectedVersion = cart.Version
            },
            otherCashierId,
            "cart-add-cross-cashier");

        Assert.Equal(StatusCodes.Status404NotFound, read.StatusCode);
        Assert.Equal(StatusCodes.Status404NotFound, add.StatusCode);
    }

    [Fact]
    public async Task GetOpen_ReturnsOnlyTheCurrentCashiersTenantScopedCart()
    {
        var fixture = await CreateFixtureAsync();
        var service = CreateService(fixture.Db, fixture.TenantId);
        var created = (await service.CreateAsync(
            new CreatePosCartRequest { WarehouseId = fixture.WarehouseId },
            fixture.CashierId,
            "cart-create-open-read")).Value!;

        var currentCashier = await service.GetOpenAsync(fixture.CashierId);
        var otherCashier = await service.GetOpenAsync(Guid.NewGuid());

        Assert.Equal(StatusCodes.Status200OK, currentCashier.StatusCode);
        Assert.Equal(created.CartId, currentCashier.Value!.CartId);
        Assert.Equal(StatusCodes.Status404NotFound, otherCashier.StatusCode);
    }

    [Fact]
    public async Task GetOpenEndpoint_ReturnsOkNullWhenCurrentCashierHasNoOpenCart()
    {
        var fixture = await CreateFixtureAsync();
        var cashierId = fixture.CashierId;
        var runtime = new Mock<IPosCartRuntimeService>();
        runtime
            .Setup(service => service.GetOpenAsync(cashierId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(PosCartServiceResult<PosCartResponse>.NotFound("Open POS cart not found."));

        var controller = new PosCartsController(
            runtime.Object,
            fixture.Db,
            new TestTenantContext(fixture.TenantId))
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                        [new Claim(ClaimTypes.NameIdentifier, cashierId.ToString())],
                        "test"))
                }
            }
        };

        var result = await controller.GetOpen(CancellationToken.None);

        var json = Assert.IsType<JsonResult>(result.Result);
        Assert.Equal(StatusCodes.Status200OK, json.StatusCode);
        Assert.Null(json.Value);
        runtime.Verify(
            service => service.GetOpenAsync(cashierId, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task WarehouseOptions_ReturnOnlyActiveWarehousesFromCurrentTenant()
    {
        var fixture = await CreateFixtureAsync();
        fixture.Db.Warehouses.AddRange(
            new Warehouse
            {
                Id = Guid.NewGuid(),
                TenantId = fixture.TenantId,
                Name = "Inactive",
                IsActive = false,
                CreatedAt = DateTime.UtcNow,
            },
            new Warehouse
            {
                Id = Guid.NewGuid(),
                TenantId = Guid.NewGuid(),
                Name = "Foreign",
                IsActive = true,
                CreatedAt = DateTime.UtcNow,
            });
        await fixture.Db.SaveChangesAsync();

        var controller = new PosCartsController(
            Mock.Of<IPosCartRuntimeService>(),
            fixture.Db,
            new TestTenantContext(fixture.TenantId));

        var result = await controller.GetWarehouseOptions(CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var warehouses = Assert.IsAssignableFrom<IReadOnlyList<PosWarehouseOptionResponse>>(ok.Value);
        var warehouse = Assert.Single(warehouses);
        Assert.Equal(fixture.WarehouseId, warehouse.Id);
        Assert.Equal("Main", warehouse.Name);
    }

    [Fact]
    public async Task Create_AutoExpiresStaleOpenCartBeforeCreatingReplacement()
    {
        var fixture = await CreateFixtureAsync();
        var service = CreateService(fixture.Db, fixture.TenantId);
        var stale = (await service.CreateAsync(
            new CreatePosCartRequest { WarehouseId = fixture.WarehouseId },
            fixture.CashierId,
            "cart-create-stale")).Value!;

        var staleEntity = await fixture.Db.PosCarts.SingleAsync(cart => cart.Id == stale.CartId);
        staleEntity.ExpiresAtUtc = DateTime.UtcNow.AddMinutes(-1);
        await fixture.Db.SaveChangesAsync();

        var replacement = await service.CreateAsync(
            new CreatePosCartRequest { WarehouseId = fixture.WarehouseId },
            fixture.CashierId,
            "cart-create-replacement");

        Assert.Equal(StatusCodes.Status201Created, replacement.StatusCode);
        Assert.NotEqual(stale.CartId, replacement.Value!.CartId);
        Assert.Equal(PosCartStatus.Expired, staleEntity.Status);
        Assert.NotNull(staleEntity.ExpiredAtUtc);
    }

    [Fact]
    public async Task CheckoutPreparation_UsesAuthoritativeCartLinesAndCompletionMarksCartComplete()
    {
        var fixture = await CreateFixtureAsync();
        var service = CreateService(fixture.Db, fixture.TenantId);
        var cart = (await service.CreateAsync(
            new CreatePosCartRequest { WarehouseId = fixture.WarehouseId },
            fixture.CashierId,
            "cart-create-checkout")).Value!;
        cart = (await service.AddItemAsync(
            cart.CartId,
            new UpsertPosCartItemRequest
            {
                ProductId = fixture.ProductId,
                ProductVariantId = fixture.VariantId,
                UnitId = fixture.UnitId,
                Quantity = 1m,
                ExpectedVersion = cart.Version
            },
            fixture.CashierId,
            "cart-add-checkout")).Value!;

        var checkout = await service.PrepareCheckoutAsync(
            cart.CartId,
            new CreateSaleRequest
            {
                WarehouseId = fixture.WarehouseId,
                Items = new List<SaleItemRequest>(),
                PaymentMethod = PaymentMethod.Cash,
                PaidAmount = cart.TotalAmount
            },
            fixture.CashierId);

        Assert.True(checkout.Succeeded);
        var saleLine = Assert.Single(checkout.Request.Items);
        Assert.Equal(fixture.ProductId, saleLine.ProductId);
        Assert.Equal(fixture.VariantId, saleLine.ProductVariantId);
        Assert.Equal(15m, saleLine.UnitPrice);

        var saleId = Guid.NewGuid();
        await service.CompleteFromSaleAsync(cart.CartId, saleId, fixture.CashierId);
        await fixture.Db.SaveChangesAsync();

        var completed = await fixture.Db.PosCarts.SingleAsync(posCart => posCart.Id == cart.CartId);
        Assert.Equal(PosCartStatus.Completed, completed.Status);
        Assert.Equal(saleId, completed.CompletedSaleId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CheckoutPreparation_CompletedReplay_ReconstructsRequestWithDepletedOrMissingStock(bool removeStock)
    {
        var fixture = await CreateFixtureAsync(stockQuantity: 1m);
        await using var db = fixture.Db;
        var service = CreateService(db, fixture.TenantId);
        var cart = await CreateCheckoutCartAsync(fixture, service);
        var request = new CreateSaleRequest
        {
            WarehouseId = cart.WarehouseId,
            CustomerId = cart.CustomerId,
            Items = new List<SaleItemRequest>
            {
                new() { ProductId = Guid.NewGuid(), Quantity = 99m, UnitPrice = 0.01m }
            },
            PaymentMethod = PaymentMethod.Cash,
            PaidAmount = cart.TotalAmount,
            CashAmount = cart.TotalAmount,
            CardAmount = 0m,
            SalesRepresentativeId = Guid.NewGuid(),
            Notes = "Original payment attempt"
        };
        var first = await service.PrepareCheckoutAsync(cart.CartId, request, fixture.CashierId);
        Assert.True(first.Succeeded);

        var saleId = Guid.NewGuid();
        await service.CompleteFromSaleAsync(cart.CartId, saleId, fixture.CashierId);
        var completedCart = await db.PosCarts.SingleAsync();
        completedCart.ExpiresAtUtc = DateTime.UtcNow.AddMinutes(-1);
        var stock = await db.ProductVariantStocks.SingleAsync();
        if (removeStock)
        {
            db.ProductVariantStocks.Remove(stock);
        }
        else
        {
            stock.Quantity = 0m;
        }
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var replay = await service.PrepareCheckoutAsync(cart.CartId, request, fixture.CashierId);

        Assert.True(replay.Succeeded);
        Assert.Equal(cart.CustomerId, replay.Request.CustomerId);
        Assert.Equal(cart.WarehouseId, replay.Request.WarehouseId);
        Assert.Equal(first.Request.PaymentMethod, replay.Request.PaymentMethod);
        Assert.Equal(first.Request.PaidAmount, replay.Request.PaidAmount);
        Assert.Equal(first.Request.CashAmount, replay.Request.CashAmount);
        Assert.Equal(first.Request.CardAmount, replay.Request.CardAmount);
        Assert.Equal(first.Request.SalesRepresentativeId, replay.Request.SalesRepresentativeId);
        Assert.Equal(first.Request.Notes, replay.Request.Notes);
        var line = Assert.Single(replay.Request.Items);
        Assert.Equal(Assert.Single(first.Request.Items), line);
        Assert.Equal(new SaleItemRequest
        {
            ProductId = fixture.ProductId,
            ProductVariantId = fixture.VariantId,
            UnitId = fixture.UnitId,
            Quantity = 1m,
            UnitPrice = 15m,
            DiscountAmount = 1m,
            TaxRate = 10m
        }, line);
        Assert.Empty(db.ChangeTracker.Entries<ProductVariantStock>());
        Assert.Equal(saleId, (await db.PosCarts.SingleAsync()).CompletedSaleId);
        Assert.Equal(PosCartStatus.Completed, (await db.PosCarts.SingleAsync()).Status);
        var remainingStock = await db.ProductVariantStocks.AsNoTracking().SingleOrDefaultAsync();
        if (removeStock)
        {
            Assert.Null(remainingStock);
        }
        else
        {
            Assert.NotNull(remainingStock);
            Assert.Equal(0m, remainingStock!.Quantity);
        }
    }

    [Theory]
    [InlineData(PosCartStatus.Active, false)]
    [InlineData(PosCartStatus.Active, true)]
    [InlineData(PosCartStatus.Recovered, false)]
    [InlineData(PosCartStatus.Recovered, true)]
    public async Task CheckoutPreparation_ActiveOrRecoveredSale_RejectsDepletedOrMissingStock(
        PosCartStatus status, bool removeStock)
    {
        var fixture = await CreateFixtureAsync(stockQuantity: 1m);
        await using var db = fixture.Db;
        var service = CreateService(db, fixture.TenantId);
        var cart = await CreateCheckoutCartAsync(fixture, service);
        var cartEntity = await db.PosCarts.SingleAsync();
        cartEntity.Status = status;
        // Sale authority alone must not exempt an active/recovered cart from stock validation.
        cartEntity.CompletedSaleId = Guid.NewGuid();
        var stock = await db.ProductVariantStocks.SingleAsync();
        if (removeStock)
        {
            db.ProductVariantStocks.Remove(stock);
        }
        else
        {
            stock.Quantity = 0m;
        }
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var result = await service.PrepareCheckoutAsync(cart.CartId, new CreateSaleRequest
        {
            WarehouseId = cart.WarehouseId,
            CustomerId = cart.CustomerId
        }, fixture.CashierId);

        Assert.False(result.Succeeded);
        Assert.Equal(StatusCodes.Status409Conflict, result.StatusCode);
        Assert.Equal("POS cart inventory is no longer available.", result.Message);
        Assert.Empty(db.ChangeTracker.Entries<ProductVariantStock>());
    }

    [Theory]
    [InlineData("tenant", StatusCodes.Status404NotFound, "POS cart not found.")]
    [InlineData("cashier", StatusCodes.Status404NotFound, "POS cart not found.")]
    [InlineData("customer", StatusCodes.Status409Conflict, "POS checkout customer does not match cart customer.")]
    [InlineData("warehouse", StatusCodes.Status409Conflict, "POS checkout warehouse does not match cart warehouse.")]
    public async Task CheckoutPreparation_CompletedReplay_RejectsMismatchedAuthority(
        string mismatch, int expectedStatus, string expectedMessage)
    {
        var fixture = await CreateFixtureAsync(stockQuantity: 1m);
        await using var db = fixture.Db;
        var service = CreateService(db, fixture.TenantId);
        var cart = await CreateCheckoutCartAsync(fixture, service);
        await service.CompleteFromSaleAsync(cart.CartId, Guid.NewGuid(), fixture.CashierId);
        (await db.ProductVariantStocks.SingleAsync()).Quantity = 0m;
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var request = new CreateSaleRequest
        {
            WarehouseId = mismatch == "warehouse" ? Guid.NewGuid() : cart.WarehouseId,
            CustomerId = mismatch == "customer" ? Guid.NewGuid() : cart.CustomerId
        };
        if (mismatch == "tenant")
        {
            service = CreateService(db, Guid.NewGuid());
        }

        var result = await service.PrepareCheckoutAsync(cart.CartId, request,
            mismatch == "cashier" ? Guid.NewGuid() : fixture.CashierId);

        Assert.False(result.Succeeded);
        Assert.Equal(expectedStatus, result.StatusCode);
        Assert.Equal(expectedMessage, result.Message);
    }

    [Theory]
    [InlineData(false, false, StatusCodes.Status409Conflict, "POS cart completion is missing sale authority.")]
    [InlineData(true, true, StatusCodes.Status400BadRequest, "POS cart is empty.")]
    public async Task CheckoutPreparation_CompletedReplay_RequiresSaleAuthorityAndNonemptyCart(
        bool hasSaleAuthority, bool emptyCart, int expectedStatus, string expectedMessage)
    {
        var fixture = await CreateFixtureAsync();
        await using var db = fixture.Db;
        var service = CreateService(db, fixture.TenantId);
        var cart = await CreateCheckoutCartAsync(fixture, service);
        var cartEntity = await db.PosCarts.SingleAsync();
        cartEntity.Status = PosCartStatus.Completed;
        cartEntity.CompletedSaleId = hasSaleAuthority ? Guid.NewGuid() : null;
        if (emptyCart)
        {
            foreach (var item in cartEntity.Items)
            {
                item.IsDeleted = true;
            }
        }
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var result = await service.PrepareCheckoutAsync(cart.CartId, new CreateSaleRequest
        {
            WarehouseId = cart.WarehouseId,
            CustomerId = cart.CustomerId
        }, fixture.CashierId);

        Assert.False(result.Succeeded);
        Assert.Equal(expectedStatus, result.StatusCode);
        Assert.Equal(expectedMessage, result.Message);
    }

    [Fact]
    public async Task CheckoutPreparation_AdvisoryInventoryRead_DoesNotTrackStockFromClearTracker()
    {
        var fixture = await CreateFixtureAsync(stockQuantity: 1m);
        await using var db = fixture.Db;
        var service = CreateService(db, fixture.TenantId);
        var cart = await CreateCheckoutCartAsync(fixture, service);
        db.ChangeTracker.Clear();
        Assert.Empty(db.ChangeTracker.Entries());

        var result = await service.PrepareCheckoutAsync(cart.CartId, new CreateSaleRequest
        {
            WarehouseId = cart.WarehouseId,
            CustomerId = cart.CustomerId
        }, fixture.CashierId);

        Assert.True(result.Succeeded);
        Assert.Single(db.ChangeTracker.Entries<PosCart>());
        Assert.Empty(db.ChangeTracker.Entries<ProductVariantStock>());
    }

    [Fact]
    public async Task AddItem_FailsClosedWhenInventoryIsInsufficient()
    {
        var fixture = await CreateFixtureAsync(stockQuantity: 1m);
        var service = CreateService(fixture.Db, fixture.TenantId);
        var cart = (await service.CreateAsync(
            new CreatePosCartRequest { WarehouseId = fixture.WarehouseId },
            fixture.CashierId,
            "cart-create-stock")).Value!;

        var result = await service.AddItemAsync(
            cart.CartId,
            new UpsertPosCartItemRequest
            {
                ProductId = fixture.ProductId,
                ProductVariantId = fixture.VariantId,
                UnitId = fixture.UnitId,
                Quantity = 2m,
                ExpectedVersion = cart.Version
            },
            fixture.CashierId,
            "cart-add-stock");

        Assert.Equal(StatusCodes.Status400BadRequest, result.StatusCode);
        Assert.Empty(fixture.Db.PosCartItems);
    }

    [Fact]
    public async Task AddVariantItem_UsesVariantPriceAndImplicitBaseQuantityWhenProductHasNoUnits()
    {
        var fixture = await CreateFixtureAsync();
        var product = await fixture.Db.Products.SingleAsync(item => item.Id == fixture.ProductId);
        product.HasVariants = true;
        fixture.Db.ProductUnits.RemoveRange(fixture.Db.ProductUnits.Where(unit => unit.ProductId == fixture.ProductId));
        await fixture.Db.SaveChangesAsync();

        var service = CreateService(fixture.Db, fixture.TenantId);
        var cart = (await service.CreateAsync(
            new CreatePosCartRequest { WarehouseId = fixture.WarehouseId },
            fixture.CashierId,
            "cart-create-variant-no-unit")).Value!;

        var result = await service.AddItemAsync(
            cart.CartId,
            new UpsertPosCartItemRequest
            {
                ProductId = fixture.ProductId,
                ProductVariantId = fixture.VariantId,
                UnitId = null,
                Quantity = 2m,
                ExpectedVersion = cart.Version
            },
            fixture.CashierId,
            "cart-add-variant-no-unit");

        Assert.Equal(StatusCodes.Status200OK, result.StatusCode);
        var line = Assert.Single(result.Value!.Items);
        Assert.Null(line.UnitId);
        Assert.Equal(1m, line.ConversionFactor);
        Assert.Equal(2m, line.BaseQuantity);
        Assert.Equal(12m, line.UnitPriceSnapshot);
    }

    private static PosCartRuntimeService CreateService(ApplicationDbContext db, Guid tenantId) =>
        new(db, new TestTenantContext(tenantId), NullLogger<PosCartRuntimeService>.Instance);

    private static async Task<PosCartResponse> CreateCheckoutCartAsync(Fixture fixture, PosCartRuntimeService service)
    {
        var created = await service.CreateAsync(
            new CreatePosCartRequest { WarehouseId = fixture.WarehouseId },
            fixture.CashierId,
            "cart-create-checkout-fixture");
        Assert.Equal(StatusCodes.Status201Created, created.StatusCode);
        var cart = created.Value!;
        var added = await service.AddItemAsync(cart.CartId, new UpsertPosCartItemRequest
        {
            ProductId = fixture.ProductId,
            ProductVariantId = fixture.VariantId,
            UnitId = fixture.UnitId,
            Quantity = 1m,
            DiscountAmount = 1m,
            ExpectedVersion = cart.Version
        }, fixture.CashierId, "cart-add-checkout-fixture");
        Assert.Equal(StatusCodes.Status200OK, added.StatusCode);
        return added.Value!;
    }

    private static async Task<Fixture> CreateFixtureAsync(decimal stockQuantity = 10m)
    {
        var tenantId = Guid.NewGuid();
        var cashierId = Guid.NewGuid();
        var warehouseId = Guid.NewGuid();
        var categoryId = Guid.NewGuid();
        var productId = Guid.NewGuid();
        var variantId = Guid.NewGuid();
        var unitId = Guid.NewGuid();

        var db = new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
                .Options,
            new TestTenantContext(tenantId));

        db.Users.Add(new User
        {
            Id = cashierId,
            TenantId = tenantId,
            Username = "cashier",
            Email = "cashier@example.test",
            Role = UserRole.Cashier,
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        });
        db.Warehouses.Add(new Warehouse
        {
            Id = warehouseId,
            TenantId = tenantId,
            Name = "Main",
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        });
        db.Categories.Add(new Category
        {
            Id = categoryId,
            TenantId = tenantId,
            Name = "Category",
            CreatedAt = DateTime.UtcNow
        });
        db.Units.Add(new PointOfSale.Core.Entities.Unit
        {
            Id = unitId,
            TenantId = tenantId,
            Name = "Piece",
            Symbol = "pc",
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        });
        db.Products.Add(new Product
        {
            Id = productId,
            TenantId = tenantId,
            CategoryId = categoryId,
            Name = "Product",
            SalePrice = 12m,
            TaxRate = 10m,
            IsActive = true,
            TrackStock = true,
            CreatedAt = DateTime.UtcNow
        });
        db.ProductVariants.Add(new ProductVariant
        {
            Id = variantId,
            TenantId = tenantId,
            ProductId = productId,
            SKU = "SKU-1",
            Price = 12m,
            CostPrice = 5m,
            IsDefault = true,
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        });
        db.ProductUnits.Add(new ProductUnit
        {
            Id = Guid.NewGuid(),
            ProductId = productId,
            UnitId = unitId,
            ConversionFactor = 1m,
            SellingPrice = 15m,
            IsBaseUnit = true,
            IsDefault = true,
            CreatedAt = DateTime.UtcNow
        });
        db.ProductVariantStocks.Add(new ProductVariantStock
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            WarehouseId = warehouseId,
            VariantId = variantId,
            Quantity = stockQuantity,
            ReservedQuantity = 0m,
            CreatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        return new Fixture(db, tenantId, cashierId, warehouseId, productId, variantId, unitId);
    }

    private sealed record Fixture(
        ApplicationDbContext Db,
        Guid TenantId,
        Guid CashierId,
        Guid WarehouseId,
        Guid ProductId,
        Guid VariantId,
        Guid UnitId);
}
