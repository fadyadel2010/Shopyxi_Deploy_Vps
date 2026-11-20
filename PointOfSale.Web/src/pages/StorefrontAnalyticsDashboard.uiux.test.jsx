import { cleanup, fireEvent, render, screen } from '@testing-library/react'
import { afterEach, expect, it, vi } from 'vitest'
import StorefrontAnalyticsDashboard from './StorefrontAnalyticsDashboard'
import en from '../i18n/locales/en.json'

vi.mock('../services/storefrontAnalyticsApi', () => ({ default: { getStorefrontAnalyticsDashboard: async () => ({
    visits: 1, orders: 0, revenue: 0, conversionRate: 0, addToCart: 0, checkoutStarted: 0,
    abandonedCheckouts: 0, abandonedRevenue: 0, averageOrderValue: 0,
    topSellingProducts: [{ productName: 'Zero product', sales: 0 }, { productName: 'One product', sales: 1 }],
    funnel: { pageView: 1, viewProduct: 0, addToCart: 0, beginCheckout: 0, purchaseCompleted: 0 },
}) } }))
vi.mock('../components/StorefrontLiveMonitoring', () => ({ default: () => null }))
vi.mock('react-i18next', () => ({ useTranslation: () => ({ t: (key, fallback) => key.split('.').reduce((v, k) => v?.[k], en) || fallback || key }) }))
vi.mock('../contexts/SettingsContext', () => ({ useSettings: () => ({ formatCurrency: v => `MONEY ${v}` }) }))
vi.mock('../hooks/useRTL', () => ({ useRTL: () => ({ locale: 'en-US', dir: 'ltr' }) }))
afterEach(cleanup)
it('exposes the selected range, allows wrapping, and does not draw nonzero bars for zero data', async () => {
    const { container } = render(<StorefrontAnalyticsDashboard />)
    await screen.findByText('Zero product')
    const group = screen.getByRole('group')
    expect(group).toHaveClass('flex-wrap', 'max-w-full')
    expect(screen.getByRole('button', { name: '7D' })).toHaveAttribute('aria-pressed', 'true')
    fireEvent.click(screen.getByRole('button', { name: 'Today' }))
    expect(screen.getByRole('button', { name: 'Today' })).toHaveAttribute('aria-pressed', 'true')
    await screen.findByText('Zero product')
    const bars = [...container.querySelectorAll('[style]')].filter(element => element.style.width)
    expect(bars.filter(element => element.style.width === '0%')).toHaveLength(5)
    expect(bars.filter(element => element.style.width === '100%')).toHaveLength(2)
})
