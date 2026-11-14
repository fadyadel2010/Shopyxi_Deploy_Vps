import { createElement, type PropsWithChildren } from 'react'
import { act, cleanup, renderHook, waitFor } from '@testing-library/react'
import { onlineManager, QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { useBuyerRequestCommands } from './useBuyerRequestCommands'
import { useIncomingRequestCommands } from './useIncomingRequestCommands'
import { useConfirmCompletion } from './useConfirmCompletion'
import { useMarkCompleted } from './useMarkCompleted'
import { marketplaceKeys } from '../queryKeys'
import type { ServiceRequestDetails } from '../types'

const api = vi.hoisted(() => ({ confirmCompletion: vi.fn(), markCompleted: vi.fn(), getRequestDetails: vi.fn() }))
const session = vi.hoisted(() => ({ scope: 'tenant:actor', current: 'tenant:actor', actorId: '', invalidate: vi.fn() }))
vi.mock('../api/marketplaceApi', () => ({ marketplaceApi: api }))
vi.mock('../authority', () => ({ useMarketplaceAuthorityScope: () => session.scope,
    getMarketplaceAuthorityScope: () => session.current, publishMarketplaceAuthorityInvalidation: session.invalidate }))
vi.mock('../../store/authStore', () => ({ useAuthStore: () => ({ user: { id: session.actorId } }) }))

const requestId = '11111111-1111-4111-8111-111111111111'
const buyer = '22222222-2222-4222-8222-222222222222'
const provider = '33333333-3333-4333-8333-333333333333'
const surfaces = [
    { name: 'buyer tab', actor: buyer, kind: 'confirmCompletion' as const, useCommand: () => useBuyerRequestCommands(buyer).confirmCompletionMutation },
    { name: 'provider tab', actor: provider, kind: 'markCompleted' as const, useCommand: () => useIncomingRequestCommands(provider).markCompletedMutation },
    { name: 'buyer details hook', actor: buyer, kind: 'confirmCompletion' as const, useCommand: useConfirmCompletion },
    { name: 'provider details hook', actor: provider, kind: 'markCompleted' as const, useCommand: useMarkCompleted },
]
let client: QueryClient
function details(kind: 'confirmCompletion' | 'markCompleted'): ServiceRequestDetails {
    const status = kind === 'confirmCompletion' ? 'CompletedByBuyer' : 'CompletedByProvider'
    return { requestId, serviceId: '44444444-4444-4444-8444-444444444444', buyerUserId: buyer, providerUserId: provider,
        status, createdAt: '2026-06-15T00:00:00Z',
        completionEvidence: { requestId, status,
            providerCompletion: { evidenceId: `${requestId}:provider`, requestId, evidenceType: 'ProviderCompletion', occurredAt: '2026-06-15T01:00:00Z', actorRole: 'Provider', actorUserId: provider },
            buyerConfirmation: kind === 'confirmCompletion'
                ? { evidenceId: `${requestId}:buyer`, requestId, evidenceType: 'BuyerConfirmation', occurredAt: '2026-06-15T02:00:00Z', actorRole: 'Buyer', actorUserId: buyer } : null,
            notes: [], attachments: [], deliverables: [] },
        capabilities: { canAccept: false, canReject: false, canCancel: false, canMarkCompleted: false,
            canConfirmCompletion: false, canChat: kind === 'markCompleted', canViewChatHistory: true,
            canSendChat: kind === 'markCompleted', canReview: false } }
}
function deferred<T>() {
    let resolve!: (value: T) => void
    const promise = new Promise<T>(yes => { resolve = yes })
    return { promise, resolve }
}
beforeEach(() => {
    vi.resetAllMocks(); session.scope = 'tenant:actor'; session.current = session.scope
    onlineManager.setOnline(true)
    // Hostile inherited defaults must not replay or issue offline lifecycle commands.
    client = new QueryClient({ defaultOptions: { mutations: { retry: 1, retryDelay: 0, networkMode: 'always' } } })
})
afterEach(() => { cleanup(); client.clear(); onlineManager.setOnline(true) })

describe.each(surfaces)('$name actual completion command', surface => {
    function command() {
        session.actorId = surface.actor
        return renderHook(surface.useCommand, { wrapper: ({ children }: PropsWithChildren) => createElement(QueryClientProvider, { client }, children) })
    }
    function acceptBackendTruth() {
        api[surface.kind].mockResolvedValue({ status: details(surface.kind).status })
        api.getRequestDetails.mockResolvedValue(details(surface.kind))
    }

    it('does not inherit retries for an unproven failed command', async () => {
        const failure = new Error('synthetic unknown mutation outcome')
        api[surface.kind].mockRejectedValue(failure)
        api.getRequestDetails.mockRejectedValue(new Error('revalidation unavailable'))
        const hook = command()
        await act(async () => { await expect(hook.result.current.mutateAsync(requestId)).rejects.toBe(failure) })
        expect(api[surface.kind]).toHaveBeenCalledExactlyOnceWith(requestId)
        expect(api.getRequestDetails).toHaveBeenCalledExactlyOnceWith(requestId)
    })

    it('pauses offline despite inherited always-online mode and issues only once after reconnect', async () => {
        acceptBackendTruth(); onlineManager.setOnline(false)
        const hook = command()
        let outcome!: Promise<unknown>
        act(() => { outcome = hook.result.current.mutateAsync(requestId).catch(error => error) })
        try {
            await waitFor(() => expect(hook.result.current.isPaused).toBe(true))
            expect(api[surface.kind]).not.toHaveBeenCalled()
        } finally {
            await act(async () => { onlineManager.setOnline(true); await outcome })
        }
        expect(api[surface.kind]).toHaveBeenCalledExactlyOnceWith(requestId)
        expect(api.getRequestDetails).toHaveBeenCalledExactlyOnceWith(requestId)
    })

    it('accepts timeout-after-commit only through coherent backend proof and invalidates without cache patching', async () => {
        acceptBackendTruth()
        api[surface.kind].mockRejectedValue(new Error('timeout after possible commit'))
        const cacheWrite = vi.spyOn(client, 'setQueryData')
        const invalidate = vi.spyOn(client, 'invalidateQueries')
        const hook = command()
        await act(async () => { await expect(hook.result.current.mutateAsync(requestId)).resolves.toEqual(details(surface.kind)) })
        expect(api[surface.kind]).toHaveBeenCalledTimes(1)
        expect(invalidate).toHaveBeenCalledWith({ queryKey: marketplaceKeys.scope(session.scope) })
        expect(session.invalidate).toHaveBeenCalledExactlyOnceWith(session.scope)
        expect(cacheWrite).not.toHaveBeenCalled()
    })

    it('rejects late settlement after actor scope replacement', async () => {
        acceptBackendTruth()
        const pending = deferred<{ status: string }>()
        api[surface.kind].mockReturnValue(pending.promise)
        const hook = command()
        let outcome!: Promise<unknown>
        act(() => { outcome = hook.result.current.mutateAsync(requestId).catch(error => error) })
        await waitFor(() => expect(api[surface.kind]).toHaveBeenCalledTimes(1))
        await act(async () => {
            session.current = 'replacement:actor'
            pending.resolve({ status: details(surface.kind).status })
            expect(await outcome).toEqual(expect.objectContaining({ message: 'Completion actor authority changed' }))
        })
        expect(api.getRequestDetails).not.toHaveBeenCalled()
    })
})
