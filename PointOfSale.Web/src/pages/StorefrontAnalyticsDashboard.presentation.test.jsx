import { act, cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import StorefrontAnalyticsDashboard from './StorefrontAnalyticsDashboard'
import storefrontAnalyticsApi from '../services/storefrontAnalyticsApi'
import { useAuthStore } from '../store/authStore'
import en from '../i18n/locales/en.json'

vi.mock('../services/storefrontAnalyticsApi', () => ({ default: { getStorefrontAnalyticsDashboard: vi.fn() } }))
vi.mock('../components/StorefrontLiveMonitoring', () => ({ default: ({ presentation }) => <div data-testid="live" data-presentation={presentation}>Independent live view</div> }))
vi.mock('../hooks/useRTL', () => ({ useRTL: () => ({ locale: 'en-US', dir: 'ltr' }) }))
vi.mock('react-i18next', () => ({ useTranslation: () => ({ t: (key, fallback) => key.split('.').reduce((value, part) => value?.[part], en) || fallback || key }) }))
vi.mock('../contexts/SettingsContext', () => ({ useSettings: () => ({ formatCurrency: value => `EGP ${value}` }) }))

const summary = { visits: 321, orders: 7, revenue: -123.45, conversionRate: 2.18, addToCart: 55, checkoutStarted: 22, abandonedCheckouts: 9, abandonedRevenue: 88.12, averageOrderValue: -17.64, topSellingProducts: [], funnel: { pageView: 999, viewProduct: 200, addToCart: 55, beginCheckout: 22, purchaseCompleted: 7 } }
beforeEach(() => {
    vi.clearAllMocks()
    storefrontAnalyticsApi.getStorefrontAnalyticsDashboard.mockResolvedValue(summary)
    useAuthStore.setState({ user: { id: 'a', tenantId: 'a' } })
})
afterEach(cleanup)

describe('Storefront root-only presentation', () => {
    it('defaults to the existing direct-route presentation, ranges and live consumer', async () => {
        const { container } = render(<StorefrontAnalyticsDashboard />)
        await screen.findByText('EGP -123.45')
        expect(container.querySelector('.bg-gradient-to-br')).not.toBeNull()
        expect(container.querySelector('.pos-dashboard__storefront')).toBeNull()
        expect(screen.getByTestId('live')).not.toHaveAttribute('data-presentation')
        expect(storefrontAnalyticsApi.getStorefrontAnalyticsDashboard).toHaveBeenCalledWith('7d')
    })
    it('opts into neutral presentation, correct source copy and unchanged backend values', async () => {
        const { container } = render(<StorefrontAnalyticsDashboard presentation="dashboard" />)
        await screen.findByText('EGP -123.45')
        expect(container.querySelector('.bg-gradient-to-br')).toBeNull()
        expect(screen.getByTestId('live')).toHaveAttribute('data-presentation', 'dashboard')
        expect(screen.getByText('Abandoned cart value:', { exact: false })).toHaveTextContent('EGP 88.12')
        expect(screen.getByText(/not a cohort conversion cascade/)).toBeInTheDocument()
        expect(screen.getByText('EGP -17.64')).toBeInTheDocument()
        expect(screen.getByRole('button', { name: '7 days' })).toHaveAttribute('aria-pressed', 'true')
    })
    it('keeps range controls and live view available on failure; does not render confirmed zeros', async () => {
        storefrontAnalyticsApi.getStorefrontAnalyticsDashboard.mockRejectedValue(Error('offline'))
        render(<StorefrontAnalyticsDashboard presentation="dashboard" />)
        expect(await screen.findByText(/not a confirmed zero/)).toBeInTheDocument()
        expect(screen.queryByText('EGP 0')).not.toBeInTheDocument()
        expect(screen.getByTestId('live')).toBeInTheDocument()
        expect(screen.getByRole('button', { name: 'Today' })).toBeEnabled()
        storefrontAnalyticsApi.getStorefrontAnalyticsDashboard.mockResolvedValue(summary)
        fireEvent.click(screen.getAllByRole('button', { name: 'Retry' })[0])
        expect(await screen.findByText('EGP -123.45')).toBeInTheDocument()
    })
    it('never relabels old values under a failed new period, ignores a late previous range, and hides prior actor', async () => {
        let oldResolve
        render(<StorefrontAnalyticsDashboard presentation="dashboard" />)
        await screen.findByText('EGP -123.45')
        storefrontAnalyticsApi.getStorefrontAnalyticsDashboard.mockImplementationOnce(() => new Promise(resolve => { oldResolve = resolve }))
            .mockRejectedValueOnce(Error('offline'))
        fireEvent.click(screen.getByRole('button', { name: '30 days' }))
        expect(screen.queryByText('EGP -123.45')).not.toBeInTheDocument()
        fireEvent.click(screen.getByRole('button', { name: 'Today' }))
        await screen.findByText(/not a confirmed zero/)
        await act(() => oldResolve(summary))
        expect(screen.queryByText('EGP -123.45')).not.toBeInTheDocument()
        storefrontAnalyticsApi.getStorefrontAnalyticsDashboard.mockResolvedValue(summary)
        fireEvent.click(screen.getAllByRole('button', { name: 'Retry' })[0])
        await screen.findByText('EGP -123.45')
        storefrontAnalyticsApi.getStorefrontAnalyticsDashboard.mockReturnValue(new Promise(() => {}))
        act(() => useAuthStore.setState({ user: { id: 'b', tenantId: 'b' } }))
        expect(screen.queryByText('EGP -123.45')).not.toBeInTheDocument()
    })
    it('marks malformed summary unavailable and retains same-period stale data on update failure', async () => {
        storefrontAnalyticsApi.getStorefrontAnalyticsDashboard.mockResolvedValueOnce({ ...summary, revenue: null })
        render(<StorefrontAnalyticsDashboard presentation="dashboard" />)
        await screen.findAllByText(/unusable data/)
        fireEvent.click(screen.getAllByRole('button', { name: 'Retry' })[0])
        await screen.findByText('EGP -123.45')
        storefrontAnalyticsApi.getStorefrontAnalyticsDashboard.mockRejectedValue(Error('offline'))
        fireEvent.click(screen.getByRole('button', { name: 'Refresh' }))
        await waitFor(() => expect(screen.getAllByText(/last successful fetch/).length).toBeGreaterThan(0))
        expect(screen.getByText('EGP -123.45')).toBeInTheDocument()
    })
})
