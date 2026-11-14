import { createElement, type PropsWithChildren } from 'react'
import { act, cleanup, renderHook, waitFor } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { afterEach, beforeEach, expect, it, vi } from 'vitest'
import { useIncomingRequestCommands } from './useIncomingRequestCommands'
import { marketplaceKeys } from '../queryKeys'

const mocks = vi.hoisted(() => ({ reject: vi.fn(), accept: vi.fn(), cancel: vi.fn(), invalidate: vi.fn(), scope: 'scope-A', current: 'scope-A' }))
vi.mock('../api/marketplaceApi', () => ({ marketplaceApi: { rejectRequest: mocks.reject, acceptRequest: mocks.accept, cancelRequest: mocks.cancel } }))
vi.mock('../authority', () => ({ useMarketplaceAuthorityScope: () => mocks.scope, getMarketplaceAuthorityScope: () => mocks.current, publishMarketplaceAuthorityInvalidation: mocks.invalidate }))
let client: QueryClient
beforeEach(() => {
    vi.clearAllMocks(); mocks.reject.mockReset(); mocks.accept.mockReset(); mocks.scope = 'scope-A'; mocks.current = 'scope-A'
    client = new QueryClient({ defaultOptions: { mutations: { retry: 1, retryDelay: 0 } } })
})
afterEach(() => { cleanup(); client.clear() })
function commands(actor: unknown = 'provider-A') {
    return renderHook(() => useIncomingRequestCommands(actor), { wrapper: ({ children }: PropsWithChildren) => createElement(QueryClientProvider, { client }, children) })
}

it('uses the rejection endpoint and only scoped invalidation, never cancellation or cache authority', async () => {
    mocks.reject.mockResolvedValue({ status: 'Rejected' })
    const invalidation = vi.spyOn(client, 'invalidateQueries')
    const cacheWrite = vi.spyOn(client, 'setQueryData')
    const hook = commands()
    await act(async () => { await expect(hook.result.current.rejectMutation.mutateAsync('request-A')).resolves.toEqual({ status: 'Rejected' }) })
    expect(mocks.reject).toHaveBeenCalledExactlyOnceWith('request-A')
    expect(mocks.cancel).not.toHaveBeenCalled()
    expect(cacheWrite).not.toHaveBeenCalled()
    expect(invalidation).toHaveBeenCalledWith({ queryKey: marketplaceKeys.scope('scope-A') })
    expect(mocks.invalidate).toHaveBeenCalledExactlyOnceWith('scope-A')
})

it('rejects an unexpected backend transition instead of presenting cancellation as rejection', async () => {
    mocks.reject.mockResolvedValue({ status: 'Cancelled' })
    const hook = commands()
    await act(async () => { await expect(hook.result.current.rejectMutation.mutateAsync('request-A')).rejects.toThrow('Invalid request rejection lifecycle response') })
    expect(mocks.cancel).not.toHaveBeenCalled()
})

it('preserves transport failure and does not retry or synthesize success', async () => {
    const failure = new Error('fixture-unavailable')
    mocks.reject.mockRejectedValue(failure)
    const hook = commands()
    await act(async () => { await expect(hook.result.current.rejectMutation.mutateAsync('request-A')).rejects.toBe(failure) })
    expect(mocks.reject).toHaveBeenCalledTimes(1)
})

it('withholds accept and reject when the actor or current session scope is unavailable', async () => {
    const hook = commands(null)
    await act(async () => { await expect(hook.result.current.acceptMutation.mutateAsync('request-A')).rejects.toThrow('unavailable') })
    const active = commands()
    mocks.current = 'scope-B'
    await act(async () => { await expect(active.result.current.rejectMutation.mutateAsync('request-A')).rejects.toThrow('unavailable') })
    expect(mocks.accept).not.toHaveBeenCalled(); expect(mocks.reject).not.toHaveBeenCalled()
})

it('rejects a late success after authority changes while the request is in flight', async () => {
    let finish!: (value: { status: string }) => void
    mocks.accept.mockImplementation(() => new Promise(resolve => { finish = resolve }))
    const hook = commands()
    let pending!: Promise<unknown>
    act(() => { pending = hook.result.current.acceptMutation.mutateAsync('request-A') })
    await waitFor(() => expect(mocks.accept).toHaveBeenCalledTimes(1))
    await act(async () => {
        mocks.current = 'scope-B'; finish({ status: 'Accepted' })
        await expect(pending).rejects.toThrow('unavailable')
    })
    expect(mocks.accept).toHaveBeenCalledTimes(1)
})
