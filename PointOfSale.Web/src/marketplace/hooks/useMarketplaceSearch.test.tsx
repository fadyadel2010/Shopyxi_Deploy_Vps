import { createElement, type PropsWithChildren } from 'react'
import { act, cleanup, renderHook, waitFor } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { afterEach, beforeEach, expect, it, vi } from 'vitest'
import { useMarketplaceSearch } from './useMarketplaceSearch'
import { marketplaceKeys } from '../queryKeys'
import type { ServiceSearchRequest } from '../types'

const backend = vi.hoisted(() => ({ search: vi.fn(), scope: 'tenant:actor' }))
vi.mock('../api/marketplaceApi', () => ({ marketplaceApi: { searchServices: backend.search } }))
vi.mock('../authority', async original => ({ ...await original<typeof import('../authority')>(),
    useMarketplaceAuthorityScope: () => backend.scope }))
const request: ServiceSearchRequest = Object.freeze({ query: 'backend order', page: 3, pageSize: 20, sort: 'rating' })
const response = Object.freeze({ items: Object.freeze([{ serviceId: 'server-second' }, { serviceId: 'server-first' }]), totalCount: 73, page: 3, pageSize: 20 })
let client: QueryClient
beforeEach(() => {
    backend.search.mockReset(); backend.scope = 'tenant:actor'
    client = new QueryClient({ defaultOptions: { queries: { retry: 2, retryDelay: 0, gcTime: Infinity } } })
})
afterEach(() => { cleanup(); client.clear() })
function search(value = request) {
    return renderHook(({ current }) => useMarketplaceSearch(current), { initialProps: { current: value },
        wrapper: ({ children }: PropsWithChildren) => createElement(QueryClientProvider, { client }, children) })
}

it('forwards the exact request and preserves backend order, counts and pagination without client repair', async () => {
    backend.search.mockResolvedValue(response)
    const hook = search()
    await waitFor(() => expect(hook.result.current.isSuccess).toBe(true))
    expect(backend.search).toHaveBeenCalledExactlyOnceWith(request)
    expect(hook.result.current.data).toEqual(response)
    expect(client.getQueryData(marketplaceKeys.search(request, backend.scope))).toEqual(response)
    // This hook does not certify DTO visibility; the consumer must still validate it.
})

it('binds cache identity to both current actor scope and all request parameters', async () => {
    backend.search.mockResolvedValue(response)
    const hook = search()
    await waitFor(() => expect(hook.result.current.isSuccess).toBe(true))
    const previousScope = backend.scope
    const changed: ServiceSearchRequest = { ...request, query: 'new query', page: 1, category: 'Design', sort: 'newest' }
    const next = { items: [], totalCount: 0, page: 1, pageSize: 20 }
    backend.search.mockResolvedValue(next); backend.scope = 'another:actor'
    hook.rerender({ current: changed })
    await waitFor(() => expect(hook.result.current.data).toEqual(next))
    expect(backend.search).toHaveBeenLastCalledWith(changed)
    expect(client.getQueryData(marketplaceKeys.search(request, previousScope))).toEqual(response)
    expect(client.getQueryData(marketplaceKeys.search(changed, backend.scope))).toEqual(next)
})

it('preserves initial backend failure and overrides inherited query retries instead of fabricating empty results', async () => {
    const failure = new Error('synthetic backend search unavailable'); backend.search.mockRejectedValue(failure)
    const hook = search()
    await waitFor(() => expect(hook.result.current.isError).toBe(true))
    expect(hook.result.current.error).toBe(failure)
    expect(hook.result.current.data).toBeUndefined()
    expect(backend.search).toHaveBeenCalledTimes(1)
})

it('reports failed refresh separately from retained stale cache rather than presenting a successful empty page', async () => {
    backend.search.mockResolvedValue(response)
    const hook = search()
    await waitFor(() => expect(hook.result.current.isSuccess).toBe(true))
    const failure = new Error('synthetic refresh failed'); backend.search.mockRejectedValue(failure)
    await act(async () => { await hook.result.current.refetch() })
    await waitFor(() => expect(hook.result.current.isRefetchError).toBe(true))
    expect(hook.result.current.data).toEqual(response)
    expect(hook.result.current.error).toBe(failure)
    expect(backend.search).toHaveBeenCalledTimes(2)
})

