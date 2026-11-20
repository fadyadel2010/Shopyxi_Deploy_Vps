import { render, screen, cleanup, fireEvent } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { LanguageProvider } from '../../i18n/LanguageContext'
import DashboardTab from './DashboardTab'

const api = vi.hoisted(() => ({ getDashboard: vi.fn(), getAnalyticsSummary: vi.fn(), getAnalyticsTrend: vi.fn(), getPayouts: vi.fn(), getOrders: vi.fn() }))
vi.mock('../../../../services/affiliateApi', () => ({ default: api }))
vi.mock('recharts', () => ({ ResponsiveContainer: () => null, BarChart: () => null, Bar: () => null, XAxis: () => null, YAxis: () => null, CartesianGrid: () => null, Tooltip: () => null }))
const summary = { currency: 'EGP', totalEarned: 50, totalApproved: 20, totalPaid: 10, totalReversed: 0, netEarnings: 40 }
const money = (value, currency) => `${currency} ${value}`
const open = () => render(<LanguageProvider><DashboardTab formatCurrency={money} /></LanguageProvider>)
beforeEach(() => {
    vi.resetAllMocks()
    localStorage.setItem('affiliate_lang', 'en')
    api.getDashboard.mockResolvedValue({ earningsByCurrency: [summary], totalOrders: 1 })
    api.getAnalyticsSummary.mockResolvedValue({ totalClicks: 0, totalOrders: 0 })
    api.getAnalyticsTrend.mockResolvedValue([])
    api.getPayouts.mockResolvedValue({ currency: 'EGP', availableForWithdrawal: 25, totalPendingRequests: 0 })
    api.getOrders.mockResolvedValue({ orders: [] })
})
afterEach(cleanup)
describe('UIUX Affiliate response boundaries', () => {
    it.each([null, {}, { totalOrders: 0 }, { earningsByCurrency: [] }, { earningsByCurrency: [{ ...summary, totalPaid: '10' }], totalOrders: 1 }])('does not call malformed dashboard %j empty', async value => {
        api.getDashboard.mockResolvedValue(value)
        open()
        expect(await screen.findByRole('alert')).toHaveTextContent('Unable to load data')
        expect(screen.queryByText('No affiliate activity yet.')).not.toBeInTheDocument()
        expect(screen.queryByText('EGP 0')).not.toBeInTheDocument()
    })
    it('retains valid finances but marks all malformed optional reads unavailable, not empty', async () => {
        api.getAnalyticsSummary.mockResolvedValue({})
        api.getAnalyticsTrend.mockResolvedValue([{ date: 'invalid', clicks: 0, orders: 0 }])
        api.getPayouts.mockResolvedValue({ currency: 'EGP' })
        api.getOrders.mockResolvedValue({})
        open()
        expect(await screen.findByText('EGP 50')).toBeVisible()
        expect(screen.getAllByRole('alert')).toHaveLength(3)
        expect(screen.queryByText('No tracked clicks in this period.')).not.toBeInTheDocument()
    })
    it('preserves genuine empty dashboard and tracking results', async () => {
        api.getDashboard.mockResolvedValue({ earningsByCurrency: [], totalOrders: 0 })
        open()
        expect(await screen.findByText('No affiliate activity yet.')).toBeVisible()
        expect(screen.getByText('No tracked clicks in this period.')).toBeVisible()
        expect(screen.queryByRole('alert')).not.toBeInTheDocument()
    })
    it('recovers through the existing retry without showing raw errors', async () => {
        api.getDashboard.mockResolvedValueOnce(null)
        open()
        fireEvent.click(await screen.findByRole('button', { name: 'Retry' }))
        expect(await screen.findByText('EGP 50')).toBeVisible()
    })
})
