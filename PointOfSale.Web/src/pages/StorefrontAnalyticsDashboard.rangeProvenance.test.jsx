/**
 * Group 5B — D-2 regression protection (standalone `/analytics/storefront`).
 *
 * A successful analytics response is financial data for one exact range. After a
 * range change the previous range's figures must never be presented as if they
 * belonged to the newly selected range.
 */
import { act, cleanup, fireEvent, render, screen } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import StorefrontAnalyticsDashboard from './StorefrontAnalyticsDashboard'
import storefrontAnalyticsApi from '../services/storefrontAnalyticsApi'
import { useAuthStore } from '../store/authStore'
import en from '../i18n/locales/en.json'
import ar from '../i18n/locales/ar.json'

const platform = { catalog: en }

vi.mock('../services/storefrontAnalyticsApi', () => ({ default: { getStorefrontAnalyticsDashboard: vi.fn() } }))
vi.mock('../components/StorefrontLiveMonitoring', () => ({ default: () => <div data-testid="live" /> }))
vi.mock('../hooks/useRTL', () => ({ useRTL: () => ({ locale: 'en-US', dir: 'ltr' }) }))
vi.mock('../contexts/SettingsContext', () => ({ useSettings: () => ({ formatCurrency: (value) => `EGP ${value}` }) }))
vi.mock('react-i18next', () => ({
    useTranslation: () => ({
        t: (key, fallback) => key.split('.').reduce((value, part) => value?.[part], platform.catalog) || fallback || key,
    }),
}))

// Every counter is distinct so an assertion can name exactly one range's figure.
const summary = (visits, revenue, orders) => ({
    visits,
    orders,
    revenue,
    conversionRate: 2.18,
    addToCart: visits + 100,
    checkoutStarted: visits + 200,
    abandonedCheckouts: 0,
    abandonedRevenue: 0,
    averageOrderValue: orders ? revenue / orders : 0,
    topSellingProducts: [],
    funnel: { pageView: visits + 300, viewProduct: visits + 400, addToCart: visits + 500, beginCheckout: visits + 600, purchaseCompleted: orders },
})

const rangeA = summary(321, 1111.5, 7)
const rangeB = summary(65432, 2222.25, 9)
const rangeC = summary(987, 33.5, 1)

beforeEach(() => {
    // reset (not clear): queued one-off responses must never leak between tests.
    vi.resetAllMocks()
    platform.catalog = en
    useAuthStore.setState({ user: { id: 'actor-a', tenantId: 'tenant-a' } })
})

afterEach(cleanup)

