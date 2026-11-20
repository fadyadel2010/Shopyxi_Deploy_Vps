/**
 * Group 5B — D-1 regression protection (standalone `/analytics/storefront` surface).
 *
 * A failed, unavailable or malformed live-monitoring response must never render a
 * healthy-looking grid of zero counters, and a genuine zero-valued response must
 * still render as zero.
 */
import { act, cleanup, fireEvent, render, screen } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import StorefrontLiveMonitoring from './StorefrontLiveMonitoring'
import storefrontAnalyticsApi from '../services/storefrontAnalyticsApi'
import { useAuthStore } from '../store/authStore'
import en from '../i18n/locales/en.json'
import ar from '../i18n/locales/ar.json'

const platform = { catalog: en }

vi.mock('../services/storefrontAnalyticsApi', () => ({ default: { getStorefrontLiveMonitoring: vi.fn() } }))
vi.mock('../hooks/useRTL', () => ({ useRTL: () => ({ locale: 'en-US' }) }))
vi.mock('react-i18next', () => ({
    useTranslation: () => ({
        t: (key, fallback) => key.split('.').reduce((value, part) => value?.[part], platform.catalog) || fallback || key,
    }),
}))

const live = { liveVisitors: 12, activeCarts: 6, checkoutInProgress: 4, recentOrders: 10, recentActivity: [] }
const zeroLive = { liveVisitors: 0, activeCarts: 0, checkoutInProgress: 0, recentOrders: 0, recentActivity: [] }

beforeEach(() => {
    vi.resetAllMocks()
    platform.catalog = en
    storefrontAnalyticsApi.getStorefrontLiveMonitoring.mockResolvedValue(live)
    useAuthStore.setState({ user: { id: 'a', tenantId: 'a' } })
})

afterEach(() => { cleanup(); vi.useRealTimers() })

