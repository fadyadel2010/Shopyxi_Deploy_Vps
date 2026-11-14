using System.Text.Json;
using Npgsql;

namespace PointOfSale.Infrastructure.Marketplace.Generation;

/// <summary>
/// Internal, unregistered common delivery implementation. Durable one-shot claims
/// survive process/session/lease loss; only definite replies acknowledge delivery.
/// Refresh observations prove observed content, NOT all-writer/provider-role
/// isolation, continuous readiness, cutover authority or retirement permission.
/// Legacy writers are NOT admitted by this component and recovery stays blocked.
/// </summary>
internal static class MarketplaceGenerationDeliveryProtocol
{
    internal enum DeliveryBoundary { BeforeWrite, AfterWriteBeforeAcknowledgement, BeforeRefresh, AfterRefreshBeforeVerification, BeforeRefreshedAcknowledgement }

    internal static string SchemaSql
    {
        get
        {
            using var stream = typeof(MarketplaceGenerationDeliveryProtocol).Assembly.GetManifestResourceStream(
                "PointOfSale.Infrastructure.Marketplace.Generation.MarketplaceGenerationDelivery.sql")!;
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
    }

    // expected is an independently pinned identity, never one learned from an
    // arbitrary response and silently accepted. Pinning is immutable even on retry.
    internal static async Task BindProviderAsync(NpgsqlConnection db, GenerationLease lease,
        MarketplacePhysicalAdmission.Expected expected, MarketplaceGenerationTransport transport, CancellationToken ct = default)
    {
        await InspectAsync(transport, expected, ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        await MarketplaceGenerationRegistry.RequireLeaseAsync(db, tx, lease, true, ct);
        await using (var insert = new NpgsqlCommand("""
            INSERT INTO marketplace_generation.provider_bindings(generation_id,journal_id,cluster_uuid,physical_uuid,fingerprint)
            SELECT generation_id,journal_id,@cluster,physical_uuid,fingerprint FROM marketplace_generation.builds
            WHERE generation_id=@generation AND journal_id=@journal AND physical_name=@name AND physical_uuid=@uuid AND fingerprint=@fingerprint
            ON CONFLICT(generation_id) DO NOTHING
            """, db, tx))
        {
            ProviderParameters(insert, expected);
            await insert.ExecuteNonQueryAsync(ct);
        }
        await RequireProviderAsync(db, tx, lease, expected, ct);
        await MarketplaceGenerationRegistry.RequireLeaseAsync(db, tx, lease, false, ct);
        await tx.CommitAsync(ct);
    }

    internal static async Task DeliverAsync(NpgsqlConnection db, GenerationLease lease, ExternalWriteTicket ticket,
        MarketplacePhysicalAdmission.Expected expected, MarketplaceGenerationTransport transport, CancellationToken ct = default,
        Action<DeliveryBoundary>? injectFailure = null)
    {
        // Claim before any transport. Competing callers/retries cannot issue a
        // second request, including when a previous process died before sending.
        var payload = await ClaimDispatchAsync(db, lease, ticket, expected, ct);
        var mutationStarted = false;
        try
        {
            var update = MarketplaceVersionedDocument.BuildUpdate(lease.JournalId, ticket, payload);
            await InspectAsync(transport, expected, ct);
            await using var dispatchTransaction = await db.BeginTransactionAsync(ct);
            await MarketplaceGenerationRegistry.RequireLeaseAsync(db, dispatchTransaction, lease, true, ct);
            await RequireProviderAsync(db, dispatchTransaction, lease, expected, ct);
            await RequireOpenBarrierAsync(db, dispatchTransaction, ticket.GenerationId, ct);
            ct.ThrowIfCancellationRequested();
            // From here EVERY exception, cancellation, HTTP error, malformed reply
            // or failed SQL acknowledgement leaves the durable intent outstanding.
            // A GET proving the right body cannot cancel a still delayed request.
            injectFailure?.Invoke(DeliveryBoundary.BeforeWrite);
            await MarketplaceGenerationRegistry.RequireLeaseAsync(db, dispatchTransaction, lease, false, ct);
            mutationStarted = true;
            var response = await transport.SendAsync(HttpMethod.Post,
                "/" + expected.PhysicalName + "/_update/" + ticket.ServiceId.ToString("D") + "?retry_on_conflict=0", update, ct);
            MarketplaceGenerationResponse.RequireWrite(response, expected.PhysicalName, ticket.ServiceId);
            injectFailure?.Invoke(DeliveryBoundary.AfterWriteBeforeAcknowledgement);
            await InspectAsync(transport, expected, ct);
            // Late definite acknowledgement is allowed after lease loss; it does
            // not confer a new writer lease or imply refreshed search visibility.
            await MarketplaceExternalWriteBarrier.RecordDefiniteOutcomeAsync(db, ticket, ExternalDefiniteOutcome.Acknowledged, ct);
            await MarketplaceGenerationAttemptOutcomes.CompleteAsync(db, ticket.OperationId, true, ct);
            await dispatchTransaction.CommitAsync(ct);
        }
        catch (Exception error) when (!mutationStarted)
        {
            // This exact winning dispatch has not invoked a mutation. Never apply
            // this rule to a losing claim or to an ambiguous send/response failure.
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try
            {
                await MarketplaceExternalWriteBarrier.RecordDefiniteOutcomeAsync(db, ticket, ExternalDefiniteOutcome.DefinitelyNotSent, cleanup.Token);
                await MarketplaceGenerationAttemptOutcomes.CompleteAsync(db, ticket.OperationId, false, cleanup.Token);
            }
            catch (Exception persistenceError) { throw new AggregateException("Pre-send failure; terminal SQL outcome was not persisted.", error, persistenceError); }
            throw;
        }
    }

    internal static async Task<string?> ClaimDispatchAsync(NpgsqlConnection db, GenerationLease lease, ExternalWriteTicket ticket,
        MarketplacePhysicalAdmission.Expected expected, CancellationToken ct = default)
    {
        if (ticket.Epoch != lease.Epoch || ticket.GenerationId != expected.Generation || lease.JournalId != expected.Journal ||
            ticket.PhysicalUuid != expected.PhysicalUuid || ticket.Fingerprint != expected.Fingerprint)
            throw new InvalidOperationException("Exact current lease/ticket/provider lineage required.");
        await using var tx = await db.BeginTransactionAsync(ct);
        await MarketplaceGenerationRegistry.RequireLeaseAsync(db, tx, lease, true, ct);
        await RequireProviderAsync(db, tx, lease, expected, ct);
        await RequireOpenBarrierAsync(db, tx, ticket.GenerationId, ct);
        string? payload;
        await using (var read = new NpgsqlCommand("""
            SELECT payload FROM marketplace_generation.write_intents
            WHERE operation_id=@operation AND completion_token=@token AND generation_id=@generation AND run_id=@run
              AND service_id=@service AND source_revision=@revision AND physical_uuid=@uuid AND fingerprint=@fingerprint
              AND digest=@digest AND issued_epoch=@epoch AND state='outstanding'
            FOR UPDATE
            """, db, tx))
        {
            TicketParameters(read, ticket);
            await using var rows = await read.ExecuteReaderAsync(ct);
            if (!await rows.ReadAsync(ct)) throw new InvalidOperationException("Exact outstanding intent required.");
            payload = rows.IsDBNull(0) ? null : rows.GetString(0);
        }
        // Validate sealed bytes before recording a dispatch claim. Corruption must
        // not become an external send, even if a caller supplies a forged ticket.
        var update = MarketplaceVersionedDocument.BuildUpdate(lease.JournalId, ticket, payload);
        await using (var claim = new NpgsqlCommand("""
            INSERT INTO marketplace_generation.dispatch_claims(operation_id) VALUES(@operation)
            ON CONFLICT(operation_id) DO NOTHING
            """, db, tx))
        {
            claim.Parameters.AddWithValue("operation", ticket.OperationId);
            if (await claim.ExecuteNonQueryAsync(ct) != 1) throw new InvalidOperationException("Dispatch already claimed; never retry an unknown request.");
        }
        await MarketplaceGenerationAttemptOutcomes.AdmitAsync(db, tx, ticket.OperationId, ticket.OperationId, update, lease.Epoch, ct);
        await MarketplaceGenerationRegistry.RequireLeaseAsync(db, tx, lease, false, ct);
        await tx.CommitAsync(ct);
        return payload;
    }

    // The transaction holds common admission locks throughout the read-only
    // verification interval. Session loss aborts the observation, not prior sends.
    // Other HTTP writers/owners bypassing these locks remain a certification blocker.
    internal static async Task ObserveRefreshedRunAsync(NpgsqlConnection db, GenerationLease lease, Guid run,
        MarketplacePhysicalAdmission.Expected expected, MarketplaceGenerationTransport transport, CancellationToken ct = default,
        Action<DeliveryBoundary>? injectFailure = null, bool gatewayRefresh = false)
    {
        await using var tx = await db.BeginTransactionAsync(ct);
        await MarketplaceGenerationRegistry.RequireLeaseAsync(db, tx, lease, true, ct);
        await RequireProviderAsync(db, tx, lease, expected, ct);
        long revision, count; string seal;
        await using (var read = new NpgsqlCommand("""
            SELECT e.revision,s.item_count,s.digest FROM marketplace_generation.output_runs r
            JOIN marketplace_generation.output_seals s ON s.run_id=r.run_id
            JOIN marketplace_generation.exports e ON e.export_id=r.export_id
            JOIN marketplace_generation.builds b ON b.generation_id=e.generation_id
            WHERE r.run_id=@run AND e.generation_id=@generation AND e.journal_id=@journal
              AND e.physical_uuid=@uuid AND e.fingerprint=@fingerprint AND r.composer_fingerprint=@composer
              AND b.checkpoint_revision=e.revision AND b.checkpoint_ordinal=9223372036854775807
              AND NOT EXISTS(SELECT 1 FROM marketplace_generation.write_intents i
                WHERE i.generation_id=e.generation_id AND (i.state='outstanding' OR (i.state='acknowledged' AND i.source_revision>e.revision)))
              AND NOT EXISTS(SELECT 1 FROM marketplace_generation.output_rows o WHERE o.run_id=r.run_id
                AND NOT EXISTS(SELECT 1 FROM marketplace_generation.write_intents i WHERE i.generation_id=e.generation_id
                  AND i.run_id=r.run_id AND i.service_id=o.service_id AND i.digest=o.digest AND i.source_revision=e.revision AND i.state='acknowledged'))
              AND NOT EXISTS(SELECT 1 FROM marketplace_generation.delivery_attempts d
                JOIN marketplace_generation.write_intents i ON i.operation_id=d.operation_id
                WHERE i.generation_id=e.generation_id AND NOT EXISTS(
                  SELECT 1 FROM marketplace_generation.delivery_attempt_outcomes a WHERE a.attempt_id=d.attempt_id))
            """, db, tx))
        {
            ProviderParameters(read, expected); read.Parameters.AddWithValue("run", run);
            read.Parameters.AddWithValue("composer", MarketplaceFrozenOutput.ComposerFingerprint);
            await using var rows = await read.ExecuteReaderAsync(ct);
            if (!await rows.ReadAsync(ct)) throw new InvalidOperationException("Complete sealed output with definite delivery and no unknown/newer writes required.");
            revision = rows.GetInt64(0); count = rows.GetInt64(1); seal = rows.GetString(2);
        }
        await InspectAsync(transport, expected, ct);
        injectFailure?.Invoke(DeliveryBoundary.BeforeRefresh);
        MarketplaceGenerationResponse.RequireShards(gatewayRefresh ? await transport.RefreshGatewayAsync(expected, ct) :
            await transport.SendAsync(HttpMethod.Post, "/" + expected.PhysicalName + "/_refresh", null, ct));
        injectFailure?.Invoke(DeliveryBoundary.AfterRefreshBeforeVerification);
        Guid? after = null; long verified = 0;
        while (true)
        {
            var page = new List<DeliveredGenerationRow>();
            await using (var read = new NpgsqlCommand("""
                SELECT o.service_id,o.payload,o.digest,i.issued_epoch FROM marketplace_generation.output_rows o
                JOIN marketplace_generation.write_intents i ON i.run_id=o.run_id AND i.service_id=o.service_id
                  AND i.digest=o.digest AND i.state='acknowledged'
                WHERE o.run_id=@run AND (@first OR o.service_id>@after) ORDER BY o.service_id LIMIT 128
                """, db, tx))
            {
                read.Parameters.AddWithValue("run", run); read.Parameters.AddWithValue("first", after is null);
                read.Parameters.AddWithValue("after", after ?? Guid.Empty);
                await using var rows = await read.ExecuteReaderAsync(ct);
                while (await rows.ReadAsync(ct)) page.Add(new(rows.GetGuid(0), rows.IsDBNull(1) ? null : rows.GetString(1), rows.GetString(2), rows.GetInt64(3)));
            }
            if (page.Count == 0) break;
            var body = JsonSerializer.Serialize(new { ids = page.Select(row => row.ServiceId.ToString("D")).ToArray() });
            var response = await transport.SendAsync(HttpMethod.Post, "/" + expected.PhysicalName + "/_mget?realtime=false", body, ct);
            MarketplaceGenerationResponse.RequireDocuments(response, expected, revision, page);
            verified += page.Count; after = page[^1].ServiceId;
        }
        if (verified != count) throw new InvalidDataException("Remote verification coverage differs from the immutable seal.");
        MarketplaceGenerationResponse.RequireCount(await transport.SendAsync(HttpMethod.Post, "/" + expected.PhysicalName + "/_count", "{\"query\":{\"match_all\":{}}}", ct), count);
        await InspectAsync(transport, expected, ct);
        await MarketplaceGenerationRegistry.RequireLeaseAsync(db, tx, lease, false, ct);
        injectFailure?.Invoke(DeliveryBoundary.BeforeRefreshedAcknowledgement);
        await using (var insert = new NpgsqlCommand("""
            INSERT INTO marketplace_generation.refresh_observations(run_id,generation_id,journal_id,cluster_uuid,physical_uuid,
                fingerprint,source_revision,seal_digest,item_count,observed_epoch)
            VALUES(@run,@generation,@journal,@cluster,@uuid,@fingerprint,@revision,@seal,@count,@epoch)
            ON CONFLICT(run_id) DO NOTHING
            """, db, tx))
        {
            ProviderParameters(insert, expected); insert.Parameters.AddWithValue("run", run); insert.Parameters.AddWithValue("revision", revision);
            insert.Parameters.AddWithValue("seal", seal); insert.Parameters.AddWithValue("count", count); insert.Parameters.AddWithValue("epoch", lease.Epoch);
            await insert.ExecuteNonQueryAsync(ct);
        }
        await MarketplaceGenerationRegistry.RequireLeaseAsync(db, tx, lease, false, ct);
        await tx.CommitAsync(ct);
    }

    private static async Task InspectAsync(MarketplaceGenerationTransport transport, MarketplacePhysicalAdmission.Expected expected, CancellationToken ct)
    {
        // Validate before concatenating even a malformed internal target.
        MarketplacePhysicalAdmission.Validate(expected);
        var cluster = await transport.SendAsync(HttpMethod.Get, "/", null, ct);
        MarketplacePhysicalAdmission.RequireCluster(expected, cluster);
        var physical = await transport.SendAsync(HttpMethod.Get, "/" + expected.PhysicalName, null, ct);
        _ = MarketplacePhysicalAdmission.InspectResponses(expected, cluster, physical);
    }

    private static async Task RequireProviderAsync(NpgsqlConnection db, NpgsqlTransaction tx, GenerationLease lease,
        MarketplacePhysicalAdmission.Expected expected, CancellationToken ct)
    {
        MarketplacePhysicalAdmission.Validate(expected);
        if (lease.JournalId != expected.Journal) throw new InvalidDataException("Provider journal mismatch.");
        await using var query = new NpgsqlCommand("""
            SELECT EXISTS(SELECT 1 FROM marketplace_generation.provider_bindings p
              JOIN marketplace_generation.builds b ON b.generation_id=p.generation_id
              WHERE p.generation_id=@generation AND p.journal_id=@journal AND p.cluster_uuid=@cluster
                AND p.physical_uuid=@uuid AND p.fingerprint=@fingerprint AND b.journal_id=p.journal_id
                AND b.physical_name=@name AND b.physical_uuid=p.physical_uuid AND b.fingerprint=p.fingerprint)
            """, db, tx);
        ProviderParameters(query, expected);
        if (await query.ExecuteScalarAsync(ct) is not true) throw new InvalidDataException("Immutable provider binding required.");
    }

    private static async Task RequireOpenBarrierAsync(NpgsqlConnection db, NpgsqlTransaction tx, Guid generation, CancellationToken ct)
    {
        await using var query = new NpgsqlCommand("SELECT state FROM marketplace_generation.write_barriers WHERE generation_id=@generation FOR UPDATE", db, tx);
        query.Parameters.AddWithValue("generation", generation);
        if (await query.ExecuteScalarAsync(ct) is not string state || state != "open") throw new InvalidOperationException("Generation dispatch admission closed.");
    }

    private static void ProviderParameters(NpgsqlCommand command, MarketplacePhysicalAdmission.Expected expected)
    {
        command.Parameters.AddWithValue("generation", expected.Generation); command.Parameters.AddWithValue("journal", expected.Journal);
        command.Parameters.AddWithValue("cluster", expected.ClusterUuid); command.Parameters.AddWithValue("uuid", expected.PhysicalUuid);
        command.Parameters.AddWithValue("fingerprint", expected.Fingerprint); command.Parameters.AddWithValue("name", expected.PhysicalName);
    }
    internal static void TicketParameters(NpgsqlCommand command, ExternalWriteTicket ticket)
    {
        command.Parameters.AddWithValue("operation", ticket.OperationId); command.Parameters.AddWithValue("token", ticket.CompletionToken);
        command.Parameters.AddWithValue("generation", ticket.GenerationId); command.Parameters.AddWithValue("run", ticket.RunId);
        command.Parameters.AddWithValue("service", ticket.ServiceId); command.Parameters.AddWithValue("revision", ticket.SourceRevision);
        command.Parameters.AddWithValue("uuid", ticket.PhysicalUuid); command.Parameters.AddWithValue("fingerprint", ticket.Fingerprint);
        command.Parameters.AddWithValue("digest", ticket.Digest); command.Parameters.AddWithValue("epoch", ticket.Epoch);
    }
}

internal sealed record DeliveredGenerationRow(Guid ServiceId, string? Payload, string Digest, long Epoch);
