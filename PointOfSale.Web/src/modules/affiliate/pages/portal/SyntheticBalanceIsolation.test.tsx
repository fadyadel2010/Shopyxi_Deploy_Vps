import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { LanguageProvider } from '../../i18n/LanguageContext'
import PayoutsTab from './PayoutsTab'
import DashboardTab from './DashboardTab'
import EarningsTab from './EarningsTab'
import OrdersTab from './OrdersTab'

const mocks = vi.hoisted(() => ({ getPayouts: vi.fn(), getDashboard: vi.fn(), getEarningsSummary: vi.fn(), getOrders: vi.fn(), requestPayout: vi.fn() }))
vi.mock('../../../../services/affiliateApi', () => ({ default: { ...mocks, getCommissionActivity: vi.fn().mockResolvedValue({ entries: [], totalPages: 0 }), getAnalyticsSummary: vi.fn().mockResolvedValue(null), getAnalyticsTrend: vi.fn().mockResolvedValue([]) } }))
vi.mock('recharts', () => ({ ResponsiveContainer: () => null, BarChart: () => null, LineChart: () => null,
    Line: () => null, Bar: () => null, XAxis: () => null, YAxis: () => null, CartesianGrid: () => null, Tooltip: () => null }))

const formatCurrency = (amount: number, currency: string) => `${currency} ${amount.toFixed(2)}`
const summary = { currency: 'EGP', totalEarned: 80, totalApproved: 0, historicalApproved: 80, totalReversed: 80,
    totalPaid: 0, netEarnings: 0, syntheticApprovedExcluded: 5 }

describe('Synthetic audit history is not payable balance', () => {
    beforeEach(() => {
        vi.clearAllMocks()
        mocks.getPayouts.mockResolvedValue({ ...summary, availableForWithdrawal: 0, totalPendingRequests: 0,
            minimumThreshold: 100, canWithdraw: false, history: [] })
        mocks.getDashboard.mockResolvedValue({ earningsByCurrency: [summary], totalOrders: 2 })
        mocks.getEarningsSummary.mockResolvedValue([summary])
        mocks.getOrders.mockResolvedValue({ totalCount: 1, orders: [{ orderId: 'qa', orderNumber: 'QA ORDER', isSyntheticQa: true,
            totalCommission: 5, currency: 'EGP', status: 'Approved', createdAtUtc: '2026-10-22T00:00:00Z', items: [] }] })
    })

    it.each(['en', 'ar'])('withdrawals identify QA exclusion in %s and never enable a request', async language => {
        localStorage.setItem('affiliate_lang', language)
        render(<LanguageProvider><PayoutsTab formatCurrency={formatCurrency} /></LanguageProvider>)
        expect(await screen.findByRole('note')).toHaveTextContent('SYNTHETIC QA — NO REAL DELIVERY OR MONEY')
        expect(screen.getByRole('note')).toHaveTextContent('EGP 5.00')
        expect(screen.getByRole('note')).toHaveTextContent(language === 'ar' ? 'مستبعد من الدفع والسحب' : 'excluded from payment and withdrawals')
        expect(screen.getByRole('button', { name: language === 'ar' ? /طلب سحب/ : /Request Withdrawal/ })).toBeDisabled()
        expect(screen.getAllByText('EGP 0.00').length).toBeGreaterThanOrEqual(2)
        expect(mocks.requestPayout).not.toHaveBeenCalled()
    })

    it.each([DashboardTab, EarningsTab])('summary preserves real/reversed history and separately labels QA', async Component => {
        localStorage.setItem('affiliate_lang', 'en')
        render(<LanguageProvider><Component formatCurrency={formatCurrency} /></LanguageProvider>)
        expect(await screen.findByRole('note')).toHaveTextContent('EGP 5.00')
        expect(screen.getAllByText('EGP 80.00').length).toBeGreaterThan(0)
        expect(screen.getAllByText('EGP 0.00').length).toBeGreaterThan(0)
    })

    it('keeps synthetic order history visible with explicit provenance', async () => {
        localStorage.setItem('affiliate_lang', 'en')
        render(<LanguageProvider><OrdersTab formatCurrency={formatCurrency} /></LanguageProvider>)
        expect(await screen.findByText('QA ORDER')).toBeVisible()
        expect(screen.getByText(/SYNTHETIC QA — NO REAL DELIVERY OR MONEY/)).toHaveTextContent('excluded from payment and withdrawals')
    })

    it('never presents a failed authority read as a verified zero or permits withdrawal', async () => {
        localStorage.setItem('affiliate_lang', 'en')
        mocks.getPayouts.mockRejectedValue({ response: { status: 409, data: { message: 'LINEAGE_UNCLASSIFIED' } } })
        render(<LanguageProvider><PayoutsTab formatCurrency={formatCurrency} /></LanguageProvider>)
        expect(await screen.findByRole('alert')).toHaveTextContent('real eligible balance could not be verified')
        expect(screen.queryByRole('button', { name: /Request Withdrawal/ })).not.toBeInTheDocument()
        expect(screen.queryByText('EGP 0.00')).not.toBeInTheDocument()
        expect(mocks.requestPayout).not.toHaveBeenCalled()
    })

    it.each(['en', 'ar'])('validates isolated withdrawal form and sends only the amount in %s', async language => {
        localStorage.setItem('affiliate_lang', language)
        mocks.getPayouts.mockResolvedValue({ ...summary, totalApproved: 500, availableForWithdrawal: 500,
            totalPendingRequests: 0, minimumThreshold: 100, canWithdraw: true, history: [] })
        mocks.requestPayout.mockResolvedValue({ id: 'disposable-only', status: 'Pending' })
        render(<LanguageProvider><PayoutsTab formatCurrency={formatCurrency} /></LanguageProvider>)
        fireEvent.click(await screen.findByRole('button', { name: language === 'ar' ? /طلب سحب/ : /Request Withdrawal/ }))
        const input = await screen.findByRole('spinbutton')
        const confirm = screen.getByRole('button', { name: language === 'ar' ? /تأكيد/ : /Confirm/ })
        for (const amount of ['', '-1', '0', '99.99', '100.001', '501', '1e2']) {
            fireEvent.change(input, { target: { value: amount } })
            expect(confirm).toBeDisabled()
        }
        expect(mocks.requestPayout).not.toHaveBeenCalled()
        fireEvent.change(input, { target: { value: '150.25' } })
        expect(confirm).toBeEnabled()
        fireEvent.click(confirm)
        await waitFor(() => expect(mocks.requestPayout).toHaveBeenCalledExactlyOnceWith(150.25))
    })
})
