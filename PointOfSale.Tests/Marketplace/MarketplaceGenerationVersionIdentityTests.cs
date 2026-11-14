using System.Text.Json;
using PointOfSale.Infrastructure.Marketplace.Generation;

namespace PointOfSale.Tests.Marketplace;

public sealed class MarketplaceGenerationVersionIdentityTests
{
    [Fact]
    public void JavaDoubleSpellingCanDifferWithoutWeakeningExactEpochOrCounterValidation()
    {
        var service = Guid.NewGuid();
        var document = new PointOfSale.Infrastructure.Elasticsearch.Documents.MarketplaceServiceDocument
        { ServiceId = service, StartingPrice = 100000000000000000000000m };
        var payload = MarketplaceFrozenOutput.SerializeDocument(document);
        var expected = new MarketplacePhysicalAdmission.Expected(Guid.NewGuid(), Guid.NewGuid(), "cluster", "index", new string('a', 64));
        var digest = MarketplaceFrozenOutput.RowDigest(service, payload);
        var source = System.Text.Json.Nodes.JsonNode.Parse(payload)!.AsObject();
        source["startingPrice"] = System.Text.Json.Nodes.JsonNode.Parse("9.999999999999999E22");
        source["__marketplace"] = JsonSerializer.SerializeToNode(new
        { journal = expected.Journal, generation = expected.Generation, revision = long.MaxValue, epoch = long.MaxValue, digest, deleted = false });
        string Response() => JsonSerializer.Serialize(new { docs = new[] { new
        { _index = expected.PhysicalName, _id = service.ToString("D"), found = true, _source = source } } });
        MarketplaceGenerationResponse.RequireDocuments(Response(), expected, long.MaxValue,
            [new(service, payload, digest, long.MaxValue)]);
        source["__marketplace"]!["epoch"] = long.MaxValue - 1;
        Assert.Throws<InvalidDataException>(() => MarketplaceGenerationResponse.RequireDocuments(Response(), expected, long.MaxValue,
            [new(service, payload, digest, long.MaxValue)]));
    }

    [Theory]
    [InlineData("0.3333333333333333333333333333")]
    [InlineData("12345678901234567890.123456789")]
    [InlineData("79228162514264337593543950335")]
    public void ProviderDoubleFieldsAreNormalizedBeforeSealingWithoutRoundingInt64Envelope(string value)
    {
        var amount = decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
        var service = Guid.NewGuid();
        var document = new PointOfSale.Infrastructure.Elasticsearch.Documents.MarketplaceServiceDocument
        { ServiceId = service, StartingPrice = amount };
        document.Trust.AverageRating = amount;
        document.ServiceStats.CompletionRate = amount;
        var payload = MarketplaceFrozenOutput.SerializeDocument(document);
        using var body = JsonDocument.Parse(payload);
        Assert.Equal(JsonSerializer.Serialize((double)amount), body.RootElement.GetProperty("startingPrice").GetRawText());
        var expected = new MarketplacePhysicalAdmission.Expected(Guid.NewGuid(), Guid.NewGuid(), "cluster", "index", new string('a', 64));
        var digest = MarketplaceFrozenOutput.RowDigest(service, payload);
        var source = System.Text.Json.Nodes.JsonNode.Parse(payload)!.AsObject();
        source["__marketplace"] = JsonSerializer.SerializeToNode(new
        { journal = expected.Journal, generation = expected.Generation, revision = long.MaxValue, epoch = long.MaxValue, digest, deleted = false });
        string Response() => JsonSerializer.Serialize(new { docs = new[] { new
        { _index = expected.PhysicalName, _id = service.ToString("D"), found = true, _source = source } } });
        MarketplaceGenerationResponse.RequireDocuments(Response(), expected, long.MaxValue,
            [new(service, payload, digest, long.MaxValue)]);
        source["startingPrice"] = (double)amount * 2;
        Assert.Throws<InvalidDataException>(() => MarketplaceGenerationResponse.RequireDocuments(Response(), expected, long.MaxValue,
            [new(service, payload, digest, long.MaxValue)]));
    }

    [Theory]
    [InlineData("root")]
    [InlineData("nested")]
    [InlineData("array")]
    public void DuplicatePropertiesCannotProduceOneDigestButAmbiguousProviderContent(string location)
    {
        var service = Guid.NewGuid();
        var member = location switch { "root" => "\"title\":\"a\",\"title\":\"b\"",
            "nested" => "\"trust\":{\"rating\":1,\"rating\":2}",
            _ => "\"values\":[{\"x\":1,\"x\":2}]" };
        var payload = "{\"serviceId\":\"" + service + "\"," + member + "}";
        var ticket = Ticket(service, payload, long.MaxValue);
        Assert.Throws<InvalidDataException>(() => MarketplaceVersionedDocument.BuildUpdate(Guid.NewGuid(), ticket, payload));
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(9007199254740993L)]
    [InlineData(9223372036854775806L)]
    [InlineData(9223372036854775807L)]
    public void RevisionPayloadPreservesInt64WithoutFloatingPointRoundTrip(long revision)
    {
        var service = Guid.NewGuid();
        var payload = JsonSerializer.Serialize(new { serviceId = service, isActive = true });
        var ticket = Ticket(service, payload, revision);
        var first = MarketplaceVersionedDocument.BuildUpdate(Guid.Parse("11111111-1111-1111-1111-111111111111"), ticket, payload);
        Assert.Equal(first, MarketplaceVersionedDocument.BuildUpdate(Guid.Parse("11111111-1111-1111-1111-111111111111"), ticket, payload));
        using var document = JsonDocument.Parse(first);
        Assert.Equal(revision, document.RootElement.GetProperty("script").GetProperty("params").GetProperty("revision").GetInt64());
    }

    private static ExternalWriteTicket Ticket(Guid service, string payload, long revision) =>
        new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), service, revision, "fixture-index",
            new string('a', 64), MarketplaceFrozenOutput.RowDigest(service, payload), 1);
}
