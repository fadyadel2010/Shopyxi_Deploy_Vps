import { act, cleanup, renderHook, waitFor } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import usePosDashboardData, { POS_SOURCES, useDashboardSource } from './usePosDashboardData'
import api from '../../services/api'
import { useAuthStore } from '../../store/authStore'

vi.mock('../../services/api', () => ({ default: { get: vi.fn() } }))
const actor = { id: 'actor-a', tenantId: 'tenant-a', effectivePermissions: ['dashboard.view', 'finance.view', 'sales.view'] }
const stats = {
    todaySales: -123.45, salesChangePercentage: -12.5, todayTransactionCount: 3,
    lowStockCount: 2, inventoryValue: 987654321.12, totalProducts: 7, totalCustomers: 8,
    weekSales: 11, monthSales: 22, yearSales: 33, totalRevenue: 44,
    outstandingReceivables: -55, outstandingPayables: -66,
}
const urls = Object.values(POS_SOURCES).map(source => source.url('week'))
const deferred = () => {
    let resolve, reject
    const promise = new Promise((yes, no) => { resolve = yes; reject = no })
    return { promise, resolve, reject }
}
const response = url => ({ data: url.endsWith('/stats') ? stats : url.includes('sales-trend') ? { data: [] } : [] })
beforeEach(() => {
    useAuthStore.setState({ user: actor })
    api.get.mockReset().mockImplementation(async url => response(url))
})
afterEach(() => { cleanup(); vi.useRealTimers(); useAuthStore.setState({ user: null }) })

