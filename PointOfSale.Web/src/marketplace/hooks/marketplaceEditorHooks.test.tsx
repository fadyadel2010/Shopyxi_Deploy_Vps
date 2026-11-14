import { createElement, type PropsWithChildren } from 'react'
import { act, cleanup, renderHook, waitFor } from '@testing-library/react'
import { onlineManager, QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { useMarketplaceProfileEditor } from './useMarketplaceProfileEditor'
import { useMarketplaceServiceEditor } from './useMarketplaceServiceEditor'
import { marketplaceKeys } from '../queryKeys'

const api = vi.hoisted(() => ({ createProfile: vi.fn(), updateProfile: vi.fn(), createService: vi.fn(), updateService: vi.fn() }))
const context = vi.hoisted(() => ({ scope: 'tenant:editor', invalidate: vi.fn(), created: vi.fn(), updated: vi.fn() }))
vi.mock('../api/marketplaceApi', () => ({ marketplaceApi: api }))
vi.mock('../authority', () => ({ useMarketplaceAuthorityScope: () => context.scope,
    getMarketplaceAuthorityScope: () => context.scope, publishMarketplaceAuthorityInvalidation: context.invalidate }))

const profile = { displayName: 'Synthetic profile' }
const service = { title: 'Synthetic service', description: 'Synthetic description' }
const variants = [
    { name: 'create profile', endpoint: 'createProfile' as const, args: [profile], callback: 'created' as const, useRun: () => {
        const { createProfileMutation } = useMarketplaceProfileEditor(context.created, context.updated)
        return () => createProfileMutation.mutateAsync(profile)
    } },
    { name: 'update profile', endpoint: 'updateProfile' as const, args: [profile], callback: 'updated' as const, useRun: () => {
        const { updateProfileMutation } = useMarketplaceProfileEditor(context.created, context.updated)
        return () => updateProfileMutation.mutateAsync(profile)
    } },
    { name: 'create service', endpoint: 'createService' as const, args: [service], callback: 'created' as const, useRun: () => {
        const { createServiceMutation } = useMarketplaceServiceEditor(context.created, context.updated)
        return () => createServiceMutation.mutateAsync(service)
    } },
    { name: 'update service', endpoint: 'updateService' as const, args: ['service-A', service], callback: 'updated' as const, useRun: () => {
        const { updateServiceMutation } = useMarketplaceServiceEditor(context.created, context.updated)
        return () => updateServiceMutation.mutateAsync({ id: 'service-A', data: service })
    } },
]
let client: QueryClient
beforeEach(() => {
    vi.resetAllMocks(); context.scope = 'tenant:editor'; onlineManager.setOnline(true)
    client = new QueryClient({ defaultOptions: { mutations: { retry: 1, retryDelay: 0, networkMode: 'always' } } })
})
afterEach(() => { cleanup(); client.clear(); onlineManager.setOnline(true) })

describe.each(variants)('$name actual editor mutation', variant => {
    function command() {
        return renderHook(variant.useRun, { wrapper: ({ children }: PropsWithChildren) => createElement(QueryClientProvider, { client }, children) })
    }
    it('routes the exact intent and only invalidates scoped cache on success', async () => {
        api[variant.endpoint].mockResolvedValue({ backend: 'response' })
        const cacheWrite = vi.spyOn(client, 'setQueryData'); const invalidate = vi.spyOn(client, 'invalidateQueries')
        const hook = command()
        await act(async () => { await hook.result.current() })
        expect(api[variant.endpoint]).toHaveBeenCalledExactlyOnceWith(...variant.args)
        expect(context[variant.callback]).toHaveBeenCalledTimes(1)
        expect(invalidate).toHaveBeenCalledWith({ queryKey: marketplaceKeys.scope('tenant:editor') })
        expect(context.invalidate).toHaveBeenCalledExactlyOnceWith('tenant:editor')
        expect(cacheWrite).not.toHaveBeenCalled()
    })
    it('preserves an unknown failure without inheriting retries or invoking success callbacks', async () => {
        const failure = new Error('synthetic unknown write outcome'); api[variant.endpoint].mockRejectedValue(failure)
        const hook = command()
        await act(async () => { await expect(hook.result.current()).rejects.toBe(failure) })
        expect(api[variant.endpoint]).toHaveBeenCalledTimes(1)
        expect(context.created).not.toHaveBeenCalled(); expect(context.updated).not.toHaveBeenCalled()
        expect(context.invalidate).toHaveBeenCalledExactlyOnceWith('tenant:editor')
    })
    it('pauses offline instead of inheriting always-online mutation behavior', async () => {
        api[variant.endpoint].mockResolvedValue({ backend: 'response' }); onlineManager.setOnline(false)
        const hook = command(); let pending!: Promise<unknown>
        act(() => { pending = hook.result.current().catch(error => error) })
        try {
            await waitFor(() => expect(client.getMutationCache().getAll().some(m => m.state.isPaused)).toBe(true))
            expect(api[variant.endpoint]).not.toHaveBeenCalled()
        } finally { await act(async () => { onlineManager.setOnline(true); await pending }) }
        expect(api[variant.endpoint]).toHaveBeenCalledTimes(1)
    })
    it('rejects a command captured before authority changed, before issuing any write', async () => {
        api[variant.endpoint].mockResolvedValue({ backend: 'response' })
        const hook = command(); context.scope = 'replacement:actor'
        await act(async () => { await expect(hook.result.current()).rejects.toThrow('authority') })
        expect(api[variant.endpoint]).not.toHaveBeenCalled()
        expect(context.created).not.toHaveBeenCalled(); expect(context.updated).not.toHaveBeenCalled()
    })
    it('rejects an offline queued intent after actor replacement instead of issuing it under the next session', async () => {
        api[variant.endpoint].mockResolvedValue({ backend: 'response' }); onlineManager.setOnline(false)
        const hook = command(); let pending!: Promise<unknown>
        act(() => { pending = hook.result.current().catch(error => error) })
        let outcome: unknown
        try {
            await waitFor(() => expect(client.getMutationCache().getAll().some(m => m.state.isPaused)).toBe(true))
            context.scope = 'replacement:actor'
        } finally { await act(async () => { onlineManager.setOnline(true); outcome = await pending }) }
        expect(outcome).toEqual(expect.objectContaining({ message: expect.stringContaining('authority') }))
        expect(api[variant.endpoint]).not.toHaveBeenCalled()
    })
    it('withholds late success and keeps invalidation bound to the original actor, never patching the next actor cache', async () => {
        let release!: (value: unknown) => void
        api[variant.endpoint].mockImplementation(() => new Promise(resolve => { release = resolve }))
        const hook = command(); const cacheWrite = vi.spyOn(client, 'setQueryData'); const invalidate = vi.spyOn(client, 'invalidateQueries')
        let pending!: Promise<unknown>
        act(() => { pending = hook.result.current().catch(error => error) })
        await waitFor(() => expect(api[variant.endpoint]).toHaveBeenCalledTimes(1))
        await act(async () => {
            context.scope = 'replacement:actor'; release({ backend: 'response' })
            expect(await pending).toEqual(expect.objectContaining({ message: expect.stringContaining('authority') }))
        })
        expect(context.created).not.toHaveBeenCalled(); expect(context.updated).not.toHaveBeenCalled()
        expect(invalidate).toHaveBeenCalledWith({ queryKey: marketplaceKeys.scope('tenant:editor') })
        expect(invalidate).not.toHaveBeenCalledWith({ queryKey: marketplaceKeys.scope('replacement:actor') })
        expect(context.invalidate).toHaveBeenCalledExactlyOnceWith('tenant:editor')
        expect(cacheWrite).not.toHaveBeenCalled()
    })
})
