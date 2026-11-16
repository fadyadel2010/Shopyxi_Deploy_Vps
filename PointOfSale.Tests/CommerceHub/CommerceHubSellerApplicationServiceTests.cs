using Microsoft.EntityFrameworkCore;
using Moq;
using PointOfSale.API.DTOs.CommerceHub;
using PointOfSale.API.Services.CommerceHub;
using PointOfSale.API.Services.Governance;
using PointOfSale.API.SuperAdmin.DTOs;
using PointOfSale.API.SuperAdmin.Services;
using PointOfSale.Core.Entities;
using PointOfSale.Core.Enums;
using PointOfSale.Infrastructure.Data;
using PointOfSale.Tests.Infrastructure;

namespace PointOfSale.Tests.CommerceHub;

public sealed class CommerceHubSellerApplicationServiceTests
{
    [Fact]
    public async Task Rejected_application_can_be_resubmitted_without_losing_history()
    {
        var fixture = await CreateFixtureAsync();
        await using var db = fixture.Db;
        var first = await fixture.Service.SubmitAsync(fixture.TenantId, fixture.UserId, ValidRequest());

        Assert.Equal("PendingReview", first.Status);
        var pending = Assert.Single(first.History);

        var duplicate = await Assert.ThrowsAsync<CommerceHubSellerApplicationException>(() =>
            fixture.Service.SubmitAsync(fixture.TenantId, fixture.UserId, ValidRequest()));
        Assert.Equal("SELLER_APPLICATION_ALREADY_PENDING", duplicate.Code);

        const string reason = "Please clarify the main product categories before resubmitting.";
        var rejected = await fixture.Service.RejectAsync(
            pending.Id,
            Guid.NewGuid(),
            "reviewer@shopixy.test",
            reason);
        Assert.Equal("Rejected", rejected.Application.Status);
        Assert.Equal(reason, rejected.Application.RejectionReason);

        var resubmitted = await fixture.Service.SubmitAsync(
            fixture.TenantId,
            fixture.UserId,
            ValidRequest("Updated description for the corrected seller application."));

        Assert.Equal("PendingReview", resubmitted.Status);
        Assert.Equal(2, resubmitted.History.Count);
        Assert.Equal(2, resubmitted.CurrentApplication!.Revision);
        Assert.Contains(resubmitted.History, item => item.Revision == 1 && item.RejectionReason == reason);
    }

    [Fact]
    public async Task Approval_uses_existing_governance_authority_and_records_reviewer()
    {
        var fixture = await CreateFixtureAsync();
        await using var db = fixture.Db;
        var submitted = await fixture.Service.SubmitAsync(fixture.TenantId, fixture.UserId, ValidRequest());
        var applicationId = submitted.CurrentApplication!.Id;
        var reviewerId = Guid.NewGuid();

        var approved = await fixture.Service.ApproveAsync(
            applicationId,
            reviewerId,
            "owner@shopixy.test");

        Assert.Equal("Approved", approved.Application.Status);
        Assert.Equal(reviewerId, approved.Application.ReviewedBySuperAdminId);
        Assert.NotNull(approved.Application.ReviewedAtUtc);
        fixture.Governance.Verify(service => service.SetVendorMarketplaceApprovalAsync(
            fixture.TenantId,
            true,
            It.Is<string>(reason => reason.Contains(submitted.CurrentApplication.ReferenceNumber)),
            reviewerId,
            "owner@shopixy.test",
            It.IsAny<CancellationToken>()), Times.Once);

        var replay = await Assert.ThrowsAsync<CommerceHubSellerApplicationException>(() =>
            fixture.Service.ApproveAsync(applicationId, reviewerId, "owner@shopixy.test"));
        Assert.Equal("SELLER_APPLICATION_ALREADY_RESOLVED", replay.Code);
    }

    [Fact]
    public async Task Direct_submission_rejects_an_actor_outside_the_merchant_tenant()
    {
        var fixture = await CreateFixtureAsync();
        await using var db = fixture.Db;

        var exception = await Assert.ThrowsAsync<CommerceHubSellerApplicationException>(() =>
            fixture.Service.SubmitAsync(fixture.TenantId, Guid.NewGuid(), ValidRequest()));

        Assert.Equal("MERCHANT_ACTOR_NOT_FOUND", exception.Code);
        Assert.Empty(await db.CommerceHubSellerApplications.ToListAsync());
    }

