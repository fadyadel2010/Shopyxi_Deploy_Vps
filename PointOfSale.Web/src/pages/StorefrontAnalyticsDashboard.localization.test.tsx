import { cleanup, render, screen, waitFor } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import i18n from '../i18n/config'
import StorefrontAnalyticsDashboard from './StorefrontAnalyticsDashboard'
import storefrontAnalyticsApi from '../services/storefrontAnalyticsApi'

const analytics = vi.mocked(storefrontAnalyticsApi)

vi.mock('../services/storefrontAnalyticsApi', () => ({ default: { getStorefrontAnalyticsDashboard: vi.fn() } }))
vi.mock('../components/StorefrontLiveMonitoring', () => ({ default: () => <div data-testid="live" /> }))
vi.mock('../contexts/SettingsContext', () => ({ useSettings: () => ({ formatCurrency: (value: number) => `EGP ${value}` }) }))

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

const ANALYTICS_KEYS = [
    'title', 'subtitle', 'visits', 'uniqueSessions', 'orders', 'storefrontOrders', 'revenue',
    'grossRevenue', 'conversionRate', 'visitsToOrders', 'addToCart', 'cartIntent',
    'checkoutStarted', 'checkoutIntent', 'abandonedOrders', 'abandonedRevenue',
    'averageOrderValue', 'revenuePerOrder', 'topSellingProducts', 'topSellingProductsSubtitle',
    'noTopProducts', 'unitsSold', 'funnelOverview', 'funnelOverviewSubtitle', 'pageView',
    'viewProduct', 'beginCheckout', 'purchaseCompleted',
]

beforeEach(async () => {
    vi.clearAllMocks()
    await i18n.changeLanguage('en')
    analytics.getStorefrontAnalyticsDashboard.mockResolvedValue(summary)
})

afterEach(async () => {
    cleanup()
    await i18n.changeLanguage('en')
})

describe('default Storefront Analytics localization', () => {
    it.each(['en', 'ar'] as const)('resolves every storefrontAnalytics key in %s', language => {
        for (const key of ANALYTICS_KEYS) {
            expect(i18n.t(`storefrontAnalytics.${key}`, { lng: language }), key).not.toBe(`storefrontAnalytics.${key}`)
        }
        for (const range of ['today', '7d', '30d', 'this_month']) {
            expect(i18n.t(`storefrontAnalytics.ranges.${range}`, { lng: language })).not.toBe(`storefrontAnalytics.ranges.${range}`)
        }
    })

    it('renders Arabic labels instead of English inline fallbacks', async () => {
        await i18n.changeLanguage('ar')
        render(<StorefrontAnalyticsDashboard />)

        expect(await screen.findByText('تحليلات المتجر الإلكتروني')).toBeInTheDocument()
        expect(screen.getByText('الزيارات')).toBeInTheDocument()
        expect(screen.getByText('الطلبات')).toBeInTheDocument()
        expect(screen.getByText('المنتجات الأكثر مبيعاً')).toBeInTheDocument()
        expect(screen.getByText('نظرة عامة على مسار الشراء')).toBeInTheDocument()
        expect(screen.queryByText('Visits')).not.toBeInTheDocument()
        expect(document.body.textContent).not.toContain('storefrontAnalytics.')
    })

    it('renders English labels and no raw keys', async () => {
        render(<StorefrontAnalyticsDashboard />)

        expect(await screen.findByText('Storefront Analytics')).toBeInTheDocument()
        expect(screen.getByText('Visits')).toBeInTheDocument()
        expect(screen.getByText('Top Selling Products')).toBeInTheDocument()
        await waitFor(() => expect(document.body.textContent).not.toContain('storefrontAnalytics.'))
    })
})
