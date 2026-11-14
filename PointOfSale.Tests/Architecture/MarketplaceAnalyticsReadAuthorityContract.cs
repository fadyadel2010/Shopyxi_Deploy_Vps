using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using OpenSearch.Client;
using PointOfSale.API.Controllers;
using PointOfSale.API.Services;
using PointOfSale.Core.Entities;
using PointOfSale.Core.Enums;
using PointOfSale.Core.Interfaces;
using PointOfSale.Infrastructure.Data;
using PointOfSale.Infrastructure.Instrumentation;
using PointOfSale.Tests.Infrastructure;

namespace PointOfSale.Tests.Architecture;

// Executes controller predicates and result composition only. EF InMemory is intentional:
// this is NOT PostgreSQL translation, concurrency, provider, or cross-store certification.
internal static class MarketplaceAnalyticsReadAuthorityContract
{
    internal static async Task AssertReadAuthorityAsync()
    {
        foreach(var scenario in new[] { "valid", "inactive-tenant", "suspended-tenant", "deleted-tenant", "missing-tenant", "governance-suspended", "governance-banned", "inactive-profile", "deleted-profile", "foreign-profile" })
        {
            var tenantId=Guid.NewGuid();var userId=Guid.NewGuid();
            await using var db=new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options,new TestTenantContext(tenantId));
            var tenant=new Tenant { Id=tenantId,Name="Synthetic analytics authority",Currency="EGP",IsActive=true,Status=TenantStatus.Active };
            if(scenario=="inactive-tenant")tenant.IsActive=false;
            if(scenario=="suspended-tenant")tenant.Status=TenantStatus.Suspended;
            if(scenario=="deleted-tenant")tenant.DeletedAtUtc=DateTime.UtcNow;
            var profile=MarketplaceProfile.Create(scenario=="foreign-profile" ? Guid.NewGuid() : tenantId,userId,"Synthetic provider");
            if(scenario=="inactive-profile")profile.Deactivate();
            if(scenario=="deleted-profile")profile.IsDeleted=true;
            if(scenario!="missing-tenant")db.Tenants.Add(tenant);
            if(scenario is "governance-suspended" or "governance-banned")db.UserGovernanceProfiles.Add(new UserGovernanceProfile
            {
                Id=Guid.NewGuid(),SubjectId=tenantId,Role=UserGovernanceRole.Vendor,
                Status=scenario=="governance-banned" ? UserGovernanceStatus.Banned : UserGovernanceStatus.Suspended,
                CreatedAtUtc=DateTime.UtcNow
            });
            var service=Service.Create(tenantId,profile.Id,"Owned service","Synthetic");
            var otherService=Service.Create(tenantId,Guid.NewGuid(),"Other provider","Synthetic");
            var foreignService=Service.Create(Guid.NewGuid(),profile.Id,"Foreign tenant","Synthetic");
            var deletedService=Service.Create(tenantId,profile.Id,"Deleted","Synthetic");deletedService.IsDeleted=true;
            db.AddRange(service,otherService,foreignService,deletedService);
            var completed=Request(tenantId,userId,profile.Id,service.Id,true);
            var foreign=Request(Guid.NewGuid(),userId,profile.Id,service.Id,true);
            var otherProfile=Request(tenantId,userId,Guid.NewGuid(),service.Id,true);
            var otherUser=Request(tenantId,Guid.NewGuid(),profile.Id,service.Id,true);
            var pending=Request(tenantId,userId,profile.Id,service.Id,false);
            var deleted=Request(tenantId,userId,profile.Id,service.Id,true);deleted.IsDeleted=true;
            db.AddRange(completed,foreign,otherProfile,otherUser,pending,deleted);
            await db.SaveChangesAsync();
            var profiles=new Mock<IMarketplaceProfileService>(MockBehavior.Strict);
            profiles.Setup(p=>p.GetProfileByUserIdAsync(userId,It.IsAny<CancellationToken>())).ReturnsAsync(profile);
            var trust=new Mock<IProviderTrustService>(MockBehavior.Strict);
            if(scenario=="valid")trust.Setup(t=>t.GetTrustAggregateAsync(profile.Id,It.IsAny<CancellationToken>()))
                .ReturnsAsync(ProviderTrustAggregate.FromSqlReviewTruth(tenantId,profile.Id,2,9,1,1,0,0,0,null,0,0,0,0m));
            var search=new Mock<IMarketplaceSearchService>(MockBehavior.Strict);
            var index=new Mock<IOpenSearchClient>(MockBehavior.Strict);
            var controller=new MarketplaceController(search.Object,profiles.Object,
                Mock.Of<IServiceCatalogService>(MockBehavior.Strict),Mock.Of<IServiceRequestService>(MockBehavior.Strict),
                Mock.Of<IServiceReviewService>(MockBehavior.Strict),Mock.Of<IChatService>(MockBehavior.Strict),trust.Object,
                Mock.Of<IFileValidationService>(MockBehavior.Strict),Mock.Of<IR2StorageService>(MockBehavior.Strict),db,index.Object,
                Mock.Of<IWebHostEnvironment>(MockBehavior.Strict),new MarketplaceTelemetry(),NullLogger<MarketplaceController>.Instance,
                Mock.Of<IMarketplaceSearchRepairAuthority>(MockBehavior.Strict))
            {
                ControllerContext=new ControllerContext { HttpContext=new DefaultHttpContext { User=new ClaimsPrincipal(new ClaimsIdentity(
                    new[] {new Claim(ClaimTypes.NameIdentifier,userId.ToString()),new Claim("tenant_id",tenantId.ToString())},"Synthetic")) } }
            };
            var result=await controller.GetMyProfileStats();
            if(scenario=="valid")
            {
                var json=JsonSerializer.SerializeToElement(Assert.IsType<OkObjectResult>(result).Value);
                Assert.Equal(tenantId,json.GetProperty("tenantId").GetGuid());
                Assert.Equal(profile.Id,json.GetProperty("providerProfileId").GetGuid());
                Assert.Equal(1,json.GetProperty("servicesOffered").GetInt32());
                Assert.Equal(1,json.GetProperty("completedRequests").GetInt32());
                Assert.Equal(4.5m,json.GetProperty("averageRating").GetDecimal());
                Assert.Equal(2,json.GetProperty("totalReviews").GetInt32());
                trust.Verify(t=>t.GetTrustAggregateAsync(profile.Id,It.IsAny<CancellationToken>()),Times.Once);
            }
            else if(scenario=="foreign-profile")Assert.IsType<NotFoundObjectResult>(result);
            else Assert.Equal(StatusCodes.Status403Forbidden,Assert.IsType<ObjectResult>(result).StatusCode);
            trust.VerifyNoOtherCalls();search.VerifyNoOtherCalls();index.VerifyNoOtherCalls();
        }
    }

    private static ServiceRequest Request(Guid tenant,Guid provider,Guid profile,Guid service,bool complete)
    {
        var request=ServiceRequest.Create(tenant,service,Guid.NewGuid(),provider,profile,"Synthetic",null,null,null,null,null);
        if(complete){request.Accept();request.MarkCompletedByProvider();request.ConfirmCompletedByBuyer();}
        return request;
    }
}
