using System.Text.Json;

namespace PointOfSale.Infrastructure.Marketplace.Generation;

/// <summary>
/// Whole-document remote CAS payload for the future common transport. No client
/// or target resolution here. Never use an OpenSearch physical delete: retained
/// tombstones are the fence against arbitrarily late source revisions.
/// Unregistered until physical admission, transport outcomes and all writers agree.
/// </summary>
internal static class MarketplaceVersionedDocument
{
    internal const string Script = """
        if (ctx.op != 'create') {
          if (!ctx._source.containsKey('__marketplace')) {
            throw new IllegalArgumentException('MARKETPLACE_UNVERSIONED_DOCUMENT');
          }
          def prior = ctx._source.__marketplace;
          if (ctx._source.serviceId != params.document.serviceId) {
            throw new IllegalArgumentException('MARKETPLACE_DOCUMENT_IDENTITY_MISMATCH');
          }
          if (prior.journal != params.journal || prior.generation != params.generation) {
            throw new IllegalArgumentException('MARKETPLACE_LINEAGE_MISMATCH');
          }
          if (prior.epoch > params.epoch) {
            throw new IllegalArgumentException('MARKETPLACE_STALE_EPOCH');
          }
          if (prior.revision > params.revision) { ctx.op = 'noop'; return; }
          if (prior.revision == params.revision) {
            if (prior.digest != params.digest || prior.deleted != params.deleted) {
              throw new IllegalArgumentException('MARKETPLACE_REVISION_CONTENT_CONFLICT');
            }
            if (prior.epoch == params.epoch) { ctx.op = 'noop'; return; }
          }
        }
        ctx._source.clear();
        ctx._source.putAll(params.document);
        ctx._source.__marketplace = [
          'journal': params.journal, 'generation': params.generation,
          'revision': params.revision, 'epoch': params.epoch,
          'digest': params.digest, 'deleted': params.deleted
        ];
        """;

    internal static string BuildUpdate(Guid journal, ExternalWriteTicket ticket, string? payload)
    {
        if (journal == Guid.Empty || ticket.GenerationId == Guid.Empty || ticket.ServiceId == Guid.Empty ||
            ticket.SourceRevision < 0 || ticket.Epoch <= 0 || ticket.Digest.Length != 64 ||
            ticket.Digest.Any(c => !char.IsAsciiHexDigit(c) || char.IsAsciiLetterUpper(c)))
            throw new ArgumentException("Exact journal/generation/version/epoch and output digest required.");
        if (MarketplaceFrozenOutput.RowDigest(ticket.ServiceId, payload) != ticket.Digest)
            throw new InvalidDataException("External payload does not match its sealed output digest.");
        object document;
        if (payload is null)
        {
            document = new { serviceId = ticket.ServiceId, isActive = false };
        }
        else
        {
            using var parsed = JsonDocument.Parse(payload);
            RequireUniqueProperties(parsed.RootElement);
            if (parsed.RootElement.ValueKind != JsonValueKind.Object ||
                !parsed.RootElement.TryGetProperty("serviceId", out var id) || !id.TryGetGuid(out var identity) ||
                identity != ticket.ServiceId || parsed.RootElement.TryGetProperty("__marketplace", out _))
                throw new InvalidDataException("Document identity or reserved version envelope rejected.");
            document = parsed.RootElement.Clone();
        }
        return JsonSerializer.Serialize(new
        {
            scripted_upsert = true,
            script = new
            {
                lang = "painless", source = Script,
                @params = new
                {
                    journal = journal.ToString("D"), generation = ticket.GenerationId.ToString("D"),
                    revision = ticket.SourceRevision, epoch = ticket.Epoch, digest = ticket.Digest,
                    deleted = payload is null, document
                }
            },
            upsert = new Dictionary<string, object>()
        });
    }

    private static void RequireUniqueProperties(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new InvalidDataException("Duplicate generation payload property rejected.");
                RequireUniqueProperties(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) RequireUniqueProperties(item);
    }
}