it('carries the first page generation across pages and includes it in page cache identity', async () => {
    const first = { ...request, page: 1 }
    backend.search.mockResolvedValue({ ...response, page: 1, generationContinuation: 'pin-A' })
    const hook = search(first)
    await waitFor(() => expect(hook.result.current.isSuccess).toBe(true))
    expect(backend.search).toHaveBeenCalledExactlyOnceWith(first)
    backend.search.mockResolvedValue({ ...response, page: 2, generationContinuation: 'pin-A' })
    const second = { ...first, page: 2 }
    hook.rerender({ current: second })
    await waitFor(() => expect(hook.result.current.data?.page).toBe(2))
    expect(backend.search).toHaveBeenLastCalledWith({ ...second, generationContinuation: 'pin-A' })
    expect(client.getQueryData(marketplaceKeys.search({ ...second, generationContinuation: 'pin-A' }, backend.scope)))
        .toEqual(hook.result.current.data)
})

it('never reuses a generation across actors, filters or page sizes', async () => {
    const first = { ...request, page: 1 }
    backend.search.mockResolvedValue({ ...response, generationContinuation: 'private-pin' })
    const hook = search(first)
    await waitFor(() => expect(hook.result.current.isSuccess).toBe(true))
    const foreign = { ...request, page: 2, category: 'different', pageSize: 10 }
    backend.scope = 'other:actor'
    backend.search.mockResolvedValue({ ...response, page: 2 })
    hook.rerender({ current: foreign })
    await waitFor(() => expect(backend.search).toHaveBeenLastCalledWith(foreign))
    expect(client.getQueryData(marketplaceKeys.searchGeneration(foreign, backend.scope))).toBeNull()
})

it('starts a fresh generation on successful first-page refresh and removes it on a legacy response', async () => {
    const first = { ...request, page: 1 }
    backend.search.mockResolvedValue({ ...response, generationContinuation: 'pin-A' })
    const hook = search(first)
    await waitFor(() => expect(hook.result.current.isSuccess).toBe(true))
    backend.search.mockResolvedValue({ ...response, generationContinuation: 'pin-B' })
    await act(async () => { await hook.result.current.refetch() })
    expect(backend.search).toHaveBeenLastCalledWith(first)
    expect(client.getQueryData(marketplaceKeys.searchGeneration(first, backend.scope))).toBe('pin-B')
    backend.search.mockResolvedValue(response)
    await act(async () => { await hook.result.current.refetch() })
    expect(client.getQueryData(marketplaceKeys.searchGeneration(first, backend.scope))).toBeNull()
})

it('does not drop a pinned generation or retry/fallback when a later page fails', async () => {
    const first = { ...request, page: 1 }
    backend.search.mockResolvedValue({ ...response, generationContinuation: 'pin-A' })
    const hook = search(first)
    await waitFor(() => expect(hook.result.current.isSuccess).toBe(true))
    const error = new Error('expired generation')
    backend.search.mockRejectedValue(error)
    hook.rerender({ current: { ...first, page: 2 } })
    await waitFor(() => expect(hook.result.current.isError).toBe(true))
    expect(hook.result.current.error).toBe(error)
    expect(backend.search).toHaveBeenCalledTimes(2)
    expect(backend.search).toHaveBeenLastCalledWith({ ...first, page: 2, generationContinuation: 'pin-A' })
})

it('does not let a cancelled first-page response overwrite a newer generation', async () => {
    const first = { ...request, page: 1 }
    backend.search.mockResolvedValue({ ...response, generationContinuation: 'pin-A' })
    const hook = search(first)
    await waitFor(() => expect(hook.result.current.isSuccess).toBe(true))
    let finishOld!: (value: unknown) => void
    backend.search.mockImplementationOnce(() => new Promise(resolve => { finishOld = resolve }))
    let oldRefresh!: Promise<unknown>
    act(() => { oldRefresh = hook.result.current.refetch() })
    await waitFor(() => expect(backend.search).toHaveBeenCalledTimes(2))
    backend.search.mockResolvedValue({ ...response, generationContinuation: 'pin-new' })
    await act(async () => { await hook.result.current.refetch() })
    await act(async () => { finishOld({ ...response, generationContinuation: 'pin-cancelled' }); await oldRefresh })
    expect(client.getQueryData(marketplaceKeys.searchGeneration(first, backend.scope))).toBe('pin-new')
})
