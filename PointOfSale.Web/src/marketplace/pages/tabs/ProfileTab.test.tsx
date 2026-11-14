import { act, cleanup, render, screen, waitFor } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { ProfileTab } from './ProfileTab'
import { marketplaceKeys } from '../../queryKeys'

const api = vi.hoisted(() => ({ getMyProfile: vi.fn(), getMyProfileStats: vi.fn(), createProfile: vi.fn(), updateProfile: vi.fn() }))
const actor = vi.hoisted(() => ({ scope: '' }))
vi.mock('../../api/marketplaceApi', () => ({ marketplaceApi: api }))
vi.mock('../../authority', async (original) => ({
    ...await original<typeof import('../../authority')>(),
    useMarketplaceAuthorityScope: () => actor.scope,
}))
vi.mock('react-i18next', () => ({ useTranslation: () => ({ t: (key: string) => key }) }))
vi.mock('../../../hooks/useFormatting', () => ({ useFormatting: () => ({ formatNumber: (value: number) => `metric:${value}`, isRTL: false }) }))
vi.mock('../../components/ProfileFormModal', () => ({ ProfileFormModal: () => null }))

const tenantId = '11111111-1111-4111-8111-111111111111'
const userId = '22222222-2222-4222-8222-222222222222'
const profileId = '33333333-3333-4333-8333-333333333333'
const profile = { hasMarketplaceProfile: true, profileId, displayName: 'Current provider', isActive: true }
const stats = { tenantId, providerUserId: userId, providerProfileId: profileId,
    servicesOffered: 3, completedRequests: 20, averageRating: 4.75, totalReviews: 20, trustBadge: 'Trusted' }
const cardsLabel = 'servicesMarketplace.profile.stats.servicesOffered'
let client: QueryClient

function mount() {
    return render(<QueryClientProvider client={client}><ProfileTab /></QueryClientProvider>)
}
function deferred<T>() {
    let resolve!: (value: T) => void
    let reject!: (reason: unknown) => void
    const promise = new Promise<T>((yes, no) => { resolve = yes; reject = no })
    return { promise, resolve, reject }
}
async function expectAcceptedCards() {
    expect(await screen.findByText(cardsLabel)).toBeInTheDocument()
    expect(screen.getByText('metric:3')).toBeInTheDocument()
    expect(screen.getByText('metric:4.75')).toBeInTheDocument()
    expect(screen.getByText('servicesMarketplace.badges.trusted')).toBeInTheDocument()
}

beforeEach(() => {
    vi.resetAllMocks()
    actor.scope = `${tenantId}:${userId}`
    // Deliberately retain old cache to prove rejection is not an accidental GC effect.
    client = new QueryClient({ defaultOptions: { queries: { retry: false, gcTime: Infinity } } })
    api.getMyProfile.mockResolvedValue(profile)
    api.getMyProfileStats.mockResolvedValue(stats)
})
afterEach(() => { cleanup(); client.clear() })

