import { act, cleanup, fireEvent, render, screen } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import StorefrontLiveMonitoring from './StorefrontLiveMonitoring'
import storefrontAnalyticsApi from '../services/storefrontAnalyticsApi'
import { useAuthStore } from '../store/authStore'
import en from '../i18n/locales/en.json'
vi.mock('../services/storefrontAnalyticsApi', () => ({ default: { getStorefrontLiveMonitoring: vi.fn() } }))
vi.mock('../hooks/useRTL', () => ({ useRTL: () => ({ locale: 'en-US' }) }))
vi.mock('react-i18next', () => ({ useTranslation: () => ({ t: (key, fallback) => key.split('.').reduce((value, part) => value?.[part], en) || fallback || key }) }))
const live = { liveVisitors: 12, activeCarts: 6, checkoutInProgress: 4, recentOrders: 10, recentActivity: [] }
beforeEach(() => {
    vi.clearAllMocks()
    storefrontAnalyticsApi.getStorefrontLiveMonitoring.mockResolvedValue(live)
    useAuthStore.setState({ user: { id: 'a', tenantId: 'a' } })
})
afterEach(() => { cleanup(); vi.useRealTimers() })
describe('Storefront independent live presentation', () => {
    it('preserves default consumer appearance', async () => {
        const { container } = render(<StorefrontLiveMonitoring />)
        await screen.findByText('12')
        expect(container.querySelector('.bg-gradient-to-br')).not.toBeNull()
        expect(container.querySelector('.pos-dashboard__live')).toBeNull()
    })
    it('labels the unrestricted count and retains independent ten-second/manual refresh and cleanup', async () => {
        vi.useFakeTimers()
        const { unmount } = render(<StorefrontLiveMonitoring presentation="dashboard" />)
        await act(async () => {})
        expect(screen.getByText(en.dashboard.phase42.storefront.recentOrders)).toBeInTheDocument()
        expect(screen.getByText('10')).toBeInTheDocument()
        await act(() => vi.advanceTimersByTimeAsync(10000))
        expect(storefrontAnalyticsApi.getStorefrontLiveMonitoring).toHaveBeenCalledTimes(2)
        fireEvent.click(screen.getByRole('button', { name: 'Refresh' }))
        await act(async () => {})
        expect(storefrontAnalyticsApi.getStorefrontLiveMonitoring).toHaveBeenCalledTimes(3)
        unmount()
        await act(() => vi.advanceTimersByTimeAsync(10000))
        expect(storefrontAnalyticsApi.getStorefrontLiveMonitoring).toHaveBeenCalledTimes(3)
    })
    it('does not report zero counters or empty activity on failure, supports retry and rejects malformed data', async () => {
        storefrontAnalyticsApi.getStorefrontLiveMonitoring.mockRejectedValueOnce(Error('offline')).mockResolvedValueOnce({ ...live, recentOrders: null })
        render(<StorefrontLiveMonitoring presentation="dashboard" />)
        await screen.findByText(/not a confirmed zero/)
        expect(screen.queryByText('No recent events returned in the five-minute window.')).not.toBeInTheDocument()
        expect(screen.queryByText('0')).not.toBeInTheDocument()
        fireEvent.click(screen.getByRole('button', { name: 'Retry' }))
        await screen.findByText(/unusable data/)
        fireEvent.click(screen.getByRole('button', { name: 'Retry' }))
        await screen.findByText('12')
    })
})
