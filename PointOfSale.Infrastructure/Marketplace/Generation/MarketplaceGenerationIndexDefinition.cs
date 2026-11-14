using System.Text.Json;
using System.Text.Json.Nodes;

namespace PointOfSale.Infrastructure.Marketplace.Generation;

/// <summary>Explicit provisioning plan only. Never creates an index or starts a worker.</summary>
internal static class MarketplaceGenerationIndexDefinition
{
    internal static string CreateBody(Guid journal, Guid generation, int shards = 3, int replicas = 1)
    {
        if (journal == Guid.Empty || generation == Guid.Empty || shards is < 1 or > 64 || replicas is < 0 or > 3)
            throw new ArgumentException("Explicit generation lineage and bounded shard topology required.");
        var mappings = JsonNode.Parse("""
            {"dynamic":"strict","_meta":{},"properties":{
            "serviceId":{"type":"keyword"},"tenantId":{"type":"keyword"},
            "title":{"type":"text","analyzer":"standard"},"description":{"type":"text","analyzer":"standard"},
            "categoryId":{"type":"keyword"},"categoryPath":{"type":"text","analyzer":"standard"},
            "providerProfileId":{"type":"keyword"},"providerDisplayName":{"type":"text","analyzer":"standard"},
            "createdAt":{"type":"date"},"isActive":{"type":"boolean"},"startingPrice":{"type":"double"},
            "pricingType":{"type":"keyword"},"currency":{"type":"keyword"},"serviceImages":{"type":"text","index":false},
            "trust":{"dynamic":"strict","properties":{"averageRating":{"type":"double"},"totalReviews":{"type":"integer"},
            "trustBadge":{"type":"keyword"},"marketingReviewCount":{"type":"integer"},"posVerifiedCount":{"type":"integer"},
            "marketingWeightedScore":{"type":"double"},"lastEventId":{"type":"keyword"},"lastEventOccurredAtUtc":{"type":"date"},"lastEventVersion":{"type":"integer"}}},
            "serviceStats":{"dynamic":"strict","properties":{"totalRequests":{"type":"integer"},"completedRequests":{"type":"integer"},"completionRate":{"type":"double"}}},
            "__marketplace":{"dynamic":"strict","properties":{"journal":{"type":"keyword"},"generation":{"type":"keyword"},
            "revision":{"type":"long"},"epoch":{"type":"long"},"digest":{"type":"keyword"},"deleted":{"type":"boolean"}}}}}
            """)!.AsObject();
        mappings["_meta"] = JsonSerializer.SerializeToNode(new { journal = journal.ToString("D"), generation = generation.ToString("D"), schema = "marketplace-generation-v1" });
        return JsonSerializer.Serialize(new { settings = new { number_of_shards = shards, number_of_replicas = replicas,
            default_pipeline = "_none", final_pipeline = "_none" }, mappings });
    }
}
