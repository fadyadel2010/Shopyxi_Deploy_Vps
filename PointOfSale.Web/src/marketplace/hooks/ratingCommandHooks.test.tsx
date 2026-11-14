import { createElement, type PropsWithChildren } from 'react'
import { act, cleanup, renderHook, waitFor } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { useBuyerRequestCommands } from './useBuyerRequestCommands'
import { useCreateReview } from './useCreateReview'
import { marketplaceKeys } from '../queryKeys'
import type { CreateReviewRequest, ServiceRequestDetails, ServiceReviewCreationAuthority } from '../types'

const api = vi.hoisted(() => ({ createReview: vi.fn(), getRequestDetails: vi.fn() }))
const session = vi.hoisted(() => ({ scope: 'tenant:buyer', current: 'tenant:buyer', invalidate: vi.fn() }))
vi.mock('../api/marketplaceApi', () => ({ marketplaceApi: api }))
vi.mock('../authority', () => ({ useMarketplaceAuthorityScope: () => session.scope,
    getMarketplaceAuthorityScope: () => session.current, publishMarketplaceAuthorityInvalidation: session.invalidate }))
vi.mock('../../store/authStore', () => ({ useAuthStore: () => ({ user: { id: '22222222-2222-4222-8222-222222222222' } }) }))

const requestId = '11111111-1111-4111-8111-111111111111'
const buyer = '22222222-2222-4222-8222-222222222222'
const provider = '33333333-3333-4333-8333-333333333333'
const serviceId = '44444444-4444-4444-8444-444444444444'
const reviewId = '55555555-5555-4555-8555-555555555555'
const review: CreateReviewRequest = { serviceRequestId: requestId, rating: 5, comment: 'Excellent' }
const response: ServiceReviewCreationAuthority = { reviewId, serviceRequestId: requestId, serviceId,
    reviewerUserId: buyer, providerUserId: provider, rating: 5, createdAt: '2026-06-15T03:00:00Z' }
function details(rated = false): ServiceRequestDetails {
    return { requestId, serviceId, buyerUserId: buyer, providerUserId: provider, status: 'CompletedByBuyer', createdAt: '2026-06-15T00:00:00Z',
        completionEvidence: { requestId, status: 'CompletedByBuyer',
            providerCompletion: { evidenceId: `${requestId}:provider`, requestId, evidenceType: 'ProviderCompletion', occurredAt: '2026-06-15T01:00:00Z', actorRole: 'Provider', actorUserId: provider },
            buyerConfirmation: { evidenceId: `${requestId}:buyer`, requestId, evidenceType: 'BuyerConfirmation', occurredAt: '2026-06-15T02:00:00Z', actorRole: 'Buyer', actorUserId: buyer },
            notes: [], attachments: [], deliverables: [] },
        capabilities: { canAccept: false, canReject: false, canCancel: false, canMarkCompleted: false, canConfirmCompletion: false,
            canChat: false, canViewChatHistory: true, canSendChat: false, canReview: !rated, existingReviewId: rated ? reviewId : null } }
}
const surfaces = [
    { name: 'buyer tab', useCommand: () => useBuyerRequestCommands(buyer).submitReviewMutation },
    { name: 'request details hook', useCommand: useCreateReview },
]
let client: QueryClient
beforeEach(() => {
    vi.resetAllMocks(); session.scope = 'tenant:buyer'; session.current = session.scope
    client = new QueryClient({ defaultOptions: { mutations: { retry: 2, retryDelay: 0 } } })
    api.getRequestDetails.mockResolvedValueOnce(details()).mockResolvedValue(details(true))
    api.createReview.mockResolvedValue(response)
})
afterEach(() => { cleanup(); client.clear() })

describe.each(surfaces)('$name actual rating command', surface => {
    function command() {
        return renderHook(surface.useCommand, { wrapper: ({ children }: PropsWithChildren) => createElement(QueryClientProvider, { client }, children) })
    }
    it('preflights and settles against backend authority, then invalidates only the original actor scope', async () => {
        const hook = command()
        const invalidate = vi.spyOn(client, 'invalidateQueries'); const cacheWrite = vi.spyOn(client, 'setQueryData')
        await act(async () => { await expect(hook.result.current.mutateAsync(review)).resolves.toEqual(response) })
        expect(api.createReview).toHaveBeenCalledExactlyOnceWith(review)
        expect(api.getRequestDetails).toHaveBeenCalledTimes(2)
        expect(api.getRequestDetails).toHaveBeenNthCalledWith(1, requestId)
        expect(api.getRequestDetails).toHaveBeenNthCalledWith(2, requestId)
        expect(invalidate).toHaveBeenCalledWith({ queryKey: marketplaceKeys.scope(session.scope) })
        expect(session.invalidate).toHaveBeenCalledExactlyOnceWith(session.scope)
        expect(cacheWrite).not.toHaveBeenCalled()
    })
    it('rejects stale preflight capabilities without issuing a review', async () => {
        api.getRequestDetails.mockReset().mockResolvedValue(details(true))
        const hook = command()
        await act(async () => { await expect(hook.result.current.mutateAsync(review)).rejects.toThrow('Current rating eligibility is unavailable') })
        expect(api.createReview).not.toHaveBeenCalled()
    })
    it('does not retry unknown transport outcomes or infer success from a committed review discovered later', async () => {
        const failure = new Error('synthetic timeout after possible commit'); api.createReview.mockRejectedValue(failure)
        const hook = command()
        await act(async () => { await expect(hook.result.current.mutateAsync(review)).rejects.toBe(failure) })
        expect(api.createReview).toHaveBeenCalledTimes(1)
        expect(api.getRequestDetails).toHaveBeenCalledTimes(2)
    })
    it('rejects actor replacement during preflight before any mutation', async () => {
        api.getRequestDetails.mockReset().mockImplementation(async () => { session.current = 'replacement:actor'; return details() })
        const hook = command()
        await act(async () => { await expect(hook.result.current.mutateAsync(review)).rejects.toThrow('Rating actor authority changed') })
        expect(api.createReview).not.toHaveBeenCalled()
    })
    it('rejects a malformed success identity without retrying or patching cache', async () => {
        api.createReview.mockResolvedValue({ ...response, reviewerUserId: provider })
        const hook = command(); const cacheWrite = vi.spyOn(client, 'setQueryData')
        await act(async () => { await expect(hook.result.current.mutateAsync(review)).rejects.toThrow('Invalid rating creation response') })
        expect(api.createReview).toHaveBeenCalledTimes(1)
        expect(cacheWrite).not.toHaveBeenCalled()
    })
    it('fences duplicate calls synchronously until the first creation and settlement finish', async () => {
        let release!: (value: ServiceReviewCreationAuthority) => void
        api.createReview.mockImplementation(() => new Promise(resolve => { release = resolve }))
        const hook = command(); let pending!: Promise<unknown>
        act(() => { pending = hook.result.current.mutateAsync(review) })
        await waitFor(() => expect(api.createReview).toHaveBeenCalledTimes(1))
        try {
            await act(async () => { await expect(hook.result.current.mutateAsync(review)).rejects.toThrow('already in progress') })
        } finally {
            await act(async () => { release(response); await pending })
        }
        expect(api.createReview).toHaveBeenCalledTimes(1)
        expect(api.getRequestDetails).toHaveBeenCalledTimes(2)
    })
})
