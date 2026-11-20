import { cleanup, fireEvent, render, screen } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import StorefrontAnalyticsDashboard from './StorefrontAnalyticsDashboard'
import storefrontAnalyticsApi from '../services/storefrontAnalyticsApi'

const analytics = vi.mocked(storefrontAnalyticsApi)

vi.mock('../services/storefrontAnalyticsApi', () => ({ default: { getStorefrontAnalyticsDashboard: vi.fn() } }))
vi.mock('../components/StorefrontLiveMonitoring', () => ({ default: () => <div data-testid="live" /> }))
vi.mock('../contexts/SettingsContext', () => ({ useSettings: () => ({ formatCurrency: (value: number) => `EGP ${value}` }) }))
vi.mock('react-i18next', () => ({ useTranslation: () => ({ t: (key: string) => key, i18n: { resolvedLanguage: 'en', language: 'en' } }) }))

const summary = {
    visits: 321,
    orders: 7,
    revenue: 123.45,
    conversionRate: 2.18,
    addToCart: 55,
    checkoutStarted: 22,
    abandonedCheckouts: 9,
    abandonedRevenue: 88.12,
    averageOrderValue: 17.64,
    topSellingProducts: [{ productName: 'Demo product', sales: 3 }],
    funnel: { pageView: 999, viewProduct: 200, addToCart: 55, beginCheckout: 22, purchaseCompleted: 7 },
}

const emptySummary = {
    ...summary,
    visits: 0, orders: 0, revenue: 0, conversionRate: 0, addToCart: 0, checkoutStarted: 0,
    abandonedCheckouts: 0, abandonedRevenue: 0, averageOrderValue: 0,
    topSellingProducts: [],
    funnel: { pageView: 0, viewProduct: 0, addToCart: 0, beginCheckout: 0, purchaseCompleted: 0 },
}

const kpiLabels = ['storefrontAnalytics.visits', 'storefrontAnalytics.orders', 'storefrontAnalytics.revenue', 'storefrontAnalytics.conversionRate']

beforeEach(() => {
    vi.clearAllMocks()
})

afterEach(cleanup)

describe('default Storefront Analytics failure states', () => {
    it('shows loading without metric cards before a response exists', () => {
        analytics.getStorefrontAnalyticsDashboard.mockReturnValue(new Promise(() => {}))
        render(<StorefrontAnalyticsDashboard />)

        expect(screen.getByText('common.loading')).toBeInTheDocument()
        for (const label of kpiLabels) expect(screen.queryByText(label)).not.toBeInTheDocument()
    })

    it('renders a successful non-empty response', async () => {
        analytics.getStorefrontAnalyticsDashboard.mockResolvedValue(summary)
        render(<StorefrontAnalyticsDashboard />)

        await screen.findByText('321')
        for (const label of kpiLabels) expect(screen.getByText(label)).toBeInTheDocument()
        expect(screen.queryByText('storefrontAnalytics.unavailableTitle')).not.toBeInTheDocument()
        expect(screen.getByText('Demo product')).toBeInTheDocument()
    })

    it('preserves a genuinely empty successful response instead of showing unavailable', async () => {
        analytics.getStorefrontAnalyticsDashboard.mockResolvedValue(emptySummary)
        render(<StorefrontAnalyticsDashboard />)

        await screen.findByText('storefrontAnalytics.noTopProducts')
        expect(screen.queryByText('storefrontAnalytics.unavailableTitle')).not.toBeInTheDocument()
        expect(screen.getByText('storefrontAnalytics.visits')).toBeInTheDocument()
        expect(screen.getAllByText('EGP 0').length).toBeGreaterThan(0)
    })

    it('never renders zero KPIs for a failed request and retries through the existing request path', async () => {
        analytics.getStorefrontAnalyticsDashboard.mockRejectedValueOnce(new Error('offline'))
        render(<StorefrontAnalyticsDashboard />)

        expect(await screen.findByText('storefrontAnalytics.unavailableTitle')).toBeInTheDocument()
        expect(screen.getByText('storefrontAnalytics.unavailableMessage')).toBeInTheDocument()
        for (const label of kpiLabels) expect(screen.queryByText(label)).not.toBeInTheDocument()
        expect(screen.queryByText('EGP 0')).not.toBeInTheDocument()
        expect(screen.getByTestId('live')).toBeInTheDocument()

        analytics.getStorefrontAnalyticsDashboard.mockResolvedValue(summary)
        fireEvent.click(screen.getByRole('button', { name: 'common.retry' }))

        await screen.findByText('321')
        expect(storefrontAnalyticsApi.getStorefrontAnalyticsDashboard).toHaveBeenCalledTimes(2)
        expect(screen.queryByText('storefrontAnalytics.unavailableTitle')).not.toBeInTheDocument()
    })

    it('presents an authorization failure as access denied, not empty analytics', async () => {
        analytics.getStorefrontAnalyticsDashboard.mockRejectedValue({ response: { status: 403 } })
        render(<StorefrontAnalyticsDashboard />)

        expect(await screen.findByText('storefrontAnalytics.accessDenied')).toBeInTheDocument()
        expect(screen.getByText('storefrontAnalytics.accessDeniedMessage')).toBeInTheDocument()
        for (const label of kpiLabels) expect(screen.queryByText(label)).not.toBeInTheDocument()
        expect(screen.queryByText('storefrontAnalytics.noTopProducts')).not.toBeInTheDocument()
    })

    it('rejects malformed payloads instead of coercing them to zero', async () => {
        analytics.getStorefrontAnalyticsDashboard.mockResolvedValue({ ...summary, revenue: null })
        render(<StorefrontAnalyticsDashboard />)

        expect(await screen.findByText('storefrontAnalytics.malformed')).toBeInTheDocument()
        for (const label of kpiLabels) expect(screen.queryByText(label)).not.toBeInTheDocument()
        expect(screen.queryByText('EGP 0')).not.toBeInTheDocument()
    })

    it('keeps the last successful result visible and labelled stale on a later failure', async () => {
        analytics.getStorefrontAnalyticsDashboard.mockResolvedValue(summary)
        render(<StorefrontAnalyticsDashboard />)
        await screen.findByText('321')

        analytics.getStorefrontAnalyticsDashboard.mockRejectedValueOnce(new Error('offline'))
        fireEvent.click(screen.getByRole('button', { name: 'common.refresh' }))

        expect(await screen.findByText('storefrontAnalytics.staleResult')).toBeInTheDocument()
        expect(screen.getByText('321')).toBeInTheDocument()
    })

    it('does not change the opted-in dashboard presentation failure contract', async () => {
        analytics.getStorefrontAnalyticsDashboard.mockRejectedValue(new Error('offline'))
        render(<StorefrontAnalyticsDashboard presentation="dashboard" />)

        expect(await screen.findByText('dashboard.phase42.failed')).toBeInTheDocument()
        expect(screen.getByTestId('live')).toBeInTheDocument()
    })
})
