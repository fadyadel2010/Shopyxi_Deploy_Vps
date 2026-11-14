using System.Security.Cryptography;
using System.Text;
using Npgsql;

namespace PointOfSale.Infrastructure.Marketplace.Generation;

internal static class MarketplaceGenerationAttemptOutcomes
{
    internal static async Task AdmitAsync(NpgsqlConnection db, NpgsqlTransaction tx, Guid operation, Guid attempt,
        string request, long epoch, CancellationToken ct)
    {
        await using var insert = new NpgsqlCommand("""
            INSERT INTO marketplace_generation.delivery_attempts(attempt_id,operation_id,request_hash,admitted_epoch)
            VALUES(@attempt,@operation,@hash,@epoch)
            """, db, tx);
        insert.Parameters.AddWithValue("attempt", attempt); insert.Parameters.AddWithValue("operation", operation);
        insert.Parameters.AddWithValue("hash", Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(request))));
        insert.Parameters.AddWithValue("epoch", epoch);
        await insert.ExecuteNonQueryAsync(ct);
    }

    internal static async Task CompleteAsync(NpgsqlConnection db, Guid attempt, bool acknowledged, CancellationToken ct)
    {
        await using var insert = new NpgsqlCommand("""
            INSERT INTO marketplace_generation.delivery_attempt_outcomes(attempt_id,outcome) VALUES(@attempt,@outcome)
            ON CONFLICT(attempt_id) DO NOTHING;
            SELECT outcome=@outcome FROM marketplace_generation.delivery_attempt_outcomes WHERE attempt_id=@attempt
            """, db);
        insert.Parameters.AddWithValue("attempt", attempt);
        insert.Parameters.AddWithValue("outcome", acknowledged ? "acknowledged" : "not-sent");
        if (await insert.ExecuteScalarAsync(ct) is not true)
            throw new InvalidOperationException("Attempt outcomes are immutable.");
    }
}
