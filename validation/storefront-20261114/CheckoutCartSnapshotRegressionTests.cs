using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using PointOfSale.API.DTOs.Cart;
using PointOfSale.API.DTOs.Checkout;
using PointOfSale.API.Services.Cart;
using PointOfSale.API.Services.Checkout;

namespace PointOfSale.Tests.Checkout;

public sealed class CheckoutCartSnapshotRegressionTests
{
    [Fact]
    public async Task Snapshot_ContentionIsNotAnInvalidCustomerRequest()
    {
        var carts = new Mock<ICartService>();
        carts.Setup(x => x.GetCartAsync(It.IsAny<Guid>(), It.IsAny<CartOwner>(), It.IsAny<string>(),
            It.IsAny<CancellationToken>())).ThrowsAsync(new CartConcurrencyConflictException());
        var service = (CheckoutService)RuntimeHelpers.GetUninitializedObject(typeof(CheckoutService));
        typeof(CheckoutService).GetField("_cartService", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(service, carts.Object);
        typeof(CheckoutService).GetField("_logger", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(service, NullLogger<CheckoutService>.Instance);
        var snapshot = typeof(CheckoutService).GetMethod("GetCartSnapshotAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var task = (Task<CartSnapshot>)snapshot.Invoke(service, [Guid.NewGuid(), "synthetic-session", "session", Guid.NewGuid(), "regression", CancellationToken.None])!;
        var error = await Assert.ThrowsAnyAsync<CheckoutException>(() => task);
        Assert.Equal("CART_CONCURRENCY_CONFLICT", error.ErrorCode);
    }
}
