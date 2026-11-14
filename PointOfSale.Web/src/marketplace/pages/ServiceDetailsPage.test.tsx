import { act, cleanup, render, screen, waitFor } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { afterEach, beforeEach, expect, it, vi } from 'vitest'
import { ServiceDetailsPage } from './ServiceDetailsPage'
import { marketplaceKeys } from '../queryKeys'
import type { ServiceDetails, ServiceReviewHistory } from '../types'

const api = vi.hoisted(() => ({ details: vi.fn(), reviews: vi.fn() }))
const identity = vi.hoisted(() => ({ scope: 'tenant:actor', serviceId: 'a2b9e228-a6e6-4a5e-8d43-e6d1504942c0' }))
vi.mock('../api/marketplaceApi', () => ({ marketplaceApi: { getServiceDetails: api.details, getServiceReviews: api.reviews } }))
vi.mock('../authority', async original => ({ ...await original<typeof import('../authority')>(),
    useMarketplaceAuthorityScope: () => identity.scope, getMarketplaceAuthorityScope: () => identity.scope }))
vi.mock('../hooks/useRequestService', () => ({ useRequestService: () => ({ mutateAsync: vi.fn(), isPending: false }) }))
vi.mock('react-router-dom', () => ({ useParams: () => ({ serviceId: identity.serviceId }), useNavigate: () => vi.fn() }))
vi.mock('react-i18next', () => ({ useTranslation: () => ({ t: (key: string, options?: { count?: string }) =>
    key === 'servicesMarketplace.serviceDetails.reviewsTitle' ? `service-reviews:${options?.count}` : key }) }))
vi.mock('../../hooks/useFormatting', () => ({ useFormatting: () => ({ formatNumber: (value: number) => `number:${value}`,
    formatCurrency: (value: number) => `money:${value}`, formatDate: (value: string) => value, isRTL: false }) }))
const providerProfileId = '00a12dc1-561f-4406-8088-1ff76375ef25'
const providerUserId = 'f2c6b9af-57fb-4fb0-9ca7-83363f078fdd'
const service: ServiceDetails = {
    serviceId: identity.serviceId, title: 'Current service', description: 'Current backend description', category: 'Development',
    providerProfileId, providerDisplayName: 'Current provider', isActive: true, createdAt: '2026-06-14T00:00:00Z',
    providerProfile: {}, requestEntry: { canRequest: true, reason: 'Eligible' },
    // Provider-wide reputation legitimately includes reviews of other services.
    trust: { averageRating: 4.5, totalReviews: 8, trustBadge: 'Rising',
        ratingDistribution: { 5: 5, 4: 2, 3: 1, 2: 0, 1: 0 }, ratingPercentages: { 5: 62.5, 4: 25, 3: 12.5, 2: 0, 1: 0 } },
}
const history: ServiceReviewHistory = { serviceId: identity.serviceId, providerProfileId, providerUserId, totalReviews: 1,
    items: [{ reviewId: 'ce210a55-f82c-442a-b4e2-4cc9188640e5', serviceRequestId: '69c9e180-5907-49d3-9096-4207fd96ff2a',
        serviceId: identity.serviceId, reviewerUserId: '105b530e-8e3d-4c69-9c72-0d395da91f4d', providerUserId, rating: 5,
        comment: 'Current service review', createdAt: '2026-06-14T00:00:00Z', updatedAt: '2026-06-14T00:00:00Z', buyerName: 'Current buyer', marketingEvidence: null }] }
const reviewsKey = () => [...marketplaceKeys.service(identity.serviceId, identity.scope), 'reviews']
const unavailable = 'servicesMarketplace.serviceDetails.reviewsUnavailableTitle'
let client: QueryClient
beforeEach(() => {
    vi.resetAllMocks()
    client = new QueryClient({ defaultOptions: { queries: { retry: 2, retryDelay: 0, gcTime: Infinity } } })
    api.details.mockResolvedValue(service); api.reviews.mockResolvedValue(history)
})
afterEach(() => { cleanup(); client.clear() })
function mount() { return render(<QueryClientProvider client={client}><ServiceDetailsPage /></QueryClientProvider>) }
function deferred<T>() {
    let resolve!: (value: T) => void; let reject!: (error: unknown) => void
    const promise = new Promise<T>((yes, no) => { resolve = yes; reject = no })
    return { promise, resolve, reject }
}

