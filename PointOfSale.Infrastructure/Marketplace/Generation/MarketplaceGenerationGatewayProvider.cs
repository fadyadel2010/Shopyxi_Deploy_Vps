using System.Text.Json;

namespace PointOfSale.Infrastructure.Marketplace.Generation;

/// <summary>
/// Real supported-client adapter. Explicitly constructed, never DI registered.
/// Provisioned update-only credentials and index lifecycle isolation are mandatory;
/// inspection does not make an OpenSearch request UUID-conditional.
/// </summary>
internal sealed class MarketplaceGenerationGatewayProvider(MarketplaceGenerationTransport transport, MarketplaceGenerationOperationStore? store = null) : IGenerationGatewayProvider
{
    public async Task<GenerationOperation?> FindVisibleSuccessorAsync(GenerationOperation operation, CancellationToken ct)
    {
        if (store is null) return null;
        await InspectAsync(operation.Target, ct);
        var body = JsonSerializer.Serialize(new { ids = new[] { operation.Ticket.ServiceId.ToString("D") } });
        var response = await transport.SendAsync(HttpMethod.Post, "/" + operation.Target.PhysicalName + "/_mget", body, ct);
        using var parsed = JsonDocument.Parse(response);
        var docs = parsed.RootElement.GetProperty("docs");
        if (docs.GetArrayLength() != 1) throw new InvalidDataException("Exact successor observation coverage required.");
        var doc = docs[0];
        if (doc.GetProperty("_index").GetString() != operation.Target.PhysicalName ||
            doc.GetProperty("_id").GetString() != operation.Ticket.ServiceId.ToString("D") || doc.TryGetProperty("error", out _))
            throw new InvalidDataException("Foreign successor identity.");
        if (doc.GetProperty("found").ValueKind == JsonValueKind.False) return null;
        var revision = doc.GetProperty("_source").GetProperty("__marketplace").GetProperty("revision").GetInt64();
        if (revision <= operation.Ticket.SourceRevision) return null;
        var successor = await store.FindRevisionAsync(operation.Target.Generation, operation.Ticket.ServiceId, revision, ct);
        MarketplaceGenerationOperationRecovery.RequireSuccessor(operation, successor);
        // Re-read AFTER refresh against that independently persisted successor,
        // including its complete payload, tombstone, epoch, target and digest.
        if (!await VerifyAsync(successor!, true, ct)) throw new InvalidDataException("Successor is not search-visible.");
        return successor;
    }

    public async Task WriteAsync(GenerationDispatchPermit permit, CancellationToken ct)
    {
        await InspectAsync(permit.Operation.Target, ct);
        var response = await transport.SendGatewayUpdateAsync(permit, ct);
        MarketplaceGenerationResponse.RequireWrite(response, permit.Operation.Target.PhysicalName, permit.Operation.Ticket.ServiceId);
    }

    public async Task<bool> VerifyAsync(GenerationOperation operation, bool refresh, CancellationToken ct)
    {
        await InspectAsync(operation.Target, ct);
        if (refresh) MarketplaceGenerationResponse.RequireShards(await transport.RefreshGatewayAsync(operation.Target, ct));
        var response = await transport.SendAsync(HttpMethod.Post, "/" + operation.Target.PhysicalName +
            (refresh ? "/_mget?realtime=false" : "/_mget"),
            JsonSerializer.Serialize(new { ids = new[] { operation.Ticket.ServiceId.ToString("D") } }), ct);
        // Parse strictly even absence: a malformed/foreign response must not be
        // interpreted as a safe not-applied result or permit rebinding the target.
        using var parsed = JsonDocument.Parse(response);
        var docs = parsed.RootElement.GetProperty("docs");
        if (docs.GetArrayLength() != 1) throw new InvalidDataException("Exact gateway observation coverage required.");
        var doc = docs[0];
        if (doc.GetProperty("_index").GetString() != operation.Target.PhysicalName ||
            doc.GetProperty("_id").GetString() != operation.Ticket.ServiceId.ToString("D") || doc.TryGetProperty("error", out _))
            throw new InvalidDataException("Foreign gateway document observation.");
        if (doc.GetProperty("found").ValueKind == JsonValueKind.False) return false;
        MarketplaceGenerationResponse.RequireDocuments(response, operation.Target, operation.Ticket.SourceRevision,
            [new(operation.Ticket.ServiceId, operation.Payload, operation.Ticket.Digest, operation.Ticket.Epoch)]);
        await InspectAsync(operation.Target, ct);
        return true;
    }

    private async Task InspectAsync(MarketplacePhysicalAdmission.Expected target, CancellationToken ct)
    {
        MarketplacePhysicalAdmission.Validate(target);
        var cluster = await transport.SendAsync(HttpMethod.Get, "/", null, ct);
        _ = MarketplacePhysicalAdmission.InspectResponses(target, cluster,
            await transport.SendAsync(HttpMethod.Get, "/" + target.PhysicalName, null, ct));
    }
}