describe('standalone Storefront Analytics range provenance', () => {
    it('shows an initial successful range and preserves genuine zero values', async () => {
        storefrontAnalyticsApi.getStorefrontAnalyticsDashboard.mockResolvedValue(rangeA)
        render(<StorefrontAnalyticsDashboard />)

        await screen.findByText('321')
        expect(storefrontAnalyticsApi.getStorefrontAnalyticsDashboard).toHaveBeenCalledWith('7d')

        storefrontAnalyticsApi.getStorefrontAnalyticsDashboard.mockResolvedValue(summary(0, 0, 0))
        fireEvent.click(screen.getByRole('button', { name: 'Today' }))

        await screen.findByText(en.storefrontAnalytics.noTopProducts)
        expect(screen.getAllByText('EGP 0').length).toBeGreaterThan(0)
        expect(screen.queryByText(en.storefrontAnalytics.unavailableTitle)).not.toBeInTheDocument()
    })

    it('never attributes the previous range figures to a newly selected range that failed', async () => {
        storefrontAnalyticsApi.getStorefrontAnalyticsDashboard
            .mockResolvedValueOnce(rangeA)
            .mockRejectedValueOnce(Object.assign(new Error('offline'), { response: { status: 500 } }))
        render(<StorefrontAnalyticsDashboard />)

        await screen.findByText('321')
        fireEvent.click(screen.getByRole('button', { name: '30D' }))

        await screen.findByText(en.storefrontAnalytics.unavailableTitle)
        expect(storefrontAnalyticsApi.getStorefrontAnalyticsDashboard).toHaveBeenLastCalledWith('30d')
        expect(screen.queryByText('321')).not.toBeInTheDocument()
        expect(screen.queryByText('EGP 1111.5')).not.toBeInTheDocument()
        expect(screen.queryByText(en.storefrontAnalytics.staleResult)).not.toBeInTheDocument()
        expect(screen.getByText(en.storefrontAnalytics.unavailableMessage)).toBeInTheDocument()
    })

    it('retries the currently selected range and displays that range values', async () => {
        storefrontAnalyticsApi.getStorefrontAnalyticsDashboard
            .mockResolvedValueOnce(rangeA)
            .mockRejectedValueOnce(new Error('offline'))
            .mockResolvedValueOnce(rangeB)
        render(<StorefrontAnalyticsDashboard />)

        await screen.findByText('321')
        fireEvent.click(screen.getByRole('button', { name: '30D' }))
        await screen.findByText(en.storefrontAnalytics.unavailableTitle)

        fireEvent.click(screen.getAllByRole('button', { name: en.common.retry })[0])

        await screen.findByText('65,432')
        expect(storefrontAnalyticsApi.getStorefrontAnalyticsDashboard).toHaveBeenLastCalledWith('30d')
        expect(screen.queryByText('321')).not.toBeInTheDocument()
    })

    it('keeps a same-range refresh failure labelled with its own range', async () => {
        storefrontAnalyticsApi.getStorefrontAnalyticsDashboard
            .mockResolvedValueOnce(rangeA)
            .mockRejectedValueOnce(new Error('offline'))
        render(<StorefrontAnalyticsDashboard />)

        await screen.findByText('321')
        fireEvent.click(screen.getByRole('button', { name: en.common.refresh }))

        await screen.findByText(en.storefrontAnalytics.staleResult)
        expect(screen.getByText('321')).toBeInTheDocument()
    })

    it('ignores an older in-flight range response that resolves after the newer selection', async () => {
        let resolveFirstRange = () => {}
        let resolveSecondRange = () => {}
        storefrontAnalyticsApi.getStorefrontAnalyticsDashboard
            .mockImplementationOnce(() => new Promise(resolve => { resolveFirstRange = resolve }))
            .mockImplementationOnce(() => new Promise(resolve => { resolveSecondRange = resolve }))
        render(<StorefrontAnalyticsDashboard />)

        fireEvent.click(screen.getByRole('button', { name: '30D' }))

        await act(async () => resolveSecondRange(rangeB))
        await screen.findByText('65,432')

        await act(async () => resolveFirstRange(rangeA))
        expect(screen.queryByText('321')).not.toBeInTheDocument()
        expect(screen.getByText('65,432')).toBeInTheDocument()
    })

    it('keeps range provenance when the interface language changes', async () => {
        storefrontAnalyticsApi.getStorefrontAnalyticsDashboard
            .mockResolvedValueOnce(rangeC)
            .mockRejectedValueOnce(new Error('offline'))
            .mockResolvedValueOnce(rangeB)
        const view = render(<StorefrontAnalyticsDashboard />)

        await screen.findByText('987')
        platform.catalog = ar
        view.rerender(<StorefrontAnalyticsDashboard />)

        fireEvent.click(screen.getByRole('button', { name: ar.storefrontAnalytics.ranges['30d'] }))
        await screen.findByText(ar.storefrontAnalytics.unavailableTitle)
        expect(screen.queryByText('987')).not.toBeInTheDocument()
        expect(screen.queryByText(ar.storefrontAnalytics.staleResult)).not.toBeInTheDocument()

        fireEvent.click(screen.getAllByRole('button', { name: ar.common.retry })[0])
        await screen.findByText('65,432')
        expect(storefrontAnalyticsApi.getStorefrontAnalyticsDashboard).toHaveBeenLastCalledWith('30d')
    })

    it('reports a malformed new-range response as unusable instead of keeping the old range', async () => {
        storefrontAnalyticsApi.getStorefrontAnalyticsDashboard
            .mockResolvedValueOnce(rangeA)
            .mockResolvedValueOnce({ ...rangeB, revenue: null })
        render(<StorefrontAnalyticsDashboard />)

        await screen.findByText('321')
        fireEvent.click(screen.getByRole('button', { name: 'Today' }))

        await screen.findByText(en.storefrontAnalytics.malformed)
        expect(screen.queryByText('321')).not.toBeInTheDocument()
        expect(screen.queryByText(en.storefrontAnalytics.staleResult)).not.toBeInTheDocument()
    })
    it.each([401, 403])('removes previously loaded financial values after access denial (%s)', async status => {
        storefrontAnalyticsApi.getStorefrontAnalyticsDashboard.mockResolvedValueOnce(rangeA)
            .mockRejectedValueOnce({ response: { status } })
        render(<StorefrontAnalyticsDashboard />)
        await screen.findByText('321')
        fireEvent.click(screen.getByRole('button', { name: en.common.refresh }))
        await screen.findByText(en.storefrontAnalytics.accessDenied)
        expect(screen.queryByText('321')).not.toBeInTheDocument()
        expect(screen.queryByText('EGP 1111.5')).not.toBeInTheDocument()
    })
    it('invalidates standalone figures when actor/tenant authority changes', async () => {
        storefrontAnalyticsApi.getStorefrontAnalyticsDashboard.mockResolvedValueOnce(rangeA)
            .mockReturnValueOnce(new Promise(() => {}))
        render(<StorefrontAnalyticsDashboard />)
        await screen.findByText('321')
        act(() => useAuthStore.setState({ user: { id: 'actor-b', tenantId: 'tenant-b' } }))
        expect(screen.queryByText('321')).not.toBeInTheDocument()
        expect(screen.queryByText('EGP 1111.5')).not.toBeInTheDocument()
    })
})
