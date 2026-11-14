import { superAdminApiClient } from '../core/superAdminApiClient'

export interface PostPayoutExceptionPolicy {
    requestType: 'PostPayoutException'
    canInitiate: false
    blockerCode: 'POST_PAYOUT_EXCEPTION_POLICY_NOT_APPROVED'
    policyReference: null
    requiredPolicyDecisions: string[]
    ordinaryClaimPolicyVersion: string
    standardReturnWindowDays: number
    defectClaimWindowDays: number
}

// This version implements policy discovery only. A future enabled response must
// not silently turn this UI into an initiation or execution surface.
export function requireBlockedExceptionPolicy(value: unknown): PostPayoutExceptionPolicy {
    const policy = value as Partial<PostPayoutExceptionPolicy> | null
    if (policy?.requestType !== 'PostPayoutException'
        || policy.canInitiate !== false
        || policy.blockerCode !== 'POST_PAYOUT_EXCEPTION_POLICY_NOT_APPROVED'
        || policy.policyReference !== null
        || policy.ordinaryClaimPolicyVersion !== 'SHOPIXY-MARKETPLACE-14-30-20261029'
        || !Number.isInteger(policy.standardReturnWindowDays) || (policy.standardReturnWindowDays ?? 0) < 14
        || !Number.isInteger(policy.defectClaimWindowDays) || (policy.defectClaimWindowDays ?? 0) < Math.max(30, policy.standardReturnWindowDays ?? 0)
        || !Array.isArray(policy.requiredPolicyDecisions)
        || policy.requiredPolicyDecisions.length === 0
        || !policy.requiredPolicyDecisions.every((item) => typeof item === 'string' && item.trim().length > 0)) {
        throw new Error('Unsupported post-payout exception policy response.')
    }
    return policy as PostPayoutExceptionPolicy
}

export async function getPostPayoutExceptionPolicy(): Promise<PostPayoutExceptionPolicy> {
    const response = await superAdminApiClient.get<unknown>('/refunds/post-payout-exception-policy')
    return requireBlockedExceptionPolicy(response.data)
}
