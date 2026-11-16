import { act, render, screen, waitFor } from '@testing-library/react'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import AnalyticsTab from './AnalyticsTab'
import { LanguageProvider } from '../../i18n/LanguageContext'

const api = vi.hoisted(() => ({
    summary: vi.fn(), trend: vi.fn(), products: vi.fn(), stores: vi.fn(),
}))
vi.mock('../../../../services/affiliateApi', () => ({ default: {
    getAnalyticsSummary: api.summary, getAnalyticsTrend: api.trend,
    getAnalyticsTopProducts: api.products, getAnalyticsTopStores: api.stores,
}}))
vi.mock('framer-motion', () => ({ motion: { div: ({children, ...p}: any) => <div {...p}>{children}</div> } }))
vi.mock('recharts', () => ({
    ResponsiveContainer: ({children}: any) => <div>{children}</div>, AreaChart: ({children}: any) => <div>{children}</div>,
    BarChart: ({children}: any) => <div>{children}</div>, Area: () => null, Line: () => null, Bar: () => null,
    XAxis: () => null, YAxis: () => null, CartesianGrid: () => null, Tooltip: () => null,
}))

const summary = { totalClicks:109, totalOrders:1, totalEarnings:110, conversionRate:100,
    clickRecords:1, attributedOrders:1, convertedClicks:1, conversionRateDenominator:'clickRecords' }

describe('Affiliate conversion analytics contract', () => {
    beforeEach(() => {
        localStorage.setItem('affiliate_lang','en')
        Object.values(api).forEach(mock => mock.mockReset())
        api.summary.mockResolvedValue(summary); api.trend.mockResolvedValue([])
        api.products.mockResolvedValue([]); api.stores.mockResolvedValue([])
    })

    it('separates tracked click events, committed attributed orders and earnings', async () => {
        render(<LanguageProvider><AnalyticsTab /></LanguageProvider>)
        expect(await screen.findByText('Tracked Click Events')).toBeInTheDocument()
        expect(screen.getAllByText('Attributed Orders').length).toBeGreaterThan(0)
        expect(screen.getByTestId('conversion-definition')).toHaveTextContent('Click records: 1')
        expect(screen.getByTestId('conversion-definition')).toHaveTextContent('Converted clicks: 1')
        expect(screen.getByTestId('conversion-definition')).toHaveTextContent('not payments, deliveries or commissions')
        expect(screen.getByText('100.00%')).toBeInTheDocument()
    })

    it('shows unavailable rather than fabricating a zero rate with no denominator', async () => {
        api.summary.mockResolvedValue({...summary, conversionRate:null, clickRecords:0, attributedOrders:0, convertedClicks:0})
        render(<LanguageProvider><AnalyticsTab /></LanguageProvider>)
        await screen.findByText('Tracked Click Events')
        expect(screen.getByText('Unavailable')).toBeInTheDocument()
    })

    it('refreshes delayed projection visibility on window focus without local increments', async () => {
        api.summary.mockResolvedValueOnce({...summary, attributedOrders:0, convertedClicks:0, conversionRate:0})
            .mockResolvedValue(summary)
        render(<LanguageProvider><AnalyticsTab /></LanguageProvider>)
        await waitFor(() => expect(api.summary).toHaveBeenCalledTimes(1))
        await act(async () => window.dispatchEvent(new Event('focus')))
        await waitFor(() => expect(api.summary).toHaveBeenCalledTimes(2))
        expect(await screen.findByText('100.00%')).toBeInTheDocument()
    })
})