    [Fact]
    public async Task Direct_submission_rejects_existing_marketplace_approval_without_an_application_row()
    {
        var fixture = await CreateFixtureAsync(marketplaceApproved: true);
        await using var db = fixture.Db;

        var exception = await Assert.ThrowsAsync<CommerceHubSellerApplicationException>(() =>
            fixture.Service.SubmitAsync(fixture.TenantId, fixture.UserId, ValidRequest()));

        Assert.Equal("SELLER_APPLICATION_ALREADY_APPROVED", exception.Code);
        Assert.Empty(await db.CommerceHubSellerApplications.ToListAsync());
    }

    [Fact]
    public void Domain_transition_rejects_a_second_review_decision()
    {
        var application = CommerceHubSellerApplication.Submit(
            Guid.NewGuid(),
            "CHS-20261116-ABCDEF1234",
            1,
            "Test Store",
            "Home",
            "A sufficiently detailed business description.",
            "Test Owner",
            "+201000000000",
            "owner@example.test",
            "[\"Home\"]",
            null,
            Guid.NewGuid(),
            DateTime.UtcNow);

        application.Reject(Guid.NewGuid(), "reviewer@example.test", "The category details need to be clarified.", DateTime.UtcNow);

        Assert.Throws<InvalidOperationException>(() =>
            application.Approve(Guid.NewGuid(), "other@example.test", DateTime.UtcNow));
    }

    private static CommerceHubSellerApplicationSubmitRequest ValidRequest(string? description = null) => new()
    {
        StoreDisplayName = "Ahram QA Store",
        BusinessCategory = "Home and gifts",
        BusinessDescription = description ?? "A merchant offering practical home products and locally sourced gifts.",
        ContactName = "QA Owner",
        ContactPhone = "+201000000000",
        ContactEmail = "qa-owner@example.test",
        ProductCategories = new[] { "Home", "Gifts" },
        StorefrontUrl = "https://qa-store.example.test"
    };

    private static async Task<Fixture> CreateFixtureAsync(bool marketplaceApproved = false)
    {
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var db = new ApplicationDbContext(options, new TestTenantContext(tenantId));
        db.Tenants.Add(new Tenant
        {
            Id = tenantId,
            Name = "Ahram QA Store",
            Slug = "ahram-qa",
            ContactEmail = "qa-owner@example.test",
            StorefrontBaseUrl = "https://ahram-qa.example.test",
            Status = TenantStatus.Active,
            IsActive = true
        });
        db.Users.Add(new User
        {
            Id = userId,
            TenantId = tenantId,
            Username = "qa-owner",
            Email = "qa-owner@example.test",
            FirstName = "QA",
            LastName = "Owner",
            Phone = "+201000000000",
            Role = UserRole.Owner,
            IsActive = true
        });
        db.UserGovernanceProfiles.Add(new UserGovernanceProfile
        {
            Id = Guid.NewGuid(),
            SubjectId = tenantId,
            Role = UserGovernanceRole.Vendor,
            Status = UserGovernanceStatus.Active,
            IsKycVerified = true,
            IsMarketplaceApproved = marketplaceApproved,
            CreatedAtUtc = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        var governance = new Mock<IUserGovernanceService>();
        governance
            .Setup(service => service.SetVendorMarketplaceApprovalAsync(
                It.IsAny<Guid>(),
                It.IsAny<bool>(),
                It.IsAny<string>(),
                It.IsAny<Guid>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SuperAdminGovernedUserDetailsDto
            {
                Id = tenantId,
                IsKycVerified = true,
                IsMarketplaceApproved = true
            });
        var audit = new Mock<ISuperAdminAuditService>();
        audit
            .Setup(service => service.LogActionAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<bool>(),
                It.IsAny<object?>(),
                It.IsAny<string?>(),
                It.IsAny<Guid?>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        return new Fixture(
            db,
            new CommerceHubSellerApplicationService(db, governance.Object, audit.Object),
            governance,
            tenantId,
            userId);
    }

    private sealed record Fixture(
        ApplicationDbContext Db,
        CommerceHubSellerApplicationService Service,
        Mock<IUserGovernanceService> Governance,
        Guid TenantId,
        Guid UserId);
}
