using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using PointOfSale.API.Configuration;
using PointOfSale.API.Services.CommerceHub;
using PointOfSale.API.SuperAdmin.Controllers;
using PointOfSale.API.SuperAdmin.Services;
using PointOfSale.Core.Enums;

namespace PointOfSale.Tests.CommerceHub;

public class P520GExceptionPolicyTests
{
    [Fact]
    public void Exception_has_explicit_type_but_no_authorization_or_invented_policy()
    {
        var capability = PostPayoutRefundExceptionPolicy.Describe();
        Assert.Equal("PostPayoutException", capability.RequestType);
        Assert.False(capability.CanInitiate);
        Assert.Null(capability.PolicyReference);
        Assert.Equal("POST_PAYOUT_EXCEPTION_POLICY_NOT_APPROVED", capability.BlockerCode);
        Assert.Equal(new[] { "EligibleReasonsAndTimeLimits", "RequiredEvidenceAndReferenceAuthority",
            "InitiatorAndIndependentReviewerAuthority", "VendorRecoveryAndFundingResponsibility" }, capability.RequiredPolicyDecisions);
    }

    [Fact]
    public void Policy_discovery_is_read_only_and_inherits_super_admin_authority()
    {
        var method = typeof(SuperAdminRefundsController).GetMethod(nameof(SuperAdminRefundsController.GetPostPayoutExceptionPolicy))!;
        Assert.Equal("post-payout-exception-policy", method.GetCustomAttribute<HttpGetAttribute>()!.Template);
        Assert.Empty(method.GetParameters());
        Assert.Null(method.GetCustomAttribute<AllowAnonymousAttribute>());
        Assert.NotNull(typeof(SuperAdminBaseController).GetCustomAttribute<AuthorizeAttribute>());
        var service = new Mock<ISuperAdminRefundsService>(MockBehavior.Strict);
        var controller = new SuperAdminRefundsController(service.Object, NullLogger<SuperAdminRefundsController>.Instance)
        { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() } };
        Assert.IsType<OkObjectResult>(controller.GetPostPayoutExceptionPolicy());
        service.VerifyNoOtherCalls();
    }

    [Fact]
    public void Policy_descriptions_do_not_share_mutable_decision_lists()
    {
        var first = PostPayoutRefundExceptionPolicy.Describe();
        var second = PostPayoutRefundExceptionPolicy.Describe();
        Assert.NotSame(first.RequiredPolicyDecisions, second.RequiredPolicyDecisions);
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, false)]
    public void Ordinary_window_boundary_remains_inclusive_then_expires(int secondsAfterBoundary, bool allowed)
    {
        var delivered = new DateTime(2026, 10, 29, 0, 0, 0, DateTimeKind.Utc);
        var eligibility = CustomerRefundRequestPolicy.Evaluate(CommerceHubOrderItemStatus.Delivered,
            delivered, 2, [], new RefundSettings(), delivered.AddDays(14).AddSeconds(secondsAfterBoundary), RefundReasonCode.Other);
        Assert.Equal(allowed, eligibility.CanRefund);
        if (!allowed) Assert.Equal(CustomerRefundRequestPolicy.RefundWindowExpiredDisabledReason, eligibility.DisabledReason);
        Assert.False(PostPayoutRefundExceptionPolicy.Describe().CanInitiate);
    }

    [Fact]
    public void Existing_reason_codes_are_complaints_not_exception_entitlements()
    {
        Assert.Equal(new[] { "DamagedItem", "NotAsDescribed", "WrongItem", "MissingParts", "QualityIssue", "Other" }, Enum.GetNames<RefundReasonCode>());
        Assert.False(PostPayoutRefundExceptionPolicy.Describe().CanInitiate);
    }
}
