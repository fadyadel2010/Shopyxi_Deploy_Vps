using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using OpenSearch.Client;
using OpenSearch.Net;
using PointOfSale.Core.Interfaces;
using PointOfSale.Infrastructure.Data;
using PointOfSale.Infrastructure.Elasticsearch.Configuration;
using PointOfSale.Infrastructure.Elasticsearch;
using PointOfSale.Infrastructure.Elasticsearch.Services;
using PointOfSale.Infrastructure.HealthChecks;
using PointOfSale.Infrastructure.Instrumentation;
using PointOfSale.Infrastructure.Jobs;
using PointOfSale.Infrastructure.ReadModels.Evolution;
using PointOfSale.Infrastructure.Elasticsearch.Documents;
using PointOfSale.Infrastructure.Marketplace.Generation;
using PointOfSale.Infrastructure.Consumers.Affiliate;
using PointOfSale.Infrastructure.MongoDB.Documents.Affiliate;
using System.Reflection;
using PointOfSale.Infrastructure.Scripts;

namespace PointOfSale.Tests.Marketplace;

/// <summary>Behavior of the fail-closed fallback, NOT provider or generation certification.</summary>
public sealed class MarketplaceGenerationRecoverySafetyTests
{
    [Theory]
    [InlineData("product")]
    [InlineData("commerce")]
    public async Task EnabledEvolutionRequiresAnExplicitTargetRatherThanTreatingNullAsAnUnknownModel(string owner)
    {
        var transport = new RecordingConnection("{}", 200);
        var service = new ReadModelEvolutionSnapshotProvider(null!, Client(transport), Options.Create(new ElasticsearchSettings {
            Enabled = true, ProductsIndexName = null!, CommerceHubProductsIndexName = null! }), NullLogger<ReadModelEvolutionSnapshotProvider>.Instance);
        var definition = ReadModelEvolutionRegistry.Get(owner == "product" ? ReadModelEvolutionRegistry.StorefrontProduct : ReadModelEvolutionRegistry.CommerceHubProduct,
            ReadModelStore.Elasticsearch);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.GetSnapshotAsync(definition));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.MarkConvergedAsync(definition, DateTime.UtcNow));
        Assert.Empty(transport.Requests);
    }

    public static IEnumerable<object[]> EvolutionForbiddenTargets() =>
        from owner in new[] { "product", "commerce" }
        from operation in new[] { "snapshot", "mark" }
        from target in new[] { "marketplace_services", "marketplace_services_g_0123456789abcdef0123456789abcdef",
            "marketplace_services_backup_20261102", "products,marketplace_services" }
        select new object[] { owner, operation, target };

    [Theory]
    [MemberData(nameof(EvolutionForbiddenTargets))]
    public async Task EvolutionRawCountAndMutationRejectForeignOwnerTargetsWithoutTransport(string owner, string operation, string target)
    {
        var transport = new RecordingConnection("{\"count\":0}", 200);
        var settings = new ElasticsearchSettings { Enabled = true, ProductsIndexName = "products", CommerceHubProductsIndexName = "commercehub_products" };
        var service = new ReadModelEvolutionSnapshotProvider(null!, Client(transport), Options.Create(settings), NullLogger<ReadModelEvolutionSnapshotProvider>.Instance);
        settings.ProductsIndexName = target; settings.CommerceHubProductsIndexName = target;
        var definition = ReadModelEvolutionRegistry.Get(owner == "product" ? ReadModelEvolutionRegistry.StorefrontProduct : ReadModelEvolutionRegistry.CommerceHubProduct,
            ReadModelStore.Elasticsearch);
        var rejected = false;
        try
        {
            if (operation == "mark") await service.MarkConvergedAsync(definition, DateTime.UtcNow);
            else await service.GetSnapshotAsync(definition);
        }
        catch (InvalidOperationException) { rejected = true; }
        Assert.True(rejected && transport.Requests.Count == 0,
            "Raw convergence path must reject before transport: " + operation + "; captured=" + string.Join(" | ", transport.Requests));
    }

    [Theory]
    [InlineData("product", "products")]
    [InlineData("commerce", "commercehub_products")]
    public async Task EvolutionRawCountAndMarkKeepOrdinaryOtherOwnerTargets(string owner, string target)
    {
        var transport = new RecordingConnection("{\"count\":0}", 200);
        var service = new ReadModelEvolutionSnapshotProvider(null!, Client(transport), Options.Create(new ElasticsearchSettings { Enabled = true }),
            NullLogger<ReadModelEvolutionSnapshotProvider>.Instance);
        var definition = ReadModelEvolutionRegistry.Get(owner == "product" ? ReadModelEvolutionRegistry.StorefrontProduct : ReadModelEvolutionRegistry.CommerceHubProduct,
            ReadModelStore.Elasticsearch);
        var snapshot = await service.GetSnapshotAsync(definition);
        Assert.NotNull(snapshot); Assert.Equal(0, snapshot.TotalDocuments);
        await service.MarkConvergedAsync(definition, DateTime.UtcNow);
        Assert.Equal(4, transport.Requests.Count);
        Assert.All(transport.Requests.Take(3), request => Assert.Equal("POST /" + target + "/_count", request));
        Assert.Equal("POST /" + target + "/_update_by_query", transport.Requests[3]);
    }

    [Fact]
    public async Task DisabledEvolutionNeverObservesOrMarksAnUnusedForbiddenTarget()
    {
        var transport = new RecordingConnection("{}", 503);
        var service = new ReadModelEvolutionSnapshotProvider(null!, Client(transport), Options.Create(new ElasticsearchSettings {
            Enabled = false, ProductsIndexName = "marketplace_services", CommerceHubProductsIndexName = "marketplace_services" }),
            NullLogger<ReadModelEvolutionSnapshotProvider>.Instance);
        foreach (var name in new[] { ReadModelEvolutionRegistry.StorefrontProduct, ReadModelEvolutionRegistry.CommerceHubProduct })
        {
            var definition = ReadModelEvolutionRegistry.Get(name, ReadModelStore.Elasticsearch);
            Assert.Null(await service.GetSnapshotAsync(definition));
            await service.MarkConvergedAsync(definition, DateTime.UtcNow);
        }
        Assert.Empty(transport.Requests);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData(null, true)]
    [InlineData("marketplace_services", false)]
    [InlineData("marketplace_services", true)]
    [InlineData("marketplace_services_g_0123456789abcdef0123456789abcdef", false)]
    [InlineData("marketplace_services_g_0123456789abcdef0123456789abcdef", true)]
    [InlineData("marketplace_services_backup_20261102", false)]
    [InlineData("marketplace_services_backup_20261102", true)]
    [InlineData("products,marketplace_services", false)]
    [InlineData("products,marketplace_services", true)]
    public async Task AffiliateMaintenanceRejectsForbiddenSearchNamespaceBeforeAnyStoreAccess(string? target, bool tenantScoped)
    {
        var settings = new ElasticsearchSettings { Enabled = true, PublicAffiliateProductsIndexName = "public_affiliate_products" };
        var transport = new RecordingConnection("{}", 200);
        // Null store dependencies are deliberate no-access traps. This proves
        // command admission ordering, not execution of the allowed Mongo workflow.
        var command = new AF16E_ReindexAffiliateProductsWithStorefrontUrl(null!, null!, Client(transport), Options.Create(settings),
            new ConfigurationBuilder().Build(), NullLogger<AF16E_ReindexAffiliateProductsWithStorefrontUrl>.Instance);
        settings.PublicAffiliateProductsIndexName = target!;
        var result = tenantScoped ? await command.ReindexTenantAsync(Guid.NewGuid()) : await command.ReindexAsync();
        Assert.False(result.Success);
        Assert.StartsWith("AFFILIATE_REINDEX_SEARCH_TARGET_REJECTED", result.ErrorMessage ?? string.Empty);
        Assert.Empty(transport.Requests);
    }

    [Fact]
    public async Task DisabledAffiliateMaintenanceDoesNotTreatUnusedSearchConfigurationAsAuthority()
    {
        var transport = new RecordingConnection("{}", 200);
        var command = new AF16E_ReindexAffiliateProductsWithStorefrontUrl(null!, null!, Client(transport),
            Options.Create(new ElasticsearchSettings { Enabled = false, PublicAffiliateProductsIndexName = "marketplace_services" }),
            new ConfigurationBuilder().Build(), NullLogger<AF16E_ReindexAffiliateProductsWithStorefrontUrl>.Instance);
        var result = await command.ReindexAsync();
        Assert.False(result.Success); // No fake certification of the null Mongo/SQL dependencies.
        Assert.DoesNotContain("AFFILIATE_REINDEX_SEARCH_TARGET_REJECTED", result.ErrorMessage!, StringComparison.Ordinal);
        Assert.Empty(transport.Requests);
    }

    public static IEnumerable<object[]> AffiliateConsumerForbiddenTargets() =>
        from operation in new[] { "product", "delete-public", "index-public" }
        from target in new[] { "marketplace_services", "marketplace_services_g_0123456789abcdef0123456789abcdef",
            "marketplace_services_backup_20261102", "products,marketplace_services" }
        select new object[] { operation, target };

    [Theory]
    [MemberData(nameof(AffiliateConsumerForbiddenTargets))]
    public async Task AffiliateConsumerDirectMutationBranchesRejectOtherOwnerTargetsBeforeTransport(string operation, string target)
    {
        var transport = new RecordingConnection("{\"_index\":\"fixture\",\"_id\":\"fixture\",\"_version\":1,\"result\":\"created\",\"_shards\":{\"total\":1,\"successful\":1,\"failed\":0}}", 200);
        var settings = new ElasticsearchSettings { Enabled = true, ProductsIndexName = "products", PublicAffiliateProductsIndexName = "public_affiliate_products" };
        var consumer = AffiliateConsumer(transport, settings);
        settings.ProductsIndexName = target; settings.PublicAffiliateProductsIndexName = target;
        var tenant = Guid.NewGuid(); var product = Guid.NewGuid(); var rejected = false;
        try { await InvokeAffiliateBranch(consumer, operation, tenant, product); }
        catch (InvalidOperationException) { rejected = true; }
        Assert.True(rejected && transport.Requests.Count == 0,
            "Affiliate branch must reject before transport: " + operation + "; captured=" + string.Join(" | ", transport.Requests));
    }

    [Fact]
    public async Task AffiliateConsumerMultiRequestMutationPinsAnAllowedTargetDespiteOptionsChangingAfterFirstDelete()
    {
        var settings = new ElasticsearchSettings { Enabled = true, PublicAffiliateProductsIndexName = "public_affiliate_products" };
        string Response(RequestData request)
        {
            settings.PublicAffiliateProductsIndexName = "marketplace_services";
            return JsonSerializer.Serialize(new { _index = "public_affiliate_products", _id = "fixture", _version = 1,
                result = request.Method == global::OpenSearch.Net.HttpMethod.DELETE ? "deleted" : "created",
                _shards = new { total = 1, successful = 1, failed = 0 } });
        }
        var transport = new RecordingConnection("{}", 200, Response);
        await InvokeAffiliateBranch(AffiliateConsumer(transport, settings), "index-public", Guid.NewGuid(), Guid.NewGuid());
        Assert.Equal(3, transport.Requests.Count);
        Assert.All(transport.Requests, request => Assert.Contains("/public_affiliate_products/", request, StringComparison.Ordinal));
        Assert.DoesNotContain(transport.Requests, request => request.Contains("marketplace_services", StringComparison.Ordinal));
    }

    private static AffiliateProductPolicyProjectionConsumer AffiliateConsumer(RecordingConnection transport, ElasticsearchSettings settings) =>
        new(null!, Client(transport), Options.Create(settings), null!, new ConfigurationBuilder().Build(), NullLogger<AffiliateProductPolicyProjectionConsumer>.Instance);

    private static Task InvokeAffiliateBranch(AffiliateProductPolicyProjectionConsumer consumer, string operation, Guid tenant, Guid product)
    {
        var (method, arguments) = operation switch {
            "product" => ("IndexVersionedProductSearchDocumentAsync", new object[] {
                new ProductSearchDocument { TenantId = tenant, ProductId = product, ProductGeneration = 1 }, CancellationToken.None }),
            "delete-public" => ("DeletePublicAffiliateSearchVisibilityAsync", new object[] { tenant, product, CancellationToken.None }),
            "index-public" => ("IndexPublicAffiliateProductAsync", new object[] {
                new PublicAffiliateProductDocument { TenantId = tenant, ProductId = product, Currency = "EGP" }, CancellationToken.None }),
            _ => throw new ArgumentException("Exact compiled affiliate branch required.")
        };
        // Reach the compiled mutation leaf directly: no fake PostgreSQL locking,
        // no Mongo/host startup and no unrelated workflow masks a transport bypass.
        return (Task)typeof(AffiliateProductPolicyProjectionConsumer).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(consumer, arguments)!;
    }

    [Fact]
    public async Task PhysicalGenerationObservationStopsAtClusterMismatchAndCancellationNeverContactsServer()
    {
        var transport = new RecordingConnection("{\"cluster_uuid\":\"foreign-cluster\"}", 200);
        var expected = new MarketplacePhysicalAdmission.Expected(Guid.NewGuid(), Guid.NewGuid(), "pinned-cluster", "pinned-physical", new string('a', 64));
        await Assert.ThrowsAsync<InvalidDataException>(() => MarketplacePhysicalAdmission.InspectAsync(Client(transport), expected));
        Assert.Equal(new[] { "GET /" }, transport.Requests);
        transport.Requests.Clear();
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => MarketplacePhysicalAdmission.InspectAsync(Client(transport), expected, cancelled.Token));
        await Assert.ThrowsAsync<ArgumentException>(() => MarketplacePhysicalAdmission.InspectAsync(Client(transport), expected with { Generation = Guid.Empty }));
        Assert.Empty(transport.Requests);
    }

    [Theory]
    [InlineData("marketplace_services")]
    [InlineData("marketplace_services_g_0123456789abcdef0123456789abcdef")]
    [InlineData("marketplace_services_backup_20261102")]
    [InlineData("products,marketplace_services")]
    [InlineData("products/_doc")]
    [InlineData("products%2f_doc")]
    [InlineData("marketplace_*")]
    [InlineData(" Products ")]
    public async Task ProductWriterRechecksMutableOptionsBeforeEveryMutationWithoutTransport(string name)
    {
        var transport = new RecordingConnection("{}", 200);
        var settings = new ElasticsearchSettings { Enabled = true, ProductsIndexName = "products" };
        var service = ProductWriter(transport, settings);
        // IOptions.Value can also be held by direct callers. Validation only in
        // DI or the constructor is not an operation-time admission boundary.
        settings.ProductsIndexName = name;
        var tenant = Guid.NewGuid(); var product = Guid.NewGuid();
        Assert.False(await service.IndexAsync(new ProductSearchDocument { TenantId = tenant, ProductId = product }));
        Assert.False(await service.TombstoneAsync(tenant, product, 1, true));
        Assert.False(await service.DeleteAsync(tenant, product));
        Assert.False(await service.EnsureIndexExistsAsync());
        Assert.False(await service.DeleteIndexAsync());
        Assert.Empty(transport.Requests);
    }

    [Fact]
    public async Task DisabledProductWriterStillHasNoDependencyOrTransportEffectsWithInvalidConfiguration()
    {
        var transport = new RecordingConnection("{}", 503);
        var service = ProductWriter(transport, new ElasticsearchSettings {
            Enabled = false, ProductsIndexName = "marketplace_services_g_invalid" });
        var tenant = Guid.NewGuid(); var product = Guid.NewGuid();
        Assert.True(await service.IndexAsync(new ProductSearchDocument { TenantId = tenant, ProductId = product }));
        Assert.True(await service.TombstoneAsync(tenant, product, 1, true));
        Assert.True(await service.DeleteAsync(tenant, product));
        Assert.True(await service.EnsureIndexExistsAsync());
        Assert.True(await service.DeleteIndexAsync());
        Assert.Empty(transport.Requests);
    }

    private static ProductSearchIndexService ProductWriter(RecordingConnection transport, ElasticsearchSettings settings) =>
        new(Client(transport), Options.Create(settings), new SearchTelemetry(),
            Mock.Of<IReadModelEvolutionConvergenceService>(MockBehavior.Strict), NullLogger<ProductSearchIndexService>.Instance);

    [Theory]
    [InlineData("products")]
    [InlineData("products_v2")]
    [InlineData("catalog-2026.11")]
    public async Task ProductWriterKeepsOrdinaryConfiguredTargetsOnEveryMutationBranch(string name)
    {
        // A typed indexing response cannot be certified from an empty JSON body.
        // These are recording-transport controls, not server delivery evidence.
        string Response(RequestData data) => JsonSerializer.Serialize(new {
            _index = name, _id = "fixture", _version = 1,
            result = data.Method == global::OpenSearch.Net.HttpMethod.DELETE ? "deleted" : "created", acknowledged = true,
            _seq_no = 1, _primary_term = 1, _shards = new { total = 1, successful = 1, failed = 0 }
        });
        var transport = new RecordingConnection("{}", 200, Response);
        var service = ProductWriter(transport, new ElasticsearchSettings { Enabled = true, ProductsIndexName = name });
        var tenant = Guid.NewGuid(); var product = Guid.NewGuid();
        Assert.True(await service.IndexAsync(new ProductSearchDocument { TenantId = tenant, ProductId = product }));
        Assert.True(await service.TombstoneAsync(tenant, product, 1, true));
        Assert.True(await service.DeleteAsync(tenant, product));
        Assert.True(await service.EnsureIndexExistsAsync());
        Assert.True(await service.DeleteIndexAsync());
        Assert.Equal(7, transport.Requests.Count);
        Assert.All(transport.Requests, request => Assert.Contains("/" + name, request, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("marketplace_services")]
    [InlineData("marketplace_services_g_0123456789abcdef0123456789abcdef")]
    [InlineData("marketplace_services_backup_20261102")]
    [InlineData("marketplace_services_g_invalid")]
    [InlineData("products,marketplace_services")]
    [InlineData("products/_doc")]
    [InlineData("products%2f_doc")]
    [InlineData("marketplace_*")]
    [InlineData("")]
    [InlineData(" ")]
    public async Task CommerceHubExplicitTargetsCannotBypassNamespaceAdmissionOnAnyPublicBranch(string name)
    {
        var transport = new RecordingConnection("{}", 200);
        using var services = new ServiceCollection().BuildServiceProvider();
        var service = new CommerceHubSearchIndexService(Client(transport),
            Options.Create(new ElasticsearchSettings { Enabled = true }),
            services.GetRequiredService<IServiceScopeFactory>(), NullLogger<CommerceHubSearchIndexService>.Instance);
        var id = Guid.NewGuid();
        Assert.False(await service.IndexAsync(new() { Id = id, TenantId = Guid.NewGuid() }, name));
        Assert.False(await service.DeleteAsync(id, name));
        Assert.False(await service.DeleteAsync(Guid.NewGuid(), id, name));
        Assert.False(await service.PromoteIndexAsync(name));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.GetExistingIdsAsync(new[] { id }, name));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.GetExistingIdsAsync(
            new[] { new CommerceHubSearchDocumentIdentity(Guid.NewGuid(), id) }, name));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.DeleteStaleAsync(
            Array.Empty<CommerceHubSearchDocumentIdentity>(), targetIndexName: name));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.CountAsync(name));
        Assert.Empty(transport.Requests);
    }

    [Fact]
    public async Task CommerceHubRejectsAnObservedAliasOntoMarketplaceBeforeMappingOrPromotion()
    {
        var transport = new RecordingConnection(
            "{\"marketplace_services_g_0123456789abcdef0123456789abcdef\":{\"aliases\":{\"commercehub_products\":{\"is_write_index\":true}}}}", 200);
        using var services = new ServiceCollection().BuildServiceProvider();
        var service = new CommerceHubSearchIndexService(Client(transport),
            Options.Create(new ElasticsearchSettings { Enabled = true }),
            services.GetRequiredService<IServiceScopeFactory>(), NullLogger<CommerceHubSearchIndexService>.Instance);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.PromoteIndexAsync("commercehub_products_v3"));
        Assert.NotEmpty(transport.Requests);
        Assert.All(transport.Requests, r => Assert.True(r.StartsWith("GET ") || r.StartsWith("HEAD "), r));
    }

    public static IEnumerable<object[]> ConflictingIndexConfiguration() =>
        from property in new[] { "ProductsIndexName", "PublicAffiliateProductsIndexName", "CommerceHubProductsIndexName" }
        from name in new[] { "marketplace_services", "MARKETPLACE_SERVICES", "*", "marketplace_*", "products,marketplace_services", "_all" }
        select new object[] { property, name };

    [Theory]
    [MemberData(nameof(ConflictingIndexConfiguration))]
    public void OtherIndexOwnersCannotBeConfiguredToMutateMarketplace(string property, string name)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Elasticsearch:Enabled"] = "true", ["Elasticsearch:" + property] = name
        }).Build();
        Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddElasticsearch(configuration));
    }

    public static IEnumerable<object[]> ReservedOrMalformedOtherOwnerNames() =>
        from property in new[] { "ProductsIndexName", "PublicAffiliateProductsIndexName", "CommerceHubProductsIndexName" }
        from name in new[] { "marketplace_services_g_0123456789abcdef0123456789abcdef", "MARKETPLACE_SERVICES_G_0123456789ABCDEF0123456789ABCDEF",
            "marketplace_services_g_invalid", "marketplace_services_", "marketplace_services_backup_20261102",
            "products/_doc", "products%2f_doc", " products", "products ", "products#fragment", "products:remote", "-products", ".", ".." }
        select new object[] { property, name };

    [Theory]
    [MemberData(nameof(ReservedOrMalformedOtherOwnerNames))]
    public void OtherIndexOwnerConfigurationCannotClaimGenerationNamespaceOrPathSelectors(string property, string name)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Elasticsearch:Enabled"] = "true", ["Elasticsearch:" + property] = name
        }).Build();
        Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddElasticsearch(configuration));
    }

    [Theory]
    [InlineData("products")]
    [InlineData("products_v2")]
    [InlineData("public_affiliate_products")]
    [InlineData("commercehub_products_v12")]
    [InlineData("catalog-2026.11")]
    [InlineData(".catalog-v2")]
    public void OrdinaryOtherOwnerNamesRemainAdmissible(string name)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Elasticsearch:Enabled"] = "true", ["Elasticsearch:ProductsIndexName"] = name
        }).Build();
        Assert.NotEmpty(new ServiceCollection().AddElasticsearch(configuration));
    }

    [Fact]
    public void DisabledSearchDoesNotRequireValidOtherOwnerNamesOrEndpoint()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Elasticsearch:Enabled"] = "false", ["Elasticsearch:ProductsIndexName"] = "marketplace_services_g_invalid",
            ["Elasticsearch:Url"] = "not-an-endpoint"
        }).Build();
        Assert.NotEmpty(new ServiceCollection().AddElasticsearch(configuration));
    }

    [Fact]
    public void OtherOwnerNameGrammarRejectsAmbiguityWithoutTrimmingOrDecoding()
    {
        Assert.True(MarketplaceSearchIndexOwnership.IsOtherOwnerIndexName(new string('p', 255)));
        Assert.False(MarketplaceSearchIndexOwnership.IsOtherOwnerIndexName(new string('p', 256)));
        foreach (var name in new string?[] { null, "", " ", "_all", "+products", "products?", "products\\_doc",
            "products\n", "products\0", "Products", "prödücts", "products\u200b", "products%5fdoc", "products|other" })
            Assert.False(MarketplaceSearchIndexOwnership.IsOtherOwnerIndexName(name));
    }

    [Theory]
    [InlineData("marketplace_services_g_0123456789abcdef0123456789abcdef")]
    [InlineData("marketplace_services_g_invalid")]
    [InlineData("marketplace_services_backup_20261102")]
    [InlineData("products/_doc")]
    [InlineData("products%2f_doc")]
    [InlineData("products,marketplace_services")]
    [InlineData("marketplace_*")]
    public async Task GenericBootstrapRefusesReservedOrMalformedTargetEvenWhenDirectlyConstructed(string name)
    {
        var transport = new RecordingConnection(Valid(), 200);
        var product = new Mock<IProductSearchIndexService>();
        product.Setup(p => p.EnsureIndexExistsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(true);
        var settings = new ElasticsearchSettings { Enabled = true, PublicAffiliateProductsIndexName = name };
        Assert.False(await Bootstrap(Client(transport), product.Object, settings).EnsureIndicesExistAsync());
        Assert.DoesNotContain(transport.Requests, r => r.Contains(name, StringComparison.Ordinal));
        Assert.DoesNotContain(transport.Requests, r => r.StartsWith("PUT ", StringComparison.Ordinal));
    }

    public static IEnumerable<object[]> InvalidTopologies()
    {
        yield return ["missing", "{}", 404];
        yield return ["transport", "{}", 503];
        yield return ["malformed", "{", 200];
        yield return ["null", "null", 200];
        yield return ["array", "[]", 200];
        yield return ["empty", "{}", 200];
        yield return ["wrong physical name (alias)", Valid().Replace("marketplace_services", "other_generation"), 200];
        var multi = JsonNode.Parse(Valid())!.AsObject(); multi["second_generation"] = multi["marketplace_services"]!.DeepClone();
        yield return ["multiple physical indices", multi.ToJsonString(), 200];
        foreach (var value in new string?[] { null, "", " ", "_na_" })
        {
            var node = JsonNode.Parse(Valid())!;
            node["marketplace_services"]!["settings"]!["index"]!["uuid"] = value;
            yield return ["invalid uuid: " + value, node.ToJsonString(), 200];
        }
        foreach (var path in new[] { "serviceId", "tenantId", "providerProfileId", "title", "description", "createdAt", "isActive", "trust", "serviceStats" })
        {
            var node = JsonNode.Parse(Valid())!;
            node["marketplace_services"]!["mappings"]!["properties"]!.AsObject().Remove(path);
            yield return ["missing " + path, node.ToJsonString(), 200];
        }
        foreach (var field in new[] { "serviceId", "tenantId", "providerProfileId", "title", "isActive" })
        {
            var node = JsonNode.Parse(Valid())!;
            node["marketplace_services"]!["mappings"]!["properties"]![field]!["type"] = "long";
            yield return ["wrong type " + field, node.ToJsonString(), 200];
        }
        foreach (var field in new[] { "trust", "serviceStats" })
        {
            var node = JsonNode.Parse(Valid())!;
            node["marketplace_services"]!["mappings"]!["properties"]![field]!["type"] = "nested";
            yield return ["wrong object type " + field, node.ToJsonString(), 200];
        }
        var wrongChild = JsonNode.Parse(Valid())!;
        wrongChild["marketplace_services"]!["mappings"]!["properties"]!["serviceStats"]!["properties"]!["totalRequests"]!["type"] = "text";
        yield return ["invalid statistics child", wrongChild.ToJsonString(), 200];
    }

    [Theory]
    [MemberData(nameof(InvalidTopologies))]
    public async Task BootstrapRejectsInvalidTopologyWithoutAnyMutation(string reason, string body, int status)
    {
        Assert.NotEmpty(reason);
        var transport = new RecordingConnection(body, status);
        var product = new Mock<IProductSearchIndexService>(MockBehavior.Strict);
        var service = Bootstrap(Client(transport), product.Object);
        Assert.False(await service.EnsureIndicesExistAsync());
        Assert.Single(transport.Requests);
        Assert.Equal("GET /marketplace_services", transport.Requests[0]);
        product.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task CompatiblePhysicalIndexIsInspectedNotCreatedOrModified()
    {
        var transport = new RecordingConnection(Valid(), 200);
        var result = await MarketplaceSearchTopology.InspectAsync(Client(transport));
        Assert.True(result.Compatible);
        Assert.Equal("fixture-uuid", result.PhysicalUuid);
        Assert.Equal(new[] { "GET /marketplace_services" }, transport.Requests);
    }

    [Fact]
    public async Task BootstrapForOtherIndicesStillWorksWithoutMarketplaceCreation()
    {
        var transport = new RecordingConnection(Valid(), 200);
        var product = new Mock<IProductSearchIndexService>(MockBehavior.Strict);
        product.Setup(p => p.EnsureIndexExistsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(true);
        Assert.True(await Bootstrap(Client(transport), product.Object).EnsureIndicesExistAsync());
        Assert.Equal("GET /marketplace_services", transport.Requests[0]);
        Assert.Equal(4, transport.Requests.Count);
        Assert.All(transport.Requests.Skip(1), r => Assert.StartsWith("HEAD ", r));
        product.Verify(p => p.EnsureIndexExistsAsync(It.IsAny<CancellationToken>()), Times.Once);
        product.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ConfiguredAffiliateNameCannotBootstrapMarketplaceThroughGenericHelper()
    {
        var transport = new RecordingConnection(Valid(), 200);
        var product = new Mock<IProductSearchIndexService>();
        product.Setup(p => p.EnsureIndexExistsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(true);
        var settings = new ElasticsearchSettings { Enabled = true, PublicAffiliateProductsIndexName = "marketplace_services" };
        Assert.False(await Bootstrap(Client(transport), product.Object, settings).EnsureIndicesExistAsync());
        Assert.Single(transport.Requests, r => r.EndsWith("/marketplace_services", StringComparison.Ordinal));
        Assert.DoesNotContain(transport.Requests, r => r.StartsWith("PUT ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DisabledBootstrapHasNoTransportOrProductSideEffects()
    {
        var transport = new RecordingConnection("{}", 503);
        var product = new Mock<IProductSearchIndexService>(MockBehavior.Strict);
        Assert.True(await Bootstrap(Client(transport), product.Object, new ElasticsearchSettings { Enabled = false }).EnsureIndicesExistAsync());
        Assert.Empty(transport.Requests);
        product.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task CancellationDoesNotContactSearch()
    {
        var transport = new RecordingConnection(Valid(), 200);
        using var ct = new CancellationTokenSource(); ct.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => MarketplaceSearchTopology.InspectAsync(Client(transport), ct.Token));
        Assert.Empty(transport.Requests);
    }

    [Fact]
    public async Task BothHealthBoundariesRejectMissingIndexInsteadOfReportingReady()
    {
        var transport = new RecordingConnection("{}", 404);
        var client = Client(transport);
        var infra = new MarketplaceElasticsearchHealthCheck(client, NullLogger<MarketplaceElasticsearchHealthCheck>.Instance);
        var api = new PointOfSale.API.HealthChecks.MarketplaceHealthCheck(new MarketplaceTelemetry(), client);
        Assert.Equal(Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Unhealthy, (await infra.CheckHealthAsync(new HealthCheckContext())).Status);
        Assert.Equal(Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Unhealthy, (await api.CheckHealthAsync(new HealthCheckContext())).Status);
        Assert.Equal(2, transport.Requests.Count);
        Assert.All(transport.Requests, r => Assert.Equal("GET /marketplace_services", r));
    }

    [Fact]
    public async Task CompetingGlobalRecoveryAttemptsCannotEnterOrdinaryRepairOrTouchDependencies()
    {
        await using var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().Options);
        var client = new Mock<IOpenSearchClient>(MockBehavior.Strict);
        var trust = new Mock<IProviderTrustService>(MockBehavior.Strict);
        ElasticsearchDriftReconciliationJob Job() => new(db, client.Object,
            NullLogger<ElasticsearchDriftReconciliationJob>.Instance, new MarketplaceTelemetry(), trust.Object);
        var errors = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ =>
            Assert.ThrowsAsync<InvalidOperationException>(() => Job().ExecuteAsync())));
        Assert.All(errors, error => Assert.Equal(MarketplaceSearchTopology.RecoveryBlockedCode, error.Message));
        client.VerifyNoOtherCalls(); trust.VerifyNoOtherCalls();
    }

    private static UnifiedSearchIndexService Bootstrap(IOpenSearchClient client, IProductSearchIndexService product,
        ElasticsearchSettings? settings = null) => new(client, product,
        Options.Create(settings ?? new ElasticsearchSettings { Enabled = true }), NullLogger<UnifiedSearchIndexService>.Instance);

    private static OpenSearchClient Client(RecordingConnection transport) => new(new ConnectionSettings(
        new SingleNodeConnectionPool(new Uri("https://fixture.invalid")), transport).MaximumRetries(0));

    private static string Valid() => """
        {"marketplace_services":{"settings":{"index":{"uuid":"fixture-uuid"}},"mappings":{"properties":{
        "serviceId":{"type":"keyword"},"tenantId":{"type":"keyword"},"providerProfileId":{"type":"keyword"},
        "title":{"type":"text"},"description":{"type":"text"},"createdAt":{"type":"date"},"isActive":{"type":"boolean"},
        "trust":{"properties":{"averageRating":{"type":"double"},"totalReviews":{"type":"integer"},"trustBadge":{"type":"keyword"}}},
        "serviceStats":{"properties":{"totalRequests":{"type":"integer"},"completedRequests":{"type":"integer"},"completionRate":{"type":"double"}}}
        }}}}
        """;

    private sealed class RecordingConnection(string body, int status, Func<RequestData, string>? responseBody = null) : InMemoryConnection
    {
        public List<string> Requests { get; } = [];
        public override Task<TResponse> RequestAsync<TResponse>(RequestData data, CancellationToken ct)
        {
            Requests.Add(data.Method + " " + data.Uri.AbsolutePath);
            return new InMemoryConnection(Encoding.UTF8.GetBytes(responseBody?.Invoke(data) ?? body), status).RequestAsync<TResponse>(data, ct);
        }
    }
}
