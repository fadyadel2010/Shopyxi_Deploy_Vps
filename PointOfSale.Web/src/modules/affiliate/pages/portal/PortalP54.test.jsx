import { act, fireEvent, render, screen, waitFor, cleanup } from '@testing-library/react'
import { StrictMode } from 'react'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { LanguageProvider } from '../../i18n/LanguageContext'
import DashboardTab from './DashboardTab'
import EarningsTab from './EarningsTab'
import OrdersTab from './OrdersTab'
import LandingPagesTab from './LandingPagesTab'
import { SettlementProgress } from './PortalUi'

const api = vi.hoisted(() => ({ getDashboard: vi.fn(), getAnalyticsSummary: vi.fn(), getAnalyticsTrend: vi.fn(), getPayouts: vi.fn(), getOrders: vi.fn(), getCommissionActivity: vi.fn(), getEarningsSummary: vi.fn(), get: vi.fn(), put: vi.fn() }))
vi.mock('../../../../services/affiliateApi', () => ({ default: api }))
vi.mock('../../../../services/affiliateAuthApi', () => ({ affiliateAxios: { get: api.get, put: api.put } }))
vi.mock('recharts', () => ({ ResponsiveContainer: ({ children }) => children, BarChart: ({ data }) => <div data-testid="chart">{JSON.stringify(data)}</div>, Bar: () => null, XAxis: () => null, YAxis: () => null, CartesianGrid: () => null, Tooltip: () => null }))
const summary = { currency: 'EGP', totalEarned: 680, totalApproved: 600, totalPaid: 0, totalReversed: 80, netEarnings: 600 }
const formatCurrency = (amount, currency) => amount == null ? '—' : `${currency} ${amount.toFixed(2)}`
const open = Component => render(<LanguageProvider><Component formatCurrency={formatCurrency} /></LanguageProvider>)
const page = { productId: 'product', productName: 'Actual product', storeName: 'AhramStore', price: 25, currency: 'EGP', estimatedCommission: 2.5, commissionRatePercentage: 10, templateType: 'Classic:v1', absoluteLandingUrl: 'http://ahramstore.localhost:5174/p/REF123/product' }
const orders = [
    { orderId: 'real', orderNumber: 'ORDER-REAL', status: 'AttributedPendingPayment', currency: 'EGP', totalCommission: 0, orderStatus: 'PendingPayment', createdAtUtc: '2026-10-21', items: [{ orderItemId: 'line', productName: 'Actual phone', netCommissionAmount: 0, paidAmount: 0, settlementStatus: 'Pending' }] },
    { orderId: 'qa', orderNumber: 'ORDER-QA', status: 'Approved', isSyntheticQa: true, currency: 'EGP', totalCommission: 5, createdAtUtc: '2026-10-22', items: [] },
]
beforeEach(() => {
    vi.clearAllMocks(); localStorage.setItem('affiliate_lang', 'en')
    api.getDashboard.mockResolvedValue({ earningsByCurrency: [summary], totalOrders: 3 })
    api.getAnalyticsSummary.mockResolvedValue({ totalClicks: 10, totalOrders: 2, conversionRate: 20 })
    api.getAnalyticsTrend.mockResolvedValue([{ date: '2026-10-21', clicks: 10, orders: 2 }])
    api.getPayouts.mockResolvedValue({ currency: 'EGP', availableForWithdrawal: 490, totalPendingRequests: 110 })
    api.getOrders.mockResolvedValue({ orders, totalCount: 2, totalPages: 1 })
    api.getEarningsSummary.mockResolvedValue([summary])
    api.getCommissionActivity.mockResolvedValue({ entries: [], totalPages: 0 })
    api.get.mockResolvedValue({ data: { landingPages: [{ ...page }], totalPages: 1 } })
})

