import { act } from 'react'
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import TreasuryDashboard from './TreasuryDashboard'
import { useAuthStore } from '../../store/authStore'

const api = vi.hoisted(() => ({
    getDashboard: vi.fn(), getBalanceSheet: vi.fn(), getProfitAndLoss: vi.fn(),
    getClearingAging: vi.fn(), getTrialBalance: vi.fn(),
    getDefaultDateRange: () => ({ from: '2026-11-01', to: '2026-11-20' }),
    getFiscalYearRange: () => ({ from: '2026-01-01', to: '2026-11-20' }),
}))
const catalog = vi.hoisted(() => ({ getTreasuryAccounts: vi.fn() }))
const toast = vi.hoisted(() => ({ showError: vi.fn() }))
const translate = vi.hoisted(() => (key, fallback) => fallback || key)
vi.mock('../../services/financialReportsApi', () => api)
vi.mock('../../services/treasuryApi', () => catalog)
vi.mock('../../services/api', () => ({ default: {}, isRequestCanceled: () => false }))
vi.mock('react-router-dom', () => ({ useNavigate: () => vi.fn() }))
vi.mock('react-i18next', () => ({ useTranslation: () => ({ t: translate }) }))
vi.mock('../../contexts/SettingsContext', () => ({ useSettings: () => ({ formatCurrency: value => `MONEY ${value}` }) }))
vi.mock('../../components/Toast', () => ({ useToast: () => toast }))
vi.mock('../../components/DonutChart', () => ({ default: () => null }))
vi.mock('../../components/ReportTruthBadge', () => ({ default: () => null }))
vi.mock('../../hooks/useRTL', () => ({ useRTL: () => ({ dir: 'ltr', locale: 'en' }) }))
vi.mock('../../utils/dateFormat', () => ({ formatDate: () => 'date', formatTime: () => 'snapshot-time' }))
const valid = { revenue: 100, cogs: 20, grossProfit: 80, grossProfitMargin: 80,
    expenses: 10, netProfit: 70, netProfitMargin: 70, totalAssets: 150,
    totalLiabilities: 50, totalEquity: 100, currentRatio: null, balanceSheetBalanced: true }
beforeEach(() => {
    vi.clearAllMocks()
    useAuthStore.setState({ user: { id: 'a', tenantId: 't' } })
    api.getDashboard.mockResolvedValue(valid)
    api.getBalanceSheet.mockResolvedValue({})
    api.getProfitAndLoss.mockResolvedValue({})
    api.getClearingAging.mockResolvedValue({ lines: [] })
    api.getTrialBalance.mockResolvedValue({ isBalanced: true, variance: 0 })
    catalog.getTreasuryAccounts.mockResolvedValue([])
    vi.spyOn(console, 'error').mockImplementation(() => {})
})
afterEach(() => { cleanup(); vi.restoreAllMocks() })
describe('UIUX Treasury unavailable and scope boundaries', () => {
    it.each([null, {}, { ...valid, revenue: '100' }])('rejects malformed summary %j without successful zero cards', async value => {
        api.getDashboard.mockResolvedValue(value)
        render(<TreasuryDashboard />)
        expect(await screen.findByText('Failed to load financial data')).toBeVisible()
        expect(screen.queryByText('MONEY 0')).not.toBeInTheDocument()
        expect(screen.queryByText('All clearing accounts settled!')).not.toBeInTheDocument()
    })
    it('retains independent summary but does not call failed clearing settled or failed trial zero variance', async () => {
        api.getClearingAging.mockRejectedValue(new Error('offline'))
        api.getTrialBalance.mockResolvedValue(null)
        render(<TreasuryDashboard />)
        expect((await screen.findAllByText('MONEY 100')).length).toBeGreaterThan(0)
        expect(screen.getAllByText('Failed to load financial data')).toHaveLength(2)
        expect(screen.queryByText('All clearing accounts settled!')).not.toBeInTheDocument()
        expect(screen.queryByText('VARIANCE')).not.toBeInTheDocument()
        expect(screen.queryByText('Fully reconciled')).not.toBeInTheDocument()
        expect(screen.queryByText(/snapshot-time/)).not.toBeInTheDocument()
    })
    it('clears a previously successful summary when a refresh fails and recovers by retry', async () => {
        render(<TreasuryDashboard />)
        await screen.findByText(/snapshot-time/)
        api.getDashboard.mockRejectedValueOnce({ response: { status: 403 } })
        fireEvent.click(screen.getByRole('button', { name: 'common.refresh' }))
        await screen.findByText('Failed to load financial data')
        expect(screen.queryByText('MONEY 100')).not.toBeInTheDocument()
        fireEvent.click(screen.getByRole('button', { name: 'Retry' }))
        await screen.findByText(/snapshot-time/)
    })
    it('does not retain old range/actor values and bypasses the range-only service cache', async () => {
        let resolveLate
        render(<TreasuryDashboard />)
        await screen.findByText(/snapshot-time/)
        api.getDashboard.mockImplementationOnce(() => new Promise(resolve => { resolveLate = resolve }))
        fireEvent.click(screen.getByRole('button', { name: 'YTD' }))
        expect(screen.queryByText('MONEY 100')).not.toBeInTheDocument()
        await waitFor(() => expect(api.getDashboard).toHaveBeenCalledTimes(2))
        act(() => useAuthStore.setState({ user: { id: 'b', tenantId: 't2' } }))
        expect(screen.queryByText('MONEY 100')).not.toBeInTheDocument()
        await screen.findByText(/snapshot-time/)
        await act(async () => resolveLate({ ...valid, revenue: 999 }))
        expect(screen.queryByText('MONEY 999')).not.toBeInTheDocument()
        expect(api.getDashboard.mock.calls.every(call => call[2] === true)).toBe(true)
    })
    it('keeps genuine zero values and nullable current ratio neutral', async () => {
        api.getDashboard.mockResolvedValue({ ...valid, revenue: 0 })
        render(<TreasuryDashboard />)
        await screen.findByText(/snapshot-time/)
        expect(screen.getByRole('progressbar', { name: 'Revenue' })).toHaveAttribute('aria-valuenow', '0')
        expect(screen.getByText('Unavailable')).toBeVisible()
        expect(screen.queryByText('Risk')).not.toBeInTheDocument()
    })
})