it('keeps service history count distinct from provider trust totals using actual query hooks and validators', async () => {
    mount()
    expect(await screen.findByRole('heading', { name: 'service-reviews:number:1' })).toBeInTheDocument()
    expect(screen.getByText('Current service review')).toBeInTheDocument()
    expect(screen.getByText('number:8')).toBeInTheDocument()
    expect(screen.queryByRole('heading', { name: 'service-reviews:number:8' })).not.toBeInTheDocument()
    expect(api.details).toHaveBeenCalledExactlyOnceWith(identity.serviceId)
    expect(api.reviews).toHaveBeenCalledExactlyOnceWith(identity.serviceId)
})

it('renders zero only for an accepted empty service-history response, not the provider-wide count', async () => {
    api.reviews.mockResolvedValue({ ...history, items: [], totalReviews: 0 })
    mount()
    expect(await screen.findByRole('heading', { name: 'service-reviews:number:0' })).toBeInTheDocument()
    expect(screen.getByText('servicesMarketplace.serviceDetails.noReviews')).toBeInTheDocument()
    expect(screen.getByText('number:8')).toBeInTheDocument()
})

it('withholds an initial pending history count instead of inventing zero', async () => {
    const pending = deferred<ServiceReviewHistory>(); api.reviews.mockReturnValue(pending.promise)
    mount()
    expect(await screen.findByRole('heading', { name: unavailable })).toBeInTheDocument()
    expect(screen.queryByRole('heading', { name: 'service-reviews:number:0' })).not.toBeInTheDocument()
    await act(async () => { pending.resolve(history); await pending.promise })
    expect(await screen.findByRole('heading', { name: 'service-reviews:number:1' })).toBeInTheDocument()
})

it('withholds retained cached history during refresh and after a nonauthority transport failure', async () => {
    mount(); await screen.findByRole('heading', { name: 'service-reviews:number:1' })
    const pending = deferred<ServiceReviewHistory>(); api.reviews.mockReturnValue(pending.promise)
    let refreshing!: Promise<void>
    act(() => { refreshing = client.invalidateQueries({ queryKey: reviewsKey() }) })
    expect(await screen.findByRole('heading', { name: unavailable })).toBeInTheDocument()
    expect(client.getQueryData(reviewsKey())).toEqual(history)
    expect(screen.queryByText('Current service review')).not.toBeInTheDocument()
    await act(async () => { pending.reject(new Error('synthetic history unavailable')); await refreshing })
    await waitFor(() => expect(client.getQueryState(reviewsKey())?.status).toBe('error'))
    expect(screen.getByRole('heading', { name: unavailable })).toBeInTheDocument()
    expect(screen.getByText('Current service')).toBeInTheDocument()
    expect(screen.queryByRole('heading', { name: 'service-reviews:number:0' })).not.toBeInTheDocument()
    expect(api.reviews).toHaveBeenCalledTimes(2)
})

it.each([401, 403, 404])('fails the whole detail closed after history authority returns %s', async status => {
    api.reviews.mockRejectedValue({ status })
    mount()
    expect(await screen.findByText('servicesMarketplace.serviceDetails.notFoundTitle')).toBeInTheDocument()
    expect(screen.queryByText('Current service review')).not.toBeInTheDocument()
    expect(screen.queryByRole('heading', { name: 'service-reviews:number:0' })).not.toBeInTheDocument()
    expect(api.reviews).toHaveBeenCalledTimes(1)
})

it.each([
    ['contradictory total', { ...history, totalReviews: 2 }],
    ['foreign service', { ...history, serviceId: 'bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb' }],
    ['foreign profile', { ...history, providerProfileId: 'bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb' }],
])('withholds %s history rather than repairing counts or displaying aggregate fallback', async (_name, response) => {
    api.reviews.mockResolvedValue(response)
    mount()
    await waitFor(() => expect(client.getQueryState(reviewsKey())?.status).toBe('success'))
    expect(await screen.findByRole('heading', { name: unavailable })).toBeInTheDocument()
    expect(screen.queryByText('Current service review')).not.toBeInTheDocument()
    expect(screen.queryByRole('heading', { name: /service-reviews:number/ })).not.toBeInTheDocument()
    expect(client.getQueryData(reviewsKey())).toEqual(response)
})
