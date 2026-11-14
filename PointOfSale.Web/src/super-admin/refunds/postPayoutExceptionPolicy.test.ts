import { beforeEach, describe, expect, it, vi } from 'vitest'
import { getPostPayoutExceptionPolicy, requireBlockedExceptionPolicy } from './postPayoutExceptionPolicy'

const api = vi.hoisted(() => ({ get: vi.fn() }))
vi.mock('../core/superAdminApiClient', () => ({ superAdminApiClient: api }))

const blocked = {
    requestType: 'PostPayoutException', canInitiate: false,
    blockerCode: 'POST_PAYOUT_EXCEPTION_POLICY_NOT_APPROVED', policyReference: null,
    requiredPolicyDecisions: ['EligibleReasonsAndTimeLimits'],
    ordinaryClaimPolicyVersion: 'SHOPIXY-MARKETPLACE-14-30-20261029', standardReturnWindowDays: 14, defectClaimWindowDays: 30,
}

describe('post-payout exception policy discovery', () => {
    beforeEach(() => vi.resetAllMocks())
    it('uses only the read-only Super Admin policy route', async () => {
        api.get.mockResolvedValue({ data: blocked })
        expect(await getPostPayoutExceptionPolicy()).toEqual(blocked)
        expect(api.get).toHaveBeenCalledExactlyOnceWith('/refunds/post-payout-exception-policy')
    })
    it.each([null, {}, { ...blocked, canInitiate: true }, { ...blocked, canInitiate: 'false' },
        { ...blocked, requestType: 'Customer' }, { ...blocked, blockerCode: 'APPROVED' },
        { ...blocked, policyReference: 'invented-policy' }, { ...blocked, requiredPolicyDecisions: [] },
        { ...blocked, requiredPolicyDecisions: [null] }])('fails closed for unsupported policy %j', (value) => {
        expect(() => requireBlockedExceptionPolicy(value)).toThrow('Unsupported post-payout exception policy response.')
    })
})