describe('standalone live monitoring failure states', () => {
    it('renders a successful response with non-zero counters', async () => {
        render(<StorefrontLiveMonitoring />)

        expect(await screen.findByText('12')).toBeInTheDocument()
        expect(screen.getByText(en.storefrontLive.activeCarts)).toBeInTheDocument()
        expect(screen.getByText('No recent storefront activity in the live window.')).toBeInTheDocument()
    })

    it('preserves genuine zero counters from a successful response', async () => {
        storefrontAnalyticsApi.getStorefrontLiveMonitoring.mockResolvedValue(zeroLive)
        render(<StorefrontLiveMonitoring />)

        await screen.findByText(en.storefrontLive.activeCarts)
        expect(screen.getAllByText('0')).toHaveLength(4)
        expect(screen.queryByText(en.storefrontLive.unavailableTitle)).not.toBeInTheDocument()
    })

    it('shows loading without counters before a response exists', () => {
        storefrontAnalyticsApi.getStorefrontLiveMonitoring.mockReturnValue(new Promise(() => {}))
        render(<StorefrontLiveMonitoring />)

        expect(screen.getByText(en.common.loading)).toBeInTheDocument()
        expect(screen.queryByText(en.storefrontLive.liveVisitors)).not.toBeInTheDocument()
        expect(screen.queryAllByText('0')).toHaveLength(0)
    })

    it('does not render zero counters for an initial failure and offers retry', async () => {
        storefrontAnalyticsApi.getStorefrontLiveMonitoring.mockRejectedValueOnce(new Error('offline'))
        render(<StorefrontLiveMonitoring />)

        expect(await screen.findByText(en.storefrontLive.unavailableTitle)).toBeInTheDocument()
        expect(screen.getByText(en.storefrontLive.unavailableMessage)).toBeInTheDocument()
        expect(screen.queryByText(en.storefrontLive.liveVisitors)).not.toBeInTheDocument()
        expect(screen.queryByText('0')).not.toBeInTheDocument()
        expect(screen.queryByText('No recent storefront activity in the live window.')).not.toBeInTheDocument()
        expect(screen.getByRole('button', { name: en.common.retry })).toBeInTheDocument()
    })

    it('treats a malformed response as unusable instead of zero', async () => {
        storefrontAnalyticsApi.getStorefrontLiveMonitoring.mockResolvedValue({ ...live, recentOrders: null })
        render(<StorefrontLiveMonitoring />)

        expect(await screen.findByText(en.storefrontLive.malformed)).toBeInTheDocument()
        expect(screen.getByText(en.storefrontLive.malformedMessage)).toBeInTheDocument()
        expect(screen.queryByText('12')).not.toBeInTheDocument()
        expect(screen.queryByText('0')).not.toBeInTheDocument()
    })

    it('keeps the last successful counters, labelled stale, when a refresh fails', async () => {
        storefrontAnalyticsApi.getStorefrontLiveMonitoring
            .mockResolvedValueOnce(live)
            .mockRejectedValueOnce(new Error('offline'))
        render(<StorefrontLiveMonitoring />)

        await screen.findByText('12')
        fireEvent.click(screen.getByRole('button', { name: en.common.refresh }))

        expect(await screen.findByText(en.storefrontLive.stale)).toBeInTheDocument()
        expect(screen.getByText('12')).toBeInTheDocument()
        // Last-known-good counters stay visible with their staleness stated explicitly.
        expect(screen.getByText(en.storefrontLive.liveVisitors)).toBeInTheDocument()
        expect(screen.queryByText(en.storefrontLive.unavailableMessage)).not.toBeInTheDocument()
    })

    it('recovers to confirmed counters when a retry succeeds', async () => {
        storefrontAnalyticsApi.getStorefrontLiveMonitoring
            .mockRejectedValueOnce(new Error('offline'))
            .mockResolvedValueOnce(live)
        render(<StorefrontLiveMonitoring />)

        await screen.findByText(en.storefrontLive.unavailableTitle)
        fireEvent.click(screen.getByRole('button', { name: en.common.retry }))

        expect(await screen.findByText('12')).toBeInTheDocument()
        expect(screen.queryByText(en.storefrontLive.unavailableTitle)).not.toBeInTheDocument()
        expect(storefrontAnalyticsApi.getStorefrontLiveMonitoring).toHaveBeenCalledTimes(2)
    })

    it('localizes the unavailable state in Arabic', async () => {
        platform.catalog = ar
        storefrontAnalyticsApi.getStorefrontLiveMonitoring.mockRejectedValueOnce(new Error('offline'))
        render(<StorefrontLiveMonitoring />)

        expect(await screen.findByText(ar.storefrontLive.unavailableTitle)).toBeInTheDocument()
        expect(screen.queryByText('0')).not.toBeInTheDocument()
    })

    it('keeps standalone and Dashboard presentation state independent', async () => {
        storefrontAnalyticsApi.getStorefrontLiveMonitoring.mockRejectedValueOnce(new Error('offline'))
        render(<StorefrontLiveMonitoring />)
        await screen.findByText(en.storefrontLive.unavailableTitle)

        storefrontAnalyticsApi.getStorefrontLiveMonitoring.mockResolvedValue(live)
        render(<StorefrontLiveMonitoring presentation="dashboard" />)
        await screen.findByText('12')

        // The standalone instance keeps its own failure state; the root instance is confirmed.
        expect(screen.getByText(en.storefrontLive.unavailableTitle)).toBeInTheDocument()
        expect(screen.queryByText('0')).not.toBeInTheDocument()
        expect(screen.getByText(en.dashboard.phase42.storefront.liveVisitors)).toBeInTheDocument()
    })

    it('keeps the independent ten-second refresh cadence and cleans it up', async () => {
        vi.useFakeTimers()
        const { unmount } = render(<StorefrontLiveMonitoring />)
        await act(async () => {})
        expect(storefrontAnalyticsApi.getStorefrontLiveMonitoring).toHaveBeenCalledTimes(1)

        await act(() => vi.advanceTimersByTimeAsync(10000))
        expect(storefrontAnalyticsApi.getStorefrontLiveMonitoring).toHaveBeenCalledTimes(2)

        unmount()
        await act(() => vi.advanceTimersByTimeAsync(10000))
        expect(storefrontAnalyticsApi.getStorefrontLiveMonitoring).toHaveBeenCalledTimes(2)
    })
    it('removes counters on authority change and never renders technical server messages', async () => {
        storefrontAnalyticsApi.getStorefrontLiveMonitoring.mockResolvedValueOnce(live)
            .mockRejectedValueOnce({ response: { status: 403, data: { message: 'private internal detail' } } })
        render(<StorefrontLiveMonitoring />)
        await screen.findByText('12')
        act(() => useAuthStore.setState({ user: { id: 'b', tenantId: 'b' } }))
        expect(screen.queryByText('12')).not.toBeInTheDocument()
        await screen.findByText(en.storefrontLive.unavailableTitle)
        expect(screen.queryByText('private internal detail')).not.toBeInTheDocument()
    })
})
