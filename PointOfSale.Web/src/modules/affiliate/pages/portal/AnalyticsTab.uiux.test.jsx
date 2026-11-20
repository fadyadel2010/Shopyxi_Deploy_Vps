import { act } from 'react'
import { cleanup, fireEvent, render, screen } from '@testing-library/react'
import { afterEach, beforeEach, expect, it, vi } from 'vitest'
import AnalyticsTab from './AnalyticsTab'
import { LanguageProvider } from '../../i18n/LanguageContext'
const api = vi.hoisted(() => ({ summary: vi.fn(), trend: vi.fn(), products: vi.fn(), stores: vi.fn() }))
vi.mock('../../../../services/affiliateApi', () => ({ default: {
    getAnalyticsSummary: api.summary, getAnalyticsTrend: api.trend, getAnalyticsTopProducts: api.products, getAnalyticsTopStores: api.stores,
} }))
vi.mock('framer-motion', () => ({ motion: {
    div: ({ children, variants, initial, animate, custom, ...props }) => <div {...props}>{children}</div>,
} }))
vi.mock('recharts', () => ({ ResponsiveContainer: () => null, AreaChart: () => null, BarChart: () => null,
    Area: () => null, Line: () => null, Bar: () => null, XAxis: () => null, YAxis: () => null, CartesianGrid: () => null, Tooltip: () => null }))
const summary = { totalClicks: 109, totalEarnings: 50, attributedOrders: 1, clickRecords: 1, convertedClicks: 1, conversionRate: 100 }
beforeEach(() => {
    localStorage.setItem('affiliate_lang', 'en')
    vi.resetAllMocks()
    api.summary.mockResolvedValue(summary)
    api.trend.mockResolvedValue([])
    api.products.mockResolvedValue([])
    api.stores.mockResolvedValue([])
})
afterEach(cleanup)
const open = () => render(<LanguageProvider><AnalyticsTab /></LanguageProvider>)
it.each([null, {}, { ...summary, totalEarnings: '50' }])('rejects malformed performance summary %j instead of fabricating money', async value => {
    api.summary.mockResolvedValue(value)
    open()
    await screen.findByRole('alert')
    expect(screen.queryByText('EGP')).not.toBeInTheDocument()
    expect(screen.queryByText('109')).not.toBeInTheDocument()
})
it('does not infer EGP and preserves valid conversion/counts', async () => {
    open()
    await screen.findByText('109')
    expect(screen.getByText('100.00%')).toBeVisible()
    expect(screen.getAllByText('Currency unavailable').length).toBeGreaterThan(0)
    expect(screen.queryByText(/EGP|E£/)).not.toBeInTheDocument()
})
it('hides old range synchronously and ignores a late previous response', async () => {
    let complete
    const { container } = open()
    await screen.findByText('109')
    api.summary.mockImplementationOnce(() => new Promise(resolve => { complete = resolve })).mockResolvedValueOnce({ ...summary, totalClicks: 222 })
    const ranges = container.querySelectorAll('[aria-pressed]')
    fireEvent.click(ranges[0])
    expect(screen.queryByText('109')).not.toBeInTheDocument()
    fireEvent.click(ranges[1])
    await screen.findByText('222')
    await act(async () => complete({ ...summary, totalClicks: 999 }))
    expect(screen.queryByText('999')).not.toBeInTheDocument()
})
it('rejects malformed optional datasets instead of claiming empty success', async () => {
    api.trend.mockResolvedValue(null)
    open()
    await screen.findByRole('alert')
    expect(screen.queryByText('109')).not.toBeInTheDocument()
})
