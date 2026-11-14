import { afterEach, beforeEach, describe, expect, it } from 'vitest'
import { marketplaceQueryClient, queryClient, superAdminQueryClient } from '../../queryClient'
import { useSuperAdminAuthStore } from './superAdminAuthStore'
import type { SuperAdminSessionResponse } from './superAdminAuth.types'

const session: SuperAdminSessionResponse = {
    superAdminId: '11111111-1111-4111-8111-111111111111', email: 'first@example.invalid',
    role: 'SuperAdmin', tokenType: 'Bearer', tokenVersion: 1,
    expiresAtUtc: '2099-01-01T00:00:00Z', refreshTokenExpiresAtUtc: '2099-01-02T00:00:00Z',
    navigationCapabilities: ['super-admin.finance.view', 'super-admin.users.govern'],
}
const key = ['super-admin', 'marketplace-governance', 'dashboard']

describe('Super Admin store actor and capability cache isolation', () => {
    beforeEach(() => {
        useSuperAdminAuthStore.getState().logout({ broadcast: false })
        queryClient.clear(); marketplaceQueryClient.clear()
    })
    afterEach(() => {
        useSuperAdminAuthStore.getState().logout({ broadcast: false })
        queryClient.clear(); marketplaceQueryClient.clear()
    })

    it.each(['setSession', 'hydrateSession'] as const)('%s purges prior actor data before the new actor is observable without disturbing POS caches', (operation) => {
        useSuperAdminAuthStore.getState().setSession(session)
        superAdminQueryClient.setQueryData(key, { secret: 'prior actor' })
        queryClient.setQueryData(['pos'], 'POS draft')
        marketplaceQueryClient.setQueryData(['marketplace'], 'Marketplace state')
        const nextId = '22222222-2222-4222-8222-222222222222'
        let observedOldData: unknown = 'not notified'
        const unsubscribe = useSuperAdminAuthStore.subscribe((state) => {
            if (state.user?.superAdminId === nextId) observedOldData = superAdminQueryClient.getQueryData(key)
        })
        try {
            useSuperAdminAuthStore.getState()[operation]({ ...session, superAdminId: nextId, email: 'next@example.invalid' })
        } finally { unsubscribe() }
        expect(observedOldData).toBeUndefined()
        expect(superAdminQueryClient.getQueryData(key)).toBeUndefined()
        expect(queryClient.getQueryData(['pos'])).toBe('POS draft')
        expect(marketplaceQueryClient.getQueryData(['marketplace'])).toBe('Marketplace state')
    })

    it('purges cached privileged data when the same actor loses a capability', () => {
        useSuperAdminAuthStore.getState().setSession(session)
        superAdminQueryClient.setQueryData(key, { secret: 'privileged data' })
        useSuperAdminAuthStore.getState().hydrateSession({ ...session, navigationCapabilities: ['super-admin.finance.view'] })
        expect(superAdminQueryClient.getQueryData(key)).toBeUndefined()
        expect(useSuperAdminAuthStore.getState().user?.navigationCapabilities).toEqual(['super-admin.finance.view'])
    })

    it('preserves current actor query data across an unchanged-identity session refresh', () => {
        useSuperAdminAuthStore.getState().setSession(session)
        superAdminQueryClient.setQueryData(key, { accepted: true })
        useSuperAdminAuthStore.getState().setSession({ ...session, tokenVersion: 2 })
        expect(superAdminQueryClient.getQueryData(key)).toEqual({ accepted: true })
    })
})
