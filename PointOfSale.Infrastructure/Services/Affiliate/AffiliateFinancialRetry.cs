using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace PointOfSale.Infrastructure.Services.Affiliate;

/// <summary>Only PostgreSQL's transaction-aborting SSI/deadlock errors are replayable.
/// Connection loss, timeouts, cancellation and unique violations have different semantics.
/// The delegate MUST dispose the failed transaction and use fresh authoritative state.</summary>
public static class AffiliateFinancialRetry
{
    public const int MaximumAttempts = 3;

    public static bool IsRolledBackTransaction(Exception exception)
    {
        // Npgsql's non-retrying EF strategy adds InvalidOperationException around
        // DbUpdateException/PostgresException. Do not classify by message/IsTransient.
        for (Exception? current = exception; current != null; current = current.InnerException)
        {
            if (current is PostgresException postgres)
                return postgres.SqlState is PostgresErrorCodes.SerializationFailure or PostgresErrorCodes.DeadlockDetected;
            if (current is not (InvalidOperationException or DbUpdateException))
                return false;
        }
        return false;
    }

    public static async Task<T> ExecuteAsync<T>(Func<int, Task<T>> attempt,
        Action<Exception, int>? onRetry = null, CancellationToken ct = default)
    {
        for (var number = 1; ; number++)
        {
            ct.ThrowIfCancellationRequested();
            try { return await attempt(number); }
            catch (Exception error) when (IsRolledBackTransaction(error))
            {
                if (number == MaximumAttempts)
                    throw new InvalidOperationException(
                        "AFFILIATE_CONCURRENCY_CONFLICT: The financial transaction was rolled back after bounded retries. Refresh and retry.", error);
                onRetry?.Invoke(error, number);
                await Task.Delay(TimeSpan.FromMilliseconds(50 * number), ct);
            }
        }
    }
}
