using System.Text;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using OpenSearch.Client;
using OpenSearch.Net;
using PointOfSale.Core.Entities;
using PointOfSale.Core.Enums;
using PointOfSale.Core.Events;
using PointOfSale.Core.DTOs.Marketplace;
using PointOfSale.Core.Interfaces;
using PointOfSale.Infrastructure.Consumers;
using PointOfSale.Infrastructure.Data;
using PointOfSale.Infrastructure.Services;
using PointOfSale.Infrastructure.Jobs;
using PointOfSale.Tests.Infrastructure;

namespace PointOfSale.Tests.Marketplace;

public sealed class ServiceTrustDeliveryFailureTests
{
    [Theory]
    [InlineData("active", true)]
    [InlineData("financially-locked", true)]
    [InlineData("tenant-disabled", false)]
    [InlineData("tenant-suspended", false)]
    [InlineData("tenant-deleted", false)]
    [InlineData("tenant-missing", false)]
    [InlineData("service-inactive", false)]
    [InlineData("service-deleted", false)]
    [InlineData("profile-inactive", false)]
    [InlineData("profile-deleted", false)]
    [InlineData("profile-foreign-tenant", false)]
    [InlineData("profile-wrong-id", false)]
    [InlineData("profile-missing", false)]
    [InlineData("vendor-suspended", false)]
    [InlineData("vendor-banned", false)]
    [InlineData("vendor-unknown", false)]
    [InlineData("vendor-other-role", false)]
    [InlineData("vendor-other-subject", false)]
    [InlineData("vendor-missing", false)]
    public void TargetedRepairVisibilityRequiresExactActiveSqlLineage(string state, bool visible)
    {
        Tenant? tenant = new() { Id = Guid.NewGuid(), Name = "fixture", Status = TenantStatus.Active, IsActive = true };
        MarketplaceProfile? profile = MarketplaceProfile.Create(tenant.Id, Guid.NewGuid(), "fixture");
        var service = Service.Create(tenant.Id, profile.Id, "fixture", "description");
        UserGovernanceProfile? vendor = new() { Id = Guid.NewGuid(), SubjectId = tenant.Id,
            Role = UserGovernanceRole.Vendor, Status = UserGovernanceStatus.Active, CreatedAtUtc = DateTime.UtcNow };
        switch (state)
        {
            case "financially-locked": vendor.Status = UserGovernanceStatus.FinanciallyLocked; break;
            case "tenant-disabled": tenant.IsActive = false; break;
            case "tenant-suspended": tenant.Status = TenantStatus.Suspended; break;
            case "tenant-deleted": tenant.DeletedAtUtc = DateTime.UtcNow; break;
            case "tenant-missing": tenant = null; break;
            case "service-inactive": service.Deactivate(); break;
            case "service-deleted": service.IsDeleted = true; break;
            case "profile-inactive": profile.Deactivate(); break;
            case "profile-deleted": profile.IsDeleted = true; break;
            case "profile-foreign-tenant": profile.TenantId = Guid.NewGuid(); break;
            case "profile-wrong-id": profile = MarketplaceProfile.Create(tenant.Id, Guid.NewGuid(), "other"); break;
            case "profile-missing": profile = null; break;
            case "vendor-suspended": vendor.Status = UserGovernanceStatus.Suspended; break;
            case "vendor-banned": vendor.Status = UserGovernanceStatus.Banned; break;
            case "vendor-unknown": vendor.Status = (UserGovernanceStatus)int.MaxValue; break;
            case "vendor-other-role": vendor.Role = UserGovernanceRole.Admin; break;
            case "vendor-other-subject": vendor.SubjectId = Guid.NewGuid(); break;
            case "vendor-missing": vendor = null; break;
        }
        // Actual predicate called after the production repair's SQL share locks.
        // No mock PostgreSQL/HTTP concurrency, write authority or live-provider
        // acceptance is implied by this pure predicate matrix.
        Assert.Equal(visible, ElasticsearchDriftReconciliationJob.IsLiveSqlLineage(service, tenant, profile, vendor));
    }

