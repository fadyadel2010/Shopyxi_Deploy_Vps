using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;
using PointOfSale.Core.DTOs.Marketplace;
using PointOfSale.Infrastructure.Data;

namespace PointOfSale.Infrastructure.Marketplace.Generation;

public interface IMarketplaceGenerationReads
{
    Task<MarketplaceGenerationReadScope> BeginAsync(Guid tenant, string queryHash, string? continuation,
        bool keepPin, CancellationToken ct);
}

public interface IMarketplaceGenerationReaderIdentity
{
    Guid RequireAuthenticatedReader();
}

/// <summary>A single target and deadline for every provider and SQL operation in a logical read.</summary>
public abstract class MarketplaceGenerationReadScope : IAsyncDisposable
{
    public abstract string PhysicalName { get; }
    public abstract string? Continuation { get; }
    public abstract CancellationToken CancellationToken { get; }
    public abstract ValueTask DisposeAsync();

    public static string QueryHash(MarketplaceSearchRequest request) => Hash(JsonSerializer.Serialize(new
    {
        version = 1, request.Query, Category = request.EffectiveCategory, request.MinRating,
        request.MinCompletedRequests, request.TrustBadge, request.Sort, request.PageSize
    }));

    internal static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}

/// <summary>Compatibility while activation is prohibited. A generation cursor never falls back to legacy.</summary>
public sealed class InactiveMarketplaceGenerationReads : IMarketplaceGenerationReads
{
    public static readonly InactiveMarketplaceGenerationReads Instance = new();
    public Task<MarketplaceGenerationReadScope> BeginAsync(Guid tenant, string queryHash, string? continuation,
        bool keepPin, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (continuation is not null) throw new InvalidOperationException("Generation continuation cannot use the legacy reader.");
        return Task.FromResult<MarketplaceGenerationReadScope>(new Scope(ct));
    }
    private sealed class Scope(CancellationToken ct) : MarketplaceGenerationReadScope
    {
        public override string PhysicalName => "marketplace_services";
        public override string? Continuation => null;
        public override CancellationToken CancellationToken => ct;
        public override ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

internal sealed class SqlMarketplaceGenerationReads(ApplicationDbContext context, IMarketplaceGenerationReaderIdentity identity,
    IOptions<MarketplaceGenerationOptions> options) : IMarketplaceGenerationReads
{
    public async Task<MarketplaceGenerationReadScope> BeginAsync(Guid tenant, string queryHash, string? continuation,
        bool keepPin, CancellationToken ct)
    {
        // Options validation rejects activation in APP.2. The complete SQL path can
        // be exercised explicitly in isolated tests without a hosted worker.
        if (!options.Value.GenerationReadersEnabled)
            return await InactiveMarketplaceGenerationReads.Instance.BeginAsync(tenant, queryHash, continuation, keepPin, ct);
        var reader = identity.RequireAuthenticatedReader();
        if (reader == Guid.Empty) throw new InvalidOperationException("Authenticated reader identity required.");
        var db = new NpgsqlConnection(context.Database.GetConnectionString());
        try
        {
            await db.OpenAsync(ct);
            GenerationReaderPin pin;
            if (continuation is null)
                pin = await MarketplaceGenerationReaderPins.AcquireAsync(db, tenant, reader, queryHash, TimeSpan.FromMinutes(15), ct);
            else
            {
                if (continuation.Length > 4096) throw new InvalidOperationException("Invalid generation continuation.");
                try { pin = JsonSerializer.Deserialize<GenerationReaderPin>(Convert.FromBase64String(continuation))
                    ?? throw new InvalidOperationException("Invalid generation continuation."); }
                catch (Exception error) when (error is FormatException or JsonException)
                { throw new InvalidOperationException("Invalid generation continuation.", error); }
            }
            var operation = await MarketplaceGenerationReaderPins.AdmitAsync(db, pin, tenant, reader, queryHash, ct);
            // Only bounded application requests use expiration. Historical unbounded
            // admissions remain durable until explicitly completed, never guessed dead.
            await using (var deadline = new NpgsqlCommand("UPDATE marketplace_generation.reader_operations SET deadline=clock_timestamp()+interval '2 minutes' WHERE operation_id=@operation", db))
            {
                deadline.Parameters.AddWithValue("operation", operation);
                await deadline.ExecuteNonQueryAsync(ct);
            }
            return new Scope(db, pin, operation, keepPin, ct);
        }
        catch { await db.DisposeAsync(); throw; }
    }

    private sealed class Scope : MarketplaceGenerationReadScope
    {
        private readonly NpgsqlConnection db;
        private readonly GenerationReaderPin pin;
        private readonly Guid operation;
        private readonly bool keepPin;
        private readonly CancellationTokenSource budget;
        private bool disposed;
        internal Scope(NpgsqlConnection db, GenerationReaderPin pin, Guid operation, bool keepPin, CancellationToken ct)
        {
            this.db = db; this.pin = pin; this.operation = operation; this.keepPin = keepPin;
            budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
            budget.CancelAfter(TimeSpan.FromSeconds(45));
        }
        public override string PhysicalName => pin.PhysicalName;
        public override string? Continuation => keepPin ? Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(pin)) : null;
        public override CancellationToken CancellationToken => budget.Token;
        public override async ValueTask DisposeAsync()
        {
            if (disposed) return;
            disposed = true;
            try
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await MarketplaceGenerationReaderPins.CompleteAsync(db, pin, operation, cleanup.Token);
                if (!keepPin) await MarketplaceGenerationReaderPins.ReleaseAsync(db, pin, cleanup.Token);
            }
            finally { budget.Dispose(); await db.DisposeAsync(); }
        }
    }
}
