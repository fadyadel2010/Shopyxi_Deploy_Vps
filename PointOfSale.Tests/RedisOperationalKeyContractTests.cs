using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using PointOfSale.API.Services.AI;
using PointOfSale.Infrastructure.Redis.Configuration;
using PointOfSale.Infrastructure.Redis.Services;
using StackExchange.Redis;

namespace PointOfSale.Tests;

public class RedisOperationalKeyContractTests
{
    [Fact]
    public async Task CommerceInsights_UsesConfiguredInstanceAndExactTenantForBothCounters()
    {
        var keys = new List<string>();
        var database = new Mock<IDatabase>();
        database.Setup(db => db.StringGetAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
            .Callback<RedisKey, CommandFlags>((key, _) => keys.Add(key.ToString()))
            .ReturnsAsync((RedisValue)4);
        var connection = new Mock<IConnectionMultiplexer>();
        connection.Setup(c => c.GetDatabase(It.IsAny<int>(), It.IsAny<object>())).Returns(database.Object);
        var settings = Options.Create(new RedisSettings { InstanceName = "fixture-runtime" });
        using var factory = new RedisConnectionFactory(settings, NullLogger<RedisConnectionFactory>.Instance, connection.Object);
        var service = new CommerceAiInsightsService(factory, NullLogger<CommerceAiInsightsService>.Instance, settings);
        var tenant = Guid.NewGuid();

        var result = await service.GetInsightsAsync(tenant);

        Assert.Equal(4, result.ChatOpened);
        Assert.Equal(4, result.DraftCreated);
        Assert.Equal(2, keys.Count);
        Assert.Contains($"fixture-runtime:tenant:{tenant:N}:commerce-ai:chatopened:v1", keys);
        Assert.Contains($"fixture-runtime:tenant:{tenant:N}:commerce-ai:orderdraftcreated:v1", keys);
        Assert.All(keys, key => Assert.DoesNotContain("commerce_ai:", key));
    }
}