describe('POS independent source contract (deterministic test data only)', () => {
    it('starts all seven exact GETs concurrently with unchanged parameters', async () => {
        const pending = deferred()
        api.get.mockReturnValue(pending.promise)
        const { result } = renderHook(() => usePosDashboardData('week'))
        expect(api.get.mock.calls).toEqual(urls.map(url => [url]))
        expect(result.current.stats.data).toBeNull()
        expect(result.current.stock.pending).toBe(true)
    })
    it.each(['stats', 'trend', 'products', 'sales', 'stock', 'categories', 'payments'])('isolates failure of %s and retries only that endpoint', async name => {
        const failedUrl = POS_SOURCES[name].url('week')
        api.get.mockImplementation(url => url === failedUrl ? Promise.reject(Error('offline')) : Promise.resolve(response(url)))
        const { result } = renderHook(() => usePosDashboardData('week'))
        await waitFor(() => expect(result.current[name].error).toBe('failed'))
        expect(result.current[name].data).toBeNull()
        for (const key of Object.keys(POS_SOURCES).filter(key => key !== name)) {
            await waitFor(() => expect(result.current[key].data).not.toBeNull())
        }
        api.get.mockImplementation(async url => response(url))
        await act(() => result.current[name].retry())
        expect(api.get).toHaveBeenCalledTimes(8)
        expect(api.get.mock.lastCall).toEqual([failedUrl])
        expect(result.current[name].error).toBeNull()
    })
    it('preserves backend signed money without recomputation and distinguishes empty success', async () => {
        const { result } = renderHook(() => usePosDashboardData('week'))
        await waitFor(() => expect(result.current.stats.data).toEqual(stats))
        expect(result.current.stock.data).toEqual([])
        expect(result.current.stock.error).toBeNull()
        expect(result.current.stats.data.outstandingReceivables).toBe(-55)
    })
    it('accepts genuine zero stats and zero/negative series', async () => {
        const zero = Object.fromEntries(Object.keys(stats).map(key => [key, 0]))
        api.get.mockImplementation(async url => ({ data: url.endsWith('/stats') ? zero : url.includes('sales-trend') ? { data: [{ date: '2026-11-19T00:00:00', label: 'Thu', amount: 0 }, { date: '2026-11-20', label: 'Fri', amount: -7 }] } : [] }))
        const { result } = renderHook(() => usePosDashboardData('week'))
        await waitFor(() => expect(result.current.stats.data).toEqual(zero))
        expect(result.current.trend.data[1].amount).toBe(-7)
    })
    it.each(Object.keys(POS_SOURCES))('rejects malformed %s rather than reporting zero/empty', async name => {
        api.get.mockImplementation(async url => url === POS_SOURCES[name].url('week') ? { data: name === 'stats' ? { ...stats, todaySales: null } : [{}] } : response(url))
        const { result } = renderHook(() => usePosDashboardData('week'))
        await waitFor(() => expect(result.current[name].error).toBe('malformed'))
        expect(result.current[name].data).toBeNull()
    })
    it('retains same-scope stale values after failure and clears their error on recovery', async () => {
        const { result } = renderHook(() => usePosDashboardData('week'))
        await waitFor(() => expect(result.current.stats.data).toEqual(stats))
        const timestamp = result.current.stats.lastSuccess
        api.get.mockRejectedValue(Error('offline'))
        await act(() => result.current.stats.retry())
        expect(result.current.stats.data).toEqual(stats)
        expect(result.current.stats.lastSuccess).toBe(timestamp)
        expect(result.current.stats.error).toBe('failed')
        api.get.mockImplementation(async url => response(url))
        await act(() => result.current.stats.retry())
        expect(result.current.stats.error).toBeNull()
    })
    it.each([{ ...actor, tenantId: 'tenant-b' }, { ...actor, id: 'actor-b' }, { ...actor, effectivePermissions: ['dashboard.view', 'inventory.view'] }])('hides previous authority synchronously and rejects late results', async nextActor => {
        const late = deferred()
        const { result } = renderHook(() => usePosDashboardData('week'))
        await waitFor(() => expect(result.current.stats.data).toEqual(stats))
        api.get.mockReturnValue(late.promise)
        act(() => useAuthStore.setState({ user: nextActor }))
        expect(result.current.stats.data).toBeNull()
        expect(result.current.stats.pending).toBe(true)
        await act(() => late.resolve({ data: { ...stats, todaySales: 77 } }))
        expect(result.current.stats.data.todaySales).toBe(77)
    })
    it('clears trend on period change, refetches all seven, and ignores earlier period responses', async () => {
        const old = deferred(), next = deferred()
        const { result, rerender } = renderHook(({ period }) => usePosDashboardData(period), { initialProps: { period: 'week' } })
        await waitFor(() => expect(result.current.trend.data).toEqual([]))
        api.get.mockImplementation(url => url.includes('period=week') ? old.promise : url.includes('period=month') ? next.promise : Promise.resolve(response(url)))
        act(() => result.current.refresh())
        rerender({ period: 'month' })
        expect(result.current.trend.data).toBeNull()
        expect(api.get).toHaveBeenCalledTimes(21)
        await act(() => next.resolve({ data: { data: [{ date: '2026-11-19', label: 'Nov', amount: -8 }] } }))
        await act(() => old.resolve({ data: { data: [{ date: '2026-11-18', label: 'Old', amount: 999 }] } }))
        expect(result.current.trend.data[0].amount).toBe(-8)
    })
    it('refreshes every 60 seconds, restarts on manual refresh, and cleans up on unmount', async () => {
        vi.useFakeTimers()
        const { result, unmount } = renderHook(() => usePosDashboardData('week'))
        await act(async () => {})
        expect(api.get).toHaveBeenCalledTimes(7)
        await act(() => vi.advanceTimersByTimeAsync(60000))
        expect(api.get).toHaveBeenCalledTimes(14)
        act(() => result.current.refresh())
        await act(async () => {})
        expect(api.get).toHaveBeenCalledTimes(21)
        unmount()
        await act(() => vi.advanceTimersByTimeAsync(60000))
        expect(api.get).toHaveBeenCalledTimes(21)
    })
    it('protects overlapping retries and unmount cleanup', async () => {
        const first = deferred(), second = deferred()
        const load = vi.fn().mockReturnValueOnce(first.promise).mockReturnValueOnce(second.promise)
        const valid = () => true
        const { result, unmount } = renderHook(() => useDashboardSource({ scope: 'a', load, valid }))
        act(() => { void result.current.retry() })
        await act(() => second.resolve([2]))
        await act(() => first.resolve([1]))
        expect(result.current.data).toEqual([2])
        unmount()
    })
    it('rejects an in-flight prior tenant response even when it settles after the new tenant', async () => {
        const old = deferred(), next = deferred()
        api.get.mockImplementation(url => url.endsWith('/stats') ? old.promise : Promise.resolve(response(url)))
        const { result } = renderHook(() => usePosDashboardData('week'))
        api.get.mockImplementation(url => url.endsWith('/stats') ? next.promise : Promise.resolve(response(url)))
        act(() => useAuthStore.setState({ user: { ...actor, tenantId: 'new' } }))
        await act(() => next.resolve({ data: { ...stats, todaySales: 7 } }))
        await act(() => old.resolve({ data: { ...stats, todaySales: 999 } }))
        expect(result.current.stats.data.todaySales).toBe(7)
    })
    it('invalidates yesterday at UTC midnight instead of relabelling stale prior-day data as today', async () => {
        vi.useFakeTimers()
        vi.setSystemTime(new Date('2026-11-19T23:59:59Z'))
        const { result } = renderHook(() => usePosDashboardData('week'))
        await act(async () => {})
        expect(result.current.stats.data).toEqual(stats)
        api.get.mockRejectedValue(Error('offline'))
        await act(() => vi.advanceTimersByTimeAsync(1000))
        expect(result.current.stats.data).toBeNull()
        expect(result.current.stats.error).toBe('failed')
        expect(api.get).toHaveBeenCalledTimes(14)
    })
    it('does not fetch financial or sales sources without their permissions', async () => {
        useAuthStore.setState({ user: { ...actor, effectivePermissions: ['dashboard.view'] } })
        const { result } = renderHook(() => usePosDashboardData('week'))
        await waitFor(() => expect(result.current.stock.data).toEqual([]))
        expect(api.get.mock.calls.map(([url]) => url)).toEqual(['/dashboard/stats', '/dashboard/low-stock?limit=8'])
        expect(result.current.trend.data).toBeNull()
        expect(result.current.sales.error).toBe('restricted')
    })
    it.each([401, 403])('discards last-known financial data on HTTP %s', async status => {
        const { result } = renderHook(() => usePosDashboardData('week'))
        await waitFor(() => expect(result.current.stats.data).toEqual(stats))
        api.get.mockRejectedValue({ response: { status } })
        await act(() => result.current.stats.retry())
        expect(result.current.stats.data).toBeNull()
        expect(result.current.stats.lastSuccess).toBeNull()
        expect(result.current.stats.error).toBe('unauthorized')
    })
})
