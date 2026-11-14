using Npgsql;

namespace PointOfSale.Infrastructure.Marketplace.Generation;

/// <summary>
/// Explicit bounded orchestration, never a hosted worker. A sealed local run is
/// delivered and verified through the actual dedicated provider transport. Any
/// ambiguous attempt stays pending; no local seal or retry is a refresh receipt.
/// </summary>
internal sealed class MarketplaceGenerationRunDelivery
{
    internal sealed record RecoveryPage(Guid? After, IReadOnlyList<GenerationOperationSnapshot> Operations);

    // Explicit bounded restart scan, not a worker or automatic activation path.
    // Includes recovery-Acknowledged rows whose FINAL intent transaction failed.
    internal async Task<RecoveryPage> ReconcilePendingAsync(NpgsqlDataSource source, IGenerationAtomicWriteBoundary boundary,
        Guid? after = null, int limit = 128, CancellationToken ct = default)
    {
        if (limit is < 1 or > 128) throw new ArgumentOutOfRangeException(nameof(limit));
        var ids = new List<Guid>();
        await using (var db = await source.OpenConnectionAsync(ct))
        await using (var page = new NpgsqlCommand("""
            SELECT r.operation_id FROM marketplace_generation.recovery_operations r
            JOIN marketplace_generation.write_intents i ON i.operation_id=r.operation_id
            WHERE i.state='outstanding' AND (@first OR r.operation_id>@after)
            ORDER BY r.operation_id LIMIT @limit
            """, db))
        {
            page.Parameters.AddWithValue("first", after is null); page.Parameters.AddWithValue("after", after ?? Guid.Empty);
            page.Parameters.AddWithValue("limit", limit);
            await using var rows = await page.ExecuteReaderAsync(ct);
            while (await rows.ReadAsync(ct)) ids.Add(rows.GetGuid(0));
        }
        var recovery = new MarketplaceGenerationRecoverableDelivery(new MarketplaceGenerationDeliveryJournal(source),
            new MarketplaceGenerationOperationStore(source), boundary);
        var results = new List<GenerationOperationSnapshot>();
        foreach (var id in ids) results.Add(await recovery.ReconcileAsync(id, ct));
        return new(ids.Count == 0 ? after : ids[^1], results);
    }

    // Explicit application integration. The source owns durable recovery and
    // gateway admission; no new credentials, hosted worker or activation switch.
    internal Task DeliverRecoverablyAsync(NpgsqlDataSource source, NpgsqlConnection db, GenerationLease lease, Guid run,
        MarketplacePhysicalAdmission.Expected physical, MarketplaceGenerationTransport transport,
        IGenerationAtomicWriteBoundary boundary, CancellationToken ct = default)
    {
        // Application deployments pass MarketplaceGenerationRemoteBoundary: this
        // process's provider transport has READ/refresh credentials, never update.
        var recovery = new MarketplaceGenerationRecoverableDelivery(new MarketplaceGenerationDeliveryJournal(source),
            new MarketplaceGenerationOperationStore(source), boundary);
        return DeliverAsync(db, lease, run, physical, transport, ct, recovery);
    }

