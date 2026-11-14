using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using PointOfSale.API.Controllers;
using PointOfSale.API.DTOs.Checkout;
using PointOfSale.API.DTOs.Storefront;
using PointOfSale.API.Services.Cart;
using PointOfSale.API.Services.Checkout;

namespace PointOfSale.Tests.Checkout;

public sealed class CheckoutCartReadContentionTests
{
    [Fact]
    public async Task CartRead_WaitsForContention_AndReturnsOnlyTheSuccessfulRead()
    {
        var attempts = 0;
        var value = new object();
        var actual = await CartReadRetry.ExecuteAsync<object>(_ =>
            ++attempts < 3 ? Task.FromException<object>(new CartConcurrencyConflictException()) : Task.FromResult(value), default);
        Assert.Same(value, actual);
        Assert.Equal(3, attempts);
    }

    [Fact]
    public async Task CartRead_PersistentContention_HasAFiniteBudget()
    {
        var attempts = 0;
        await Assert.ThrowsAsync<CartConcurrencyConflictException>(() => CartReadRetry.ExecuteAsync<object>(_ =>
        {
            attempts++;
            throw new CartConcurrencyConflictException();
        }, default));
        Assert.Equal(4, attempts);
    }

    [Fact]
    public async Task CartRead_DoesNotRetryInfrastructureOrBusinessFailures()
    {
        var attempts = 0;
        var failure = new InvalidOperationException("Unrelated failure");
        var actual = await Assert.ThrowsAsync<InvalidOperationException>(() => CartReadRetry.ExecuteAsync<object>(_ =>
        {
            attempts++;
            throw failure;
        }, default));
        Assert.Same(failure, actual);
        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task CartRead_CancellationDuringWait_DoesNotReadAgain()
    {
        using var cancellation = new CancellationTokenSource();
        var attempts = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CartReadRetry.ExecuteAsync<object>(_ =>
        {
            attempts++;
            cancellation.Cancel();
            throw new CartConcurrencyConflictException();
        }, cancellation.Token));
        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task Snapshot_DoesNotRelabelCartContentionAsAnInvalidCart()
    {
        // Isolate the snapshot boundary before it touches SQL or pricing. No
        // application host, database, transport or provider is started.
        var carts = new Mock<ICartService>();
        carts.Setup(x => x.GetCartAsync(It.IsAny<Guid>(), It.IsAny<PointOfSale.API.DTOs.Cart.CartOwner>(),
            It.IsAny<string>(), It.IsAny<CancellationToken>())).ThrowsAsync(new CartConcurrencyConflictException());
        var service = (CheckoutService)RuntimeHelpers.GetUninitializedObject(typeof(CheckoutService));
        typeof(CheckoutService).GetField("_cartService", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(service, carts.Object);
        typeof(CheckoutService).GetField("_logger", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(service, NullLogger<CheckoutService>.Instance);
        var method = typeof(CheckoutService).GetMethod("GetCartSnapshotAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var task = (Task<CartSnapshot>)method.Invoke(service, [Guid.NewGuid(), "synthetic-session", "session", Guid.NewGuid(), "test", CancellationToken.None])!;
        var error = await Assert.ThrowsAsync<CheckoutCartReadConflictException>(() => task);
        Assert.Equal("CART_CONCURRENCY_CONFLICT", error.ErrorCode);
    }

    [Fact]
    public async Task PreviewPricing_PersistentCartContention_Returns409Not400()
    {
        var authentication = new Mock<IAuthenticationService>();
        authentication.Setup(x => x.AuthenticateAsync(It.IsAny<HttpContext>(), It.IsAny<string>())).ReturnsAsync(AuthenticateResult.NoResult());
        using var services = new ServiceCollection().AddSingleton(authentication.Object).BuildServiceProvider();
        var http = new DefaultHttpContext { RequestServices = services };
        http.Request.Headers["X-Tenant-Id"] = Guid.NewGuid().ToString();
        http.Request.Headers["X-Session-Id"] = "synthetic-session";
        var checkout = new Mock<ICheckoutService>();
        checkout.Setup(x => x.PreviewPricingAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<CheckoutPricingPreviewRequestDto>(), It.IsAny<string>(),
            It.IsAny<CancellationToken>())).ThrowsAsync(new CheckoutCartReadConflictException());
        var controller = new CheckoutController(checkout.Object, Mock.Of<ICheckoutAttachmentService>(), NullLogger<CheckoutController>.Instance)
            { ControllerContext = new ControllerContext { HttpContext = http } };
        var result = Assert.IsType<ConflictObjectResult>(await controller.PreviewPricing(new() { CartId = Guid.NewGuid(), ShippingCountry = "EG" }, default));
        Assert.Equal(409, result.StatusCode);
        Assert.Equal("CART_CONCURRENCY_CONFLICT", Assert.IsType<ApiErrorResponse>(result.Value).Code);
    }
}
