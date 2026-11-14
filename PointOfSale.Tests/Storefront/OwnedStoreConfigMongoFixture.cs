using MongoDB.Driver;

namespace PointOfSale.Tests.Storefront;

internal static class OwnedStoreConfigMongoFixture
{
    internal static MongoClientSettings Settings(string? connection, string? database)
    {
        const string prefix = "storeconfig_cert_";
        if (string.IsNullOrWhiteSpace(connection) || database is null || !database.StartsWith(prefix, StringComparison.Ordinal) ||
            database.Length != prefix.Length + 32 || !Guid.TryParseExact(database[prefix.Length..], "N", out _))
            throw new InvalidOperationException("An explicit owned StoreConfig fixture identity is required.");
        var url = new MongoUrl(connection);
        var servers = url.Servers.ToArray();
        if (servers.Length != 1 || servers[0].Host != "app2h-mongo" || servers[0].Port != 27017 ||
            url.Username != "app2h_runtime" || string.IsNullOrWhiteSpace(url.Password) ||
            url.AuthenticationSource != "admin" || !string.IsNullOrEmpty(url.DatabaseName) ||
            !string.IsNullOrEmpty(url.ReplicaSetName))
            throw new InvalidOperationException("Refusing a nonfixture Mongo endpoint or bootstrap identity.");
        return MongoClientSettings.FromUrl(url);
    }
}

public sealed class OwnedStoreConfigMongoFixtureTests
{
    [Fact]
    public void ExactOwnedTransportAcceptsOnlyRestrictedRuntimeIdentity()
    {
        var settings = OwnedStoreConfigMongoFixture.Settings(
            "mongodb://app2h_runtime:fictional-password@app2h-mongo:27017/?authSource=admin",
            "storeconfig_cert_" + Guid.NewGuid().ToString("N"));
        Assert.Equal("app2h-mongo", settings.Server.Host);
        Assert.Equal("app2h_runtime", settings.Credential.Username);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("mongodb://app2h_fixture:bootstrap@app2h-mongo:27017/?authSource=admin", "storeconfig_cert_00000000000000000000000000000001")]
    [InlineData("mongodb://app2h_runtime:fictional@localhost:27017/?authSource=admin", "storeconfig_cert_00000000000000000000000000000001")]
    [InlineData("mongodb://app2h_runtime:fictional@app2h-mongo:27017/?authSource=admin", "shared")]
    [InlineData("mongodb://app2h_runtime:fictional@app2h-mongo:27017/shared?authSource=admin", "storeconfig_cert_00000000000000000000000000000001")]
    public void ForeignOrDiscoveredDatabaseCannotConstructAClient(string? connection, string? database) =>
        Assert.Throws<InvalidOperationException>(() => OwnedStoreConfigMongoFixture.Settings(connection, database));
}
