using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;
using PointOfSale.Infrastructure.MongoDB.Configuration;

namespace PointOfSale.Infrastructure.MongoDB;

/// <summary>
/// Keeps schema provisioning separate from an already-provisioned runtime.
/// Validate-only mode never sends createIndexes, collMod, or dropIndexes.
/// </summary>
public sealed class MongoIndexManagementPolicy(IOptions<MongoDbSettings>? settings = null)
{
    public bool ValidateOnly { get; } = settings?.Value.ValidateIndexesOnly ?? false;

    public void Ensure<T>(IMongoCollection<T> collection, IEnumerable<CreateIndexModel<T>> models)
    {
        var required = models.ToArray();
        if (!ValidateOnly)
        {
            collection.Indexes.CreateMany(required);
            return;
        }

        using var cursor = collection.Indexes.List();
        Validate(collection, required, cursor.ToList());
    }

    public async Task EnsureAsync<T>(IMongoCollection<T> collection, IEnumerable<CreateIndexModel<T>> models,
        CancellationToken cancellationToken = default)
    {
        var required = models.ToArray();
        if (!ValidateOnly)
        {
            await collection.Indexes.CreateManyAsync(required, cancellationToken);
            return;
        }

        using var cursor = await collection.Indexes.ListAsync(cancellationToken: cancellationToken);
        Validate(collection, required, await cursor.ToListAsync(cancellationToken));
    }

    public void EnsureOne<T>(IMongoCollection<T> collection, CreateIndexModel<T> model) =>
        Ensure(collection, [model]);

    public Task EnsureOneAsync<T>(IMongoCollection<T> collection, CreateIndexModel<T> model,
        CancellationToken cancellationToken = default) => EnsureAsync(collection, [model], cancellationToken);

    private static void Validate<T>(IMongoCollection<T> collection, CreateIndexModel<T>[] required,
        List<BsonDocument> existing)
    {
        var render = new RenderArgs<T>(collection.DocumentSerializer, BsonSerializer.SerializerRegistry);
        foreach (var model in required)
        {
            var name = model.Options?.Name;
            var keys = model.Keys.Render(render);
            var partialFilter = (model.Options as CreateIndexOptions<T>)?.PartialFilterExpression?.Render(render);
            // Existing application definitions are explicitly named. Refuse to infer
            // a schema identity if a future definition omits its stable name.
            if (string.IsNullOrWhiteSpace(name) || !existing.Any(index =>
                    index.GetValue("name", "").AsString == name && Matches(index, keys, model.Options, partialFilter)))
                throw new MongoIndexValidationException();
        }
    }

    public static bool Matches(BsonDocument existing, BsonDocument requiredKeys, CreateIndexOptions? options,
        BsonDocument? requiredPartialFilter = null)
    {
        if (!existing.TryGetValue("key", out var value) || !value.IsBsonDocument) return false;
        var keys = value.AsBsonDocument;
        var textFields = requiredKeys.Elements.Where(e => e.Value.IsString && e.Value.AsString == "text").ToArray();
        if (textFields.Length > 0)
        {
            // Mongo stores text keys as _fts/_ftsx and the original fields in weights.
            if (!keys.Contains("_fts") || keys["_fts"] != "text" || keys.GetValue("_ftsx", 0) != 1 ||
                existing.GetValue("default_language", "english") != "english" ||
                existing.GetValue("language_override", "language") != "language" ||
                !existing.TryGetValue("weights", out var weights) || !weights.IsBsonDocument ||
                weights.AsBsonDocument.ElementCount != textFields.Length ||
                textFields.Any(e => !weights.AsBsonDocument.TryGetValue(e.Name, out var weight) || weight.ToDouble() != 1))
                return false;
            var normalized = new BsonDocument();
            foreach (var element in keys)
            {
                if (element.Name == "_fts") normalized.AddRange(textFields);
                else if (element.Name != "_ftsx") normalized.Add(element);
            }
            keys = normalized;
        }

        if (!keys.Equals(requiredKeys) || existing.GetValue("unique", false).ToBoolean() != (options?.Unique ?? false) ||
            existing.GetValue("sparse", false).ToBoolean() != (options?.Sparse ?? false)) return false;
        var ttl = options?.ExpireAfter;
        if (ttl.HasValue)
        {
            if (!existing.TryGetValue("expireAfterSeconds", out var seconds) || seconds.ToDouble() != ttl.Value.TotalSeconds) return false;
        }
        else if (existing.Contains("expireAfterSeconds")) return false;
        if (requiredPartialFilter == null)
        {
            if (existing.Contains("partialFilterExpression")) return false;
        }
        else if (!existing.TryGetValue("partialFilterExpression", out var partial) ||
            !partial.IsBsonDocument || !partial.AsBsonDocument.Equals(requiredPartialFilter)) return false;
        // Never accept an unexpected partial filter or custom collation.
        return !existing.Contains("collation") &&
            !existing.GetValue("hidden", false).ToBoolean();
    }
}

public sealed class MongoIndexValidationException() : InvalidOperationException(
    "A required MongoDB index is missing or incompatible. Provision schema separately; runtime validation does not modify indexes.");