describe('ProfileTab actual query hooks and dashboard composition', () => {
    it('renders accepted backend cards through the dashboard wrapper using actor-scoped cache keys', async () => {
        mount(); await expectAcceptedCards()
        expect(client.getQueryData(marketplaceKeys.myProfileStats(actor.scope))).toEqual(stats)
        expect(api.getMyProfile).toHaveBeenCalledTimes(1)
        expect(api.getMyProfileStats).toHaveBeenCalledTimes(1)
        expect(api.createProfile).not.toHaveBeenCalled()
        expect(api.updateProfile).not.toHaveBeenCalled()
    })

    it.each([
        ['foreign tenant', { tenantId: 'foreign-tenant' }],
        ['foreign actor', { providerUserId: 'foreign-actor' }],
        ['foreign profile', { providerProfileId: 'foreign-profile' }],
        ['missing profile identity', { providerProfileId: undefined }],
        ['negative count', { servicesOffered: -1 }],
        ['inconsistent review count', { totalReviews: 21 }],
        ['invalid rating', { averageRating: 8 }],
        ['inconsistent badge', { trustBadge: 'New' }],
        ['unsupported chart', { chart: [{ value: 999 }] }],
        ['unsupported history', { historicalMetrics: [{ value: 999 }] }],
    ])('withholds cards for %s instead of rendering or repairing cache data', async (_name, change) => {
        const invalid = { ...stats, ...change }
        api.getMyProfileStats.mockResolvedValue(invalid)
        mount()
        await waitFor(() => expect(client.getQueryState(marketplaceKeys.myProfileStats(actor.scope))?.status).toBe('success'))
        expect(screen.queryByText(cardsLabel)).not.toBeInTheDocument()
        expect(client.getQueryData(marketplaceKeys.myProfileStats(actor.scope))).toEqual(invalid)
    })

    it.each(['stats', 'profile'] as const)('hides cached cards during %s refresh and after a failed revalidation', async (kind) => {
        mount(); await expectAcceptedCards()
        const pending = deferred<never>()
        const endpoint = kind === 'stats' ? api.getMyProfileStats : api.getMyProfile
        endpoint.mockReturnValue(pending.promise)
        const queryKey = kind === 'stats' ? marketplaceKeys.myProfileStats(actor.scope) : marketplaceKeys.myProfile(actor.scope)
        let refreshing!: Promise<void>
        act(() => { refreshing = client.invalidateQueries({ queryKey }) })
        await waitFor(() => expect(screen.queryByText(cardsLabel)).not.toBeInTheDocument())
        // React Query deliberately still has old data; the real tab must withhold it.
        expect(client.getQueryData(marketplaceKeys.myProfileStats(actor.scope))).toEqual(stats)
        await act(async () => { pending.reject(new Error('synthetic revalidation denied')); await refreshing })
        await waitFor(() => expect(client.getQueryState(queryKey)?.status).toBe('error'))
        expect(screen.queryByText(cardsLabel)).not.toBeInTheDocument()
        expect(endpoint).toHaveBeenCalledTimes(2)
    })

    it.each([
        ['inactive', { ...profile, isActive: false }],
        ['absent', { hasMarketplaceProfile: false }],
        ['missing identity', { ...profile, profileId: undefined }],
    ])('does not request stats for an %s profile', async (_name, response) => {
        api.getMyProfile.mockResolvedValue(response)
        mount()
        await waitFor(() => expect(client.getQueryState(marketplaceKeys.myProfile(actor.scope))?.status).toBe('success'))
        expect(screen.queryByText(cardsLabel)).not.toBeInTheDocument()
        expect(api.getMyProfileStats).not.toHaveBeenCalled()
    })

    it('does not request stats after profile authority fails', async () => {
        api.getMyProfile.mockRejectedValue(new Error('synthetic authority failure'))
        mount()
        expect(await screen.findByText('servicesMarketplace.profile.unavailableTitle')).toBeInTheDocument()
        expect(api.getMyProfileStats).not.toHaveBeenCalled()
    })

    it('changes query ownership on actor transition and rejects replay of the previous actor response', async () => {
        const view = mount(); await expectAcceptedCards()
        const previousScope = actor.scope
        actor.scope = `${tenantId}:44444444-4444-4444-8444-444444444444`
        view.rerender(<QueryClientProvider client={client}><ProfileTab /></QueryClientProvider>)
        await waitFor(() => expect(client.getQueryState(marketplaceKeys.myProfileStats(actor.scope))?.status).toBe('success'))
        expect(api.getMyProfileStats).toHaveBeenCalledTimes(2)
        expect(screen.queryByText(cardsLabel)).not.toBeInTheDocument()
        expect(client.getQueryData(marketplaceKeys.myProfileStats(previousScope))).toEqual(stats)
        // This test proves key isolation and presentation rejection. Cache retirement
        // on logout/realtime belongs to MarketplaceAuthorityBoundary, not this tab.
    })
})
