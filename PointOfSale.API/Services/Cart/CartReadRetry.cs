namespace PointOfSale.API.Services.Cart;

/// <summary>
/// Checkout pricing, shipping preview and session autosave may read the same cart
/// concurrently. Wait briefly for its application mutation lock, never bypass it.
/// This policy is only for cart reads, not commands, checkout or provider calls.
/// </summary>
internal static class CartReadRetry
{
    internal static async Task<T> ExecuteAsync<T>(
        Func<CancellationToken, Task<T>> read,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return await read(cancellationToken);
            }
            catch (CartConcurrencyConflictException) when (attempt < 3)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(150 * (1 << attempt)), cancellationToken);
            }
        }
    }
}