describe('P5.4 performance and financial presentation', () => {
    it('uses the trend endpoint, never nonexistent summary.dailyData or analytics earnings as financial totals', async () => {
        open(DashboardTab)
        expect((await screen.findAllByTestId('chart'))[0]).toHaveTextContent('2026-10-21')
        expect(api.getAnalyticsTrend).toHaveBeenCalledTimes(1)
        expect(screen.getByText('EGP 490.00')).toBeVisible()
        expect(screen.getByText('EGP 110.00')).toBeVisible()
        expect(screen.getByRole('progressbar')).toHaveAttribute('aria-valuenow', '0')
    })
    it('explains empty tracking and missing conversion instead of rendering blank charts', async () => {
        api.getAnalyticsTrend.mockResolvedValue([]); api.getAnalyticsSummary.mockResolvedValue({ totalClicks: 0, totalOrders: 0 })
        open(DashboardTab)
        expect(await screen.findByText('No tracked clicks in this period.')).toBeVisible()
        expect(screen.getByText('No tracked orders in this period.')).toBeVisible()
        expect(screen.queryByTestId('chart')).not.toBeInTheDocument()
    })
    it('retains authoritative totals when optional endpoints fail; does not call failure zero', async () => {
        api.getAnalyticsTrend.mockRejectedValue(new Error('offline')); api.getPayouts.mockRejectedValue(new Error('offline'))
        open(DashboardTab)
        expect((await screen.findAllByText('EGP 600.00')).length).toBeGreaterThan(0)
        expect(screen.getByText(/Withdrawal balance unavailable/)).toBeVisible()
        expect(screen.getAllByText(/Analytics could not be loaded/).length).toBeGreaterThan(0)
    })
    it('never fills paid progress with approved money in Earnings', async () => {
        open(EarningsTab)
        expect(await screen.findByRole('progressbar')).toHaveAttribute('aria-valuenow', '0')
        expect(screen.getByTestId('paid-progress-fill')).toHaveStyle({ width: '0%' })
    })
    it('keeps pending order activity when no commission has been earned', async () => {
        api.getEarningsSummary.mockResolvedValue([])
        open(EarningsTab)
        expect(await screen.findByText('ORDER-REAL')).toBeVisible()
        expect(screen.getByText('Attributed · no commission yet')).toBeVisible()
        expect(screen.queryByRole('progressbar')).not.toBeInTheDocument()
    })
    it.each([[0, 0, 0], [100, 25, 25], [100, 200, 100], [100, -1, 0]])('bounds visual ratio for earned=%s paid=%s', (earned, paid, expected) => {
        render(<LanguageProvider><SettlementProgress summary={{ currency: 'EGP', totalEarned: earned, totalPaid: paid }} /></LanguageProvider>)
        expect(screen.getByRole('progressbar')).toHaveAttribute('aria-valuenow', String(expected))
    })
    it('searches page products, expands keyboard-accessible details, and separates QA', async () => {
        open(OrdersTab)
        fireEvent.change(await screen.findByRole('searchbox'), { target: { value: 'Actual phone' } })
        expect(screen.queryByRole('button', { name: 'ORDER-QA' })).not.toBeInTheDocument()
        fireEvent.click(screen.getByRole('button', { name: 'ORDER-REAL' }))
        expect(screen.getByRole('button', { name: 'ORDER-REAL' })).toHaveAttribute('aria-expanded', 'true')
        expect(screen.getByText(/This is not withdrawable money/)).toBeVisible()
        expect(screen.getByText(/quantity, payment status and referral context/)).toBeVisible()
        fireEvent.change(screen.getByRole('searchbox'), { target: { value: '' } })
        fireEvent.change(screen.getByLabelText('History type'), { target: { value: 'qa' } })
        expect(screen.queryByRole('button', { name: 'ORDER-REAL' })).not.toBeInTheDocument()
        expect(screen.getByText(/SYNTHETIC QA/)).toBeVisible()
    })
})

describe('P5.4 durable design selection', () => {
    it('ignores a late initial read rather than replacing an in-progress selection', async () => {
        let resolveOld
        api.get.mockReturnValueOnce(new Promise(resolve => { resolveOld = resolve })).mockResolvedValue({ data: { landingPages: [{ ...page }], totalPages: 1 } })
        render(<StrictMode><LanguageProvider><LandingPagesTab formatCurrency={formatCurrency} /></LanguageProvider></StrictMode>)
        fireEvent.click(await screen.findByRole('button', { name: 'Storytelling' }))
        await act(async () => resolveOld({ data: { landingPages: [{ ...page }], totalPages: 1 } }))
        expect(screen.getByRole('button', { name: 'Storytelling' })).toHaveAttribute('aria-pressed', 'true')
    })
    it.each(['Classic:v1', 'Storytelling:v1', 'MinimalFastCheckout:v1'])('saves %s then restores from backend after remount, with the same attributed public URL', async template => {
        let saved = { ...page }
        api.get.mockImplementation(async () => ({ data: { landingPages: [saved], totalPages: 1 } }))
        api.put.mockImplementation(async (_url, body) => { saved = { ...saved, templateType: body.templateType, hasSavedDesign: true }; return { data: saved } })
        open(LandingPagesTab)
        const label = template.startsWith('Minimal') ? 'Minimal' : template.split(':')[0]
        fireEvent.click(await screen.findByRole('button', { name: label }))
        fireEvent.click(screen.getByRole('button', { name: 'Save design' }))
        await waitFor(() => expect(api.put).toHaveBeenCalledWith('/affiliate/landing-pages/product/design', { templateType: template }))
        await waitFor(() => expect(screen.getByRole('button', { name: 'Open published page' })).toBeEnabled())
        cleanup(); open(LandingPagesTab)
        expect(await screen.findByRole('button', { name: label })).toHaveAttribute('aria-pressed', 'true')
        await waitFor(() => expect(screen.getByText(page.absoluteLandingUrl)).toBeVisible())
        expect(screen.queryByText(/\?template=/)).not.toBeInTheDocument()
    })
    it('failed saving never presents an unsaved preview as a published design', async () => {
        api.put.mockRejectedValue(new Error('offline')); open(LandingPagesTab)
        fireEvent.click(await screen.findByRole('button', { name: 'Storytelling' }))
        expect(screen.getByRole('button', { name: 'Copy URL' })).toBeDisabled()
        fireEvent.click(screen.getByRole('button', { name: 'Save design' }))
        expect(await screen.findByRole('alert')).toHaveTextContent('Your published page has not changed')
        expect(screen.getByRole('button', { name: 'Open published page' })).toBeDisabled()
    })
})