    internal async Task DeliverAsync(NpgsqlConnection db, GenerationLease lease, Guid run,
        MarketplacePhysicalAdmission.Expected physical, MarketplaceGenerationTransport transport,
        CancellationToken ct = default, MarketplaceGenerationRecoverableDelivery? recovery = null)
    {
        // The old raw one-shot path is a deterministic fixture seam only. A real
        // invocation must select durable recovery before any claims or mutations.
        if (recovery is null) transport.RequireLegacyFixture();
        await MarketplaceGenerationDeliveryProtocol.BindProviderAsync(db, lease, physical, transport, ct);
        Guid? after = null;
        while (true)
        {
            lease = await MarketplaceGenerationRegistry.RenewAsync(db, lease, TimeSpan.FromMinutes(2), ct);
            var services = new List<Guid>();
            await using (var page = new NpgsqlCommand("""
                SELECT o.service_id FROM marketplace_generation.output_rows o
                JOIN marketplace_generation.output_runs r ON r.run_id=o.run_id
                JOIN marketplace_generation.output_seals s ON s.run_id=r.run_id
                JOIN marketplace_generation.exports e ON e.export_id=r.export_id
                WHERE r.run_id=@run AND e.generation_id=@generation AND e.journal_id=@journal
                  AND (@first OR o.service_id>@after) ORDER BY o.service_id LIMIT 128
                """, db))
            {
                page.Parameters.AddWithValue("run", run); page.Parameters.AddWithValue("generation", physical.Generation);
                page.Parameters.AddWithValue("journal", lease.JournalId); page.Parameters.AddWithValue("first", after is null);
                page.Parameters.AddWithValue("after", after ?? Guid.Empty);
                await using var rows = await page.ExecuteReaderAsync(ct);
                while (await rows.ReadAsync(ct)) services.Add(rows.GetGuid(0));
            }
            if (services.Count == 0) break;
            foreach (var service in services)
            {
                lease = await MarketplaceGenerationRegistry.RenewAsync(db, lease, TimeSpan.FromMinutes(2), ct);
                var existing = await ReadTicketAsync(db, run, service, ct);
                if (existing?.State == "acknowledged" && recovery is null) continue;
                var ticket = existing?.Ticket ?? await MarketplaceExternalWriteBarrier.AdmitAsync(db, lease, run, service, Guid.NewGuid(), ct);
                if (recovery is not null)
                {
                    var result = await recovery.DeliverAsync(lease, ticket, physical, ct);
                    if (result.State != GenerationOperationState.Acknowledged)
                        throw new InvalidOperationException("Run contains non-acknowledged or superseded operations; this older seal cannot be certified.");
                }
                else if (existing?.Claimed == true || ticket.Epoch != lease.Epoch)
                    await MarketplaceGenerationRedelivery.RetryAsync(db, lease, ticket, physical, transport, Guid.NewGuid(), ct);
                else
                    await MarketplaceGenerationDeliveryProtocol.DeliverAsync(db, lease, ticket, physical, transport, ct);
            }
            after = services[^1];
        }
        lease = await MarketplaceGenerationRegistry.RenewAsync(db, lease, TimeSpan.FromMinutes(2), ct);
        await MarketplaceGenerationDeliveryProtocol.ObserveRefreshedRunAsync(db, lease, run, physical, transport, ct,
            gatewayRefresh: recovery is not null);
    }

    private sealed record Existing(ExternalWriteTicket Ticket, string State, bool Claimed);
    private static async Task<Existing?> ReadTicketAsync(NpgsqlConnection db, Guid run, Guid service, CancellationToken ct)
    {
        await using var read = new NpgsqlCommand("""
            SELECT i.operation_id,i.completion_token,i.generation_id,i.source_revision,i.physical_uuid,i.fingerprint,
                i.digest,i.issued_epoch,i.state,EXISTS(SELECT 1 FROM marketplace_generation.dispatch_claims c WHERE c.operation_id=i.operation_id)
            FROM marketplace_generation.write_intents i WHERE i.run_id=@run AND i.service_id=@service
              AND i.state IN ('outstanding','acknowledged','superseded','not-sent')
            """, db);
        read.Parameters.AddWithValue("run", run); read.Parameters.AddWithValue("service", service);
        await using var rows = await read.ExecuteReaderAsync(ct);
        if (!await rows.ReadAsync(ct)) return null;
        var result = new Existing(new(rows.GetGuid(0), rows.GetGuid(1), rows.GetGuid(2), run, service, rows.GetInt64(3),
            rows.GetString(4), rows.GetString(5), rows.GetString(6), rows.GetInt64(7)), rows.GetString(8), rows.GetBoolean(9));
        if (await rows.ReadAsync(ct)) throw new InvalidDataException("Ambiguous delivery identity for sealed output row.");
        return result;
    }
}
