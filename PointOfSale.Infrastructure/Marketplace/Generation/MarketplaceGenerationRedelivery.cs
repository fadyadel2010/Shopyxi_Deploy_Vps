using Npgsql;

namespace PointOfSale.Infrastructure.Marketplace.Generation;

/// <summary>
/// Replays exactly the original immutable CAS request, never a reconstructed live
/// document or a newly stamped source revision. This is not a timeout-to-success
/// conversion. The original intent remains outstanding until a definite provider
/// response. Remote physical/role isolation is still required before activation.
/// </summary>
internal static class MarketplaceGenerationRedelivery
{
    internal static async Task RetryAsync(NpgsqlConnection db, GenerationLease currentLease, ExternalWriteTicket original,
        MarketplacePhysicalAdmission.Expected expected, MarketplaceGenerationTransport transport, Guid attempt,
        CancellationToken ct = default,
        Action<MarketplaceGenerationDeliveryProtocol.DeliveryBoundary>? injectFailure = null)
    {
        if (attempt == Guid.Empty || currentLease.JournalId != expected.Journal || original.GenerationId != expected.Generation ||
            original.PhysicalUuid != expected.PhysicalUuid || original.Fingerprint != expected.Fingerprint || original.Epoch > currentLease.Epoch)
            throw new ArgumentException("Exact original ticket and current recovery owner required.");
        MarketplacePhysicalAdmission.Validate(expected);
        string update;
        await using (var tx = await db.BeginTransactionAsync(ct))
        {
            await MarketplaceGenerationRegistry.RequireLeaseAsync(db, tx, currentLease, true, ct);
            await using var read = new NpgsqlCommand("""
                SELECT i.payload FROM marketplace_generation.write_intents i
                JOIN marketplace_generation.provider_bindings p ON p.generation_id=i.generation_id
                JOIN marketplace_generation.write_barriers w ON w.generation_id=i.generation_id
                WHERE i.operation_id=@operation AND i.completion_token=@token AND i.generation_id=@generation
                  AND i.run_id=@run AND i.service_id=@service AND i.source_revision=@revision AND i.physical_uuid=@uuid
                  AND i.fingerprint=@fingerprint AND i.digest=@digest AND i.issued_epoch=@original_epoch AND i.state='outstanding'
                  AND p.journal_id=@journal AND p.cluster_uuid=@cluster AND p.physical_uuid=i.physical_uuid AND p.fingerprint=i.fingerprint
                  AND w.state IN ('open','draining')
                FOR UPDATE OF i,w
                """, db, tx);
            read.Parameters.AddWithValue("operation", original.OperationId); read.Parameters.AddWithValue("token", original.CompletionToken);
            read.Parameters.AddWithValue("generation", original.GenerationId); read.Parameters.AddWithValue("run", original.RunId);
            read.Parameters.AddWithValue("service", original.ServiceId); read.Parameters.AddWithValue("revision", original.SourceRevision);
            read.Parameters.AddWithValue("uuid", original.PhysicalUuid); read.Parameters.AddWithValue("fingerprint", original.Fingerprint);
            read.Parameters.AddWithValue("digest", original.Digest); read.Parameters.AddWithValue("original_epoch", original.Epoch);
            read.Parameters.AddWithValue("journal", currentLease.JournalId); read.Parameters.AddWithValue("cluster", expected.ClusterUuid);
            await using (var rows = await read.ExecuteReaderAsync(ct))
            {
                if (!await rows.ReadAsync(ct)) throw new InvalidOperationException("Exact outstanding original intent and immutable provider binding required.");
                update = MarketplaceVersionedDocument.BuildUpdate(currentLease.JournalId, original, rows.IsDBNull(0) ? null : rows.GetString(0));
            }
            await MarketplaceGenerationAttemptOutcomes.AdmitAsync(db, tx, original.OperationId, attempt, update, currentLease.Epoch, ct);
            await MarketplaceGenerationRegistry.RequireLeaseAsync(db, tx, currentLease, false, ct);
            await tx.CommitAsync(ct);
        }
        var sent = false;
        try
        {
            var cluster = await transport.SendAsync(HttpMethod.Get, "/", null, ct);
            MarketplacePhysicalAdmission.RequireCluster(expected, cluster);
            _ = MarketplacePhysicalAdmission.InspectResponses(expected, cluster,
                await transport.SendAsync(HttpMethod.Get, "/" + expected.PhysicalName, null, ct));
            await using var tx = await db.BeginTransactionAsync(ct);
            await MarketplaceGenerationRegistry.RequireLeaseAsync(db, tx, currentLease, true, ct);
            // The earlier durable claim is not permission to send after a slow
            // physical inspection, ownership loss, or a competing terminal reply.
            await using (var eligible = new NpgsqlCommand("""
                SELECT EXISTS(SELECT 1 FROM marketplace_generation.write_intents i
                  JOIN marketplace_generation.write_barriers w ON w.generation_id=i.generation_id
                  WHERE i.operation_id=@operation AND i.state='outstanding' AND w.state IN ('open','draining'))
                """, db, tx))
            {
                eligible.Parameters.AddWithValue("operation", original.OperationId);
                if (await eligible.ExecuteScalarAsync(ct) is not true) throw new InvalidOperationException("Retry admission closed.");
            }
            injectFailure?.Invoke(MarketplaceGenerationDeliveryProtocol.DeliveryBoundary.BeforeWrite);
            await MarketplaceGenerationRegistry.RequireLeaseAsync(db, tx, currentLease, false, ct);
            sent = true;
            var response = await transport.SendAsync(HttpMethod.Post,
                "/" + expected.PhysicalName + "/_update/" + original.ServiceId.ToString("D") + "?retry_on_conflict=0", update, ct);
            MarketplaceGenerationResponse.RequireWrite(response, expected.PhysicalName, original.ServiceId);
            injectFailure?.Invoke(MarketplaceGenerationDeliveryProtocol.DeliveryBoundary.AfterWriteBeforeAcknowledgement);
            MarketplacePhysicalAdmission.RequireCluster(expected, await transport.SendAsync(HttpMethod.Get, "/", null, ct));
            _ = MarketplacePhysicalAdmission.InspectResponses(expected, cluster,
                await transport.SendAsync(HttpMethod.Get, "/" + expected.PhysicalName, null, ct));
            await MarketplaceGenerationAttemptOutcomes.CompleteAsync(db, attempt, true, ct);
            await MarketplaceExternalWriteBarrier.RecordDefiniteOutcomeAsync(db, original, ExternalDefiniteOutcome.Acknowledged, ct);
            await tx.CommitAsync(ct);
        }
        catch when (!sent)
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await MarketplaceGenerationAttemptOutcomes.CompleteAsync(db, attempt, false, cleanup.Token);
            throw;
        }
        // Still not refreshed visibility. ObserveRefreshedRunAsync is mandatory.
    }
}
