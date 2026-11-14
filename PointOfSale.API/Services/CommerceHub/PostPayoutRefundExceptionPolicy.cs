namespace PointOfSale.API.Services.CommerceHub;

/// <summary>
/// Capability description, not a refund entitlement or an execution authorization.
/// Existing reversal accounting does not authorize creation of an out-of-window request.
/// Deliberately has no configuration switch: activation requires an approved domain contract
/// and a separately reviewed initiation/review lifecycle, not an operator override.
/// </summary>
public static class PostPayoutRefundExceptionPolicy
{
    public const string RequestType = "PostPayoutException";
    public const string BlockerCode = "POST_PAYOUT_EXCEPTION_POLICY_NOT_APPROVED";

    public static PostPayoutRefundExceptionCapability Describe(PointOfSale.API.Configuration.RefundSettings? settings = null) => new(
        RequestType,
        CanInitiate: false,
        BlockerCode,
        PolicyReference: null,
        RequiredPolicyDecisions:
        [
            "EligibleReasonsAndTimeLimits",
            "RequiredEvidenceAndReferenceAuthority",
            "InitiatorAndIndependentReviewerAuthority",
            "VendorRecoveryAndFundingResponsibility"
        ],
        CustomerRefundRequestPolicy.PolicyVersion,
        CustomerRefundRequestPolicy.StandardWindowDays(settings ?? new()),
        CustomerRefundRequestPolicy.DefectWindowDays(settings ?? new()));
}

public sealed record PostPayoutRefundExceptionCapability(
    string RequestType,
    bool CanInitiate,
    string BlockerCode,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.Never)] string? PolicyReference,
    IReadOnlyList<string> RequiredPolicyDecisions,
    string OrdinaryClaimPolicyVersion,
    int StandardReturnWindowDays,
    int DefectClaimWindowDays);
