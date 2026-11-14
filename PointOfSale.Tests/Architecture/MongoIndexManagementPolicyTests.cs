using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;
using Moq;
using PointOfSale.Infrastructure.MongoDB;
using PointOfSale.Infrastructure.MongoDB.Configuration;

namespace PointOfSale.Tests.Architecture;

public sealed class MongoIndexManagementPolicyTests
{
    private static readonly CreateIndexModel<BsonDocument> Required = new(
        Builders<BsonDocument>.IndexKeys.Ascending("tenant").Descending("created"),
        new CreateIndexOptions { Name = "required", Unique = true, Sparse = true, ExpireAfter = TimeSpan.FromDays(1) });

    private static BsonDocument Existing() => new()
    {
        { "name", "required" }, { "key", new BsonDocument { { "tenant", 1 }, { "created", -1 } } },
        { "unique", true }, { "sparse", true }, { "expireAfterSeconds", 86400 }
    };

    [Fact]
    public void MatchingDefinitionsAreAccepted() =>
        Assert.True(MongoIndexManagementPolicy.Matches(Existing(), new BsonDocument { { "tenant", 1 }, { "created", -1 } }, Required.Options));

    [Theory]
    [InlineData("key")]
    [InlineData("unique")]
    [InlineData("sparse")]
    [InlineData("expireAfterSeconds")]
    [InlineData("partialFilterExpression")]
    [InlineData("collation")]
    [InlineData("hidden")]
    public void IncompatibleDefinitionsAreRejected(string field)
    {
        var existing = Existing();
        existing[field] = field switch
        {
            "key" => new BsonDocument { { "created", -1 }, { "tenant", 1 } },
            "unique" or "sparse" => false,
            "expireAfterSeconds" => 1,
            "hidden" => true,
            _ => new BsonDocument("locale", "en")
        };
        Assert.False(MongoIndexManagementPolicy.Matches(existing, new BsonDocument { { "tenant", 1 }, { "created", -1 } }, Required.Options));
    }

    [Fact]
    public void ServerTextIndexRepresentationIsValidated()
    {
        var existing = new BsonDocument { { "key", new BsonDocument { { "_fts", "text" }, { "_ftsx", 1 } } }, { "weights", new BsonDocument("name", 1) } };
        Assert.True(MongoIndexManagementPolicy.Matches(existing, new BsonDocument("name", "text"), new CreateIndexOptions()));
        existing["weights"] = new BsonDocument("other", 1);
        Assert.False(MongoIndexManagementPolicy.Matches(existing, new BsonDocument("name", "text"), new CreateIndexOptions()));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ValidateOnlyListsMetadataAndNeverCreatesIndexes(bool async)
    {
        var manager = new Mock<IMongoIndexManager<BsonDocument>>(MockBehavior.Strict);
        var cursor = new Mock<IAsyncCursor<BsonDocument>>();
        cursor.SetupGet(x => x.Current).Returns(new[] { Existing() });
        cursor.SetupSequence(x => x.MoveNext(It.IsAny<CancellationToken>())).Returns(true).Returns(false);
        cursor.SetupSequence(x => x.MoveNextAsync(It.IsAny<CancellationToken>())).ReturnsAsync(true).ReturnsAsync(false);
        manager.Setup(x => x.List(It.IsAny<CancellationToken>())).Returns(cursor.Object);
        manager.Setup(x => x.ListAsync(It.IsAny<CancellationToken>())).ReturnsAsync(cursor.Object);
        var collection = new Mock<IMongoCollection<BsonDocument>>();
        collection.SetupGet(x => x.Indexes).Returns(manager.Object);
        collection.SetupGet(x => x.DocumentSerializer).Returns(BsonSerializer.SerializerRegistry.GetSerializer<BsonDocument>());
        var policy = new MongoIndexManagementPolicy(Options.Create(new MongoDbSettings { ValidateIndexesOnly = true }));
        if (async) await policy.EnsureAsync(collection.Object, [Required]);
        else policy.Ensure(collection.Object, [Required]);
        manager.Verify(x => x.CreateMany(It.IsAny<IEnumerable<CreateIndexModel<BsonDocument>>>(), It.IsAny<CancellationToken>()), Times.Never);
        manager.Verify(x => x.CreateManyAsync(It.IsAny<IEnumerable<CreateIndexModel<BsonDocument>>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public void MissingIndexFailsClosedWithoutSchemaCreation()
    {
        var manager = new Mock<IMongoIndexManager<BsonDocument>>(MockBehavior.Strict);
        var cursor = new Mock<IAsyncCursor<BsonDocument>>();
        cursor.SetupGet(x => x.Current).Returns(Array.Empty<BsonDocument>());
        cursor.Setup(x => x.MoveNext(It.IsAny<CancellationToken>())).Returns(false);
        manager.Setup(x => x.List(It.IsAny<CancellationToken>())).Returns(cursor.Object);
        var collection = new Mock<IMongoCollection<BsonDocument>>();
        collection.SetupGet(x => x.Indexes).Returns(manager.Object);
        collection.SetupGet(x => x.DocumentSerializer).Returns(BsonSerializer.SerializerRegistry.GetSerializer<BsonDocument>());
        var policy = new MongoIndexManagementPolicy(Options.Create(new MongoDbSettings { ValidateIndexesOnly = true }));
        Assert.Throws<MongoIndexValidationException>(() => policy.Ensure(collection.Object, [Required]));
    }

    [Fact]
    public void AllApplicationIndexCreationRoutesThroughPolicy()
    {
        var root = Environment.GetEnvironmentVariable("POINT_OF_SALE_SOLUTION_ROOT")!;
        foreach (var project in new[] { "PointOfSale.API", "PointOfSale.Infrastructure" })
        foreach (var file in Directory.EnumerateFiles(Path.Combine(root, project), "*.cs", SearchOption.AllDirectories))
        {
            if (Path.GetFileName(file) == "MongoIndexManagementPolicy.cs") continue;
            var source = File.ReadAllText(file);
            Assert.DoesNotContain(".Indexes.Create", source);
        }
        var program = CertificationSource.ReadRepoFile("PointOfSale.API/Program.cs");
        Assert.Contains("AddHostedService<MongoDbIndexInitializer>()", program);
        Assert.Contains("AddHostedService<CommerceHubMongoDbIndexInitializer>()", program);
        Assert.Contains("AddCheck<PointOfSale.API.HealthChecks.MongoDependencyHealthCheck>", program);
    }
}