    [Theory]
    [InlineData("foreign-tenant")]
    [InlineData("foreign-profile")]
    public async Task PublicStatisticsRejectRequestRowsOutsideTheAdmittedServiceLineage(string poison)
    {
        var tenant = new Tenant { Id = Guid.NewGuid(), Name = "fixture", Status = TenantStatus.Active, IsActive = true };
        var scope = new TestTenantContext(tenant.Id);
        await using var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options, scope);
        var provider = Guid.NewGuid(); var profile = MarketplaceProfile.Create(tenant.Id, provider, "fixture");
        var otherProfile = MarketplaceProfile.Create(tenant.Id, Guid.NewGuid(), "other fixture");
        var service = Service.Create(tenant.Id, profile.Id, "fixture", "description");
        ServiceRequest Completed(Guid rowTenant, Guid rowProfile)
        {
            var request = ServiceRequest.Create(rowTenant, service.Id, Guid.NewGuid(), provider, rowProfile,
                "fixture", null, null, null, null, null);
            request.Accept(); request.MarkCompletedByProvider(); request.ConfirmCompletedByBuyer(); return request;
        }
        db.AddRange(tenant, profile, otherProfile, service, Completed(tenant.Id, profile.Id),
            Completed(poison == "foreign-tenant" ? Guid.NewGuid() : tenant.Id,
                poison == "foreign-profile" ? otherProfile.Id : profile.Id));
        await db.SaveChangesAsync();
        var trust = new Mock<IProviderTrustService>(MockBehavior.Strict);
        trust.Setup(t => t.GetTrustAggregateAsync(profile.Id, It.IsAny<CancellationToken>())).ReturnsAsync((ProviderTrustAggregate?)null);
        var search = new MarketplaceSearchService(CandidateClient(service), db, scope, trust.Object, NullLogger<MarketplaceSearchService>.Instance);
        var item = Assert.Single((await search.SearchServicesAsync(new MarketplaceSearchRequest())).Items);
        Assert.Equal(service.Id, item.ServiceId); Assert.Equal(1, item.TotalRequests); Assert.Equal(1, item.CompletedRequests);
        Assert.Equal(1m, item.CompletionRate);
    }

    [Theory]
    [InlineData("search", "active")]
    [InlineData("catalog", "active")]
    [InlineData("search", "inactive")]
    [InlineData("catalog", "inactive")]
    [InlineData("search", "deleted")]
    [InlineData("catalog", "deleted")]
    public async Task PublicSqlListingAndDetailRequireCurrentTenantActivity(string entry, string state)
    {
        var tenant = new Tenant { Id = Guid.NewGuid(), Name = "fixture", Status = TenantStatus.Active,
            IsActive = state != "inactive", DeletedAtUtc = state == "deleted" ? DateTime.UtcNow : null };
        var scope = new TestTenantContext(tenant.Id);
        await using var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options, scope);
        var profile = MarketplaceProfile.Create(tenant.Id, Guid.NewGuid(), "fixture");
        var service = Service.Create(tenant.Id, profile.Id, "fixture", "description");
        db.AddRange(tenant, profile, service); await db.SaveChangesAsync();
        if (entry == "catalog")
        {
            var catalog = new ServiceCatalogService(db, scope, Mock.Of<IEventPublisher>(MockBehavior.Strict), NullLogger<ServiceCatalogService>.Instance);
            var actual = await catalog.GetServiceByIdAsync(service.Id);
            if (state == "active") Assert.Equal(service.Id, Assert.IsType<Service>(actual).Id);
            else Assert.Null(actual);
        }
        else
        {
            // Candidate search data supplies identity only;actual public fields and
            // admission must come from the production SQL-composition method.
            var client = CandidateClient(service);
            var trust = new Mock<IProviderTrustService>(MockBehavior.Strict);
            trust.Setup(t => t.GetTrustAggregateAsync(profile.Id, It.IsAny<CancellationToken>())).ReturnsAsync((ProviderTrustAggregate?)null);
            var search = new MarketplaceSearchService(client, db, scope, trust.Object, NullLogger<MarketplaceSearchService>.Instance);
            var actual = await search.SearchServicesAsync(new MarketplaceSearchRequest());
            if (state == "active") Assert.Equal(service.Id, Assert.Single(actual.Items).ServiceId);
            else Assert.Empty(actual.Items);
            trust.Verify(t => t.GetTrustAggregateAsync(profile.Id, It.IsAny<CancellationToken>()), state == "active" ? Times.Once() : Times.Never());
        }
    }

    private static OpenSearchClient CandidateClient(Service service)
    {
        var body = System.Text.Json.JsonSerializer.Serialize(new { took = 1, timed_out = false,
            _shards = new { total = 1, successful = 1, failed = 0 },
            hits = new { total = new { value = 1, relation = "eq" }, max_score = 1,
                hits = new[] { new { _index = "marketplace_services", _id = service.Id.ToString(), _score = 1,
                    _source = new { serviceId = service.Id, tenantId = service.TenantId } } } } });
        return new OpenSearchClient(new ConnectionSettings(new SingleNodeConnectionPool(new Uri("https://fixture.invalid")),
            new InMemoryConnection(Encoding.UTF8.GetBytes(body))).MaximumRetries(0));
    }

    [Theory]
    [InlineData("create", "active")]
    [InlineData("update", "active")]
    [InlineData("activation", "active")]
    [InlineData("trust", "active")]
    [InlineData("completion", "active")]
    [InlineData("create", "inactive")]
    [InlineData("update", "inactive")]
    [InlineData("activation", "inactive")]
    [InlineData("trust", "inactive")]
    [InlineData("completion", "inactive")]
    [InlineData("create", "deleted")]
    [InlineData("update", "deleted")]
    [InlineData("activation", "deleted")]
    [InlineData("trust", "deleted")]
    [InlineData("completion", "deleted")]
    public async Task EveryLiveProjectionEntryRejectsInactiveOrDeletedTenantDespiteActiveStatus(string entry, string state)
    {
        // Local SQL-query/recording-transport admission proof only, not PostgreSQL
        // concurrency or real OpenSearch script/lifecycle certification.
        await using var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var tenant = new Tenant { Id = Guid.NewGuid(), Name = "fixture", Status = TenantStatus.Active,
            IsActive = state != "inactive", DeletedAtUtc = state == "deleted" ? DateTime.UtcNow : null };
        var profile = MarketplaceProfile.Create(tenant.Id, Guid.NewGuid(), "fixture");
        var service = Service.Create(tenant.Id, profile.Id, "fixture", "description");
        db.AddRange(tenant, profile, service); await db.SaveChangesAsync();
        var connection = new AdmissionConnection(service.Id);
        var client = new OpenSearchClient(new ConnectionSettings(new SingleNodeConnectionPool(new Uri("https://fixture.invalid")), connection)
            .DisableDirectStreaming().MaximumRetries(0));
        var consumer = new ServiceDiscoveryProjectionConsumer(client, db, NullLogger<ServiceDiscoveryProjectionConsumer>.Instance);
        switch (entry)
        {
            case "create": await consumer.Consume(Context(new ServiceCreatedEvent { ServiceId = service.Id, TenantId = tenant.Id })); break;
            case "update": await consumer.Consume(Context(new ServiceUpdatedEvent { ServiceId = service.Id, TenantId = tenant.Id })); break;
            case "activation": await consumer.Consume(Context(new ServiceActivationChangedEvent { ServiceId = service.Id, TenantId = tenant.Id, IsActive = true })); break;
            case "trust": await consumer.Consume(Context(new ProviderTrustUpdatedEvent { ProviderProfileId = profile.Id,
                TenantId = tenant.Id, AverageRating = 4m, TotalReviews = 1, OccurredAtUtc = DateTime.UtcNow, EventVersion = 1 })); break;
            case "completion": await consumer.Consume(Context(new ServiceRequestCompletedEvent { ServiceId = service.Id, TenantId = tenant.Id })); break;
            default: throw new ArgumentOutOfRangeException(nameof(entry));
        }
        if (state == "active") Assert.Contains(connection.Methods, method => method != "DELETE");
        else
        {
            Assert.DoesNotContain(connection.Methods, method => method != "DELETE");
            Assert.Equal(entry == "trust" ? 0 : 1, connection.Methods.Count);
        }
    }

    private static ConsumeContext<T> Context<T>(T message) where T : class
    {
        var context = new Mock<ConsumeContext<T>>();
        context.SetupGet(c => c.Message).Returns(message);
        return context.Object;
    }

    private sealed class AdmissionConnection(Guid serviceId) : InMemoryConnection
    {
        public List<string> Methods { get; } = [];
        public override Task<TResponse> RequestAsync<TResponse>(RequestData requestData, CancellationToken cancellationToken)
        {
            var method = requestData.Method.ToString(); Methods.Add(method);
            var result = method == "DELETE" ? "deleted" : "updated";
            var body = $"{{\"_index\":\"marketplace_services\",\"_id\":\"{serviceId:D}\",\"_version\":1,\"result\":\"{result}\",\"_shards\":{{\"total\":1,\"successful\":1,\"failed\":0}}}}";
            return new InMemoryConnection(Encoding.UTF8.GetBytes(body), 200).RequestAsync<TResponse>(requestData, cancellationToken);
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task TrustDeliveryDoesNotAcknowledgePartialSearchFailure(int failureAt)
    {
        await using var db=new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var tenant=Guid.NewGuid();
        var profile=MarketplaceProfile.Create(tenant,Guid.NewGuid(),"fixture");
        var service=Service.Create(tenant,profile.Id,"fixture","description");
        db.AddRange(new Tenant { Id=tenant,Name="fixture",Status=TenantStatus.Active,IsActive=true },profile,service);
        await db.SaveChangesAsync();
        var connection=new SequenceConnection(failureAt);
        var client=new OpenSearchClient(new ConnectionSettings(new SingleNodeConnectionPool(new Uri("https://fixture.invalid")),connection)
            .DisableDirectStreaming().MaximumRetries(0));
        var context=new Mock<ConsumeContext<ProviderTrustUpdatedEvent>>();
        context.SetupGet(c=>c.Message).Returns(new ProviderTrustUpdatedEvent { ProviderProfileId=profile.Id,
            TenantId=tenant,AverageRating=4m,TotalReviews=1,OccurredAtUtc=DateTime.UtcNow,EventVersion=1 });
        var consumer=new ServiceDiscoveryProjectionConsumer(client,db,NullLogger<ServiceDiscoveryProjectionConsumer>.Instance);
        var error=await Assert.ThrowsAsync<InvalidOperationException>(()=>consumer.Consume(context.Object));
        Assert.Contains("delivery must be retried",error.Message,StringComparison.Ordinal);
        Assert.Equal(failureAt,connection.Calls);
    }

    private sealed class SequenceConnection(int failureAt) : InMemoryConnection
    {
        public int Calls { get; private set; }
        public override Task<TResponse> RequestAsync<TResponse>(RequestData requestData,CancellationToken cancellationToken)
        {
            Calls++;
            var failure=Calls==failureAt;
            var body=failure ? "{\"error\":{\"type\":\"unavailable_shards_exception\",\"reason\":\"controlled fixture failure\"},\"status\":503}"
                : "{\"_index\":\"marketplace_services\",\"_id\":\"fixture\",\"_version\":1,\"result\":\"updated\",\"_shards\":{\"total\":1,\"successful\":1,\"failed\":0}}";
            return new InMemoryConnection(Encoding.UTF8.GetBytes(body),failure?503:200).RequestAsync<TResponse>(requestData,cancellationToken);
        }
    }
}
