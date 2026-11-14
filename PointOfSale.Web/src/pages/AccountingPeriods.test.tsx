import { beforeEach, describe, expect, it, vi } from 'vitest'
import { fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import { MemoryRouter } from 'react-router-dom'
import AccountingPeriods from './AccountingPeriods'

const state = vi.hoisted(() => ({
    user: { role: 'Owner', effectivePermissions: ['finance.view', 'finance.admin', 'reports.financial.view'] },
    get: vi.fn(), post: vi.fn(),
    t: (key: string) => key,
}))
vi.mock('react-i18next', () => ({ useTranslation: () => ({ t: state.t }) }))
vi.mock('../services/api', () => ({ default: { get: state.get, post: state.post }, isRequestCanceled: () => false }))
vi.mock('../store/authStore', () => ({ useAuthStore: (selector: (x: unknown) => unknown) => selector({ user: state.user }) }))
vi.mock('../hooks/useRTL', () => ({ useRTL: () => ({ dir: 'ltr' }) }))

const period = { id: 'period-1', code: 'QA-ERP-R10-20261113-PERIOD', name: 'November QA', startDate: '2026-11-01', endDate: '2026-11-30', status: 1, version: 4, reason: 'QA reason' }
const renderPage = () => render(<MemoryRouter><AccountingPeriods /></MemoryRouter>)

beforeEach(() => {
    vi.clearAllMocks()
    state.user = { role: 'Owner', effectivePermissions: ['finance.view', 'finance.admin', 'reports.financial.view'] }
    state.get.mockImplementation((url: string) => Promise.resolve({ data:
        url.endsWith('/journals') ? { items: [], total: 0, page: 1 } :
            url.endsWith('/period-1') ? { period, totals: { journalCount: 0, totalDebits: 0, totalCredits: 0 }, history: [] } :
                { items: [period], total: 1, pageSize: 50 },
    }))
    state.post.mockResolvedValue({ data: period })
})

describe('Accounting periods', () => {
    it('loads the real period contract, details, date-filtered report and explicit close confirmation', async () => {
        renderPage()
        fireEvent.click(await screen.findByRole('button', { name: period.code }))
        fireEvent.click(await screen.findByRole('button', { name: 'accountingPeriods.close' }))
        const dialog = screen.getByRole('dialog')
        fireEvent.change(within(dialog).getByRole('textbox', { name: 'accountingPeriods.reason' }), { target: { value: 'QA balanced closing' } })
        expect(state.post).not.toHaveBeenCalled()
        fireEvent.click(within(dialog).getByRole('button', { name: 'accountingPeriods.confirm' }))
        await waitFor(() => expect(state.post).toHaveBeenCalledWith('/accounting-periods/period-1/close', { version: 4, reason: 'QA balanced closing' }))
        expect(await screen.findByRole('link', { name: 'accountingPeriods.report' })).toHaveAttribute('href', '/treasury/general-ledger?from=2026-11-01&to=2026-11-30')
    })

    it('creates a period with explicit dates, code, open intent and audit reason', async () => {
        renderPage()
        fireEvent.click(screen.getByRole('button', { name: 'accountingPeriods.create' }))
        const dialog = screen.getByRole('dialog')
        for (const [field, value] of Object.entries({ code: 'QA-NOV', name: 'QA November', startDate: '2026-11-01', endDate: '2026-11-30', reason: 'Synthetic period' }))
            fireEvent.change(within(dialog).getByLabelText(`accountingPeriods.${field}`), { target: { value } })
        fireEvent.click(within(dialog).getByRole('button', { name: 'accountingPeriods.confirm' }))
        await waitFor(() => expect(state.post).toHaveBeenCalledWith('/accounting-periods', { code: 'QA-NOV', name: 'QA November', startDate: '2026-11-01', endDate: '2026-11-30', reason: 'Synthetic period', open: true }))
    })

    it('does not automatically post, close, retry or reopen after a failed response', async () => {
        state.post.mockRejectedValue({ response: { data: { message: 'PERIOD_CONFLICT: reload' } } })
        renderPage()
        fireEvent.click(await screen.findByRole('button', { name: period.code }))
        fireEvent.click(await screen.findByRole('button', { name: 'accountingPeriods.close' }))
        fireEvent.change(screen.getByRole('textbox', { name: 'accountingPeriods.reason' }), { target: { value: 'QA close' } })
        fireEvent.click(screen.getByRole('button', { name: 'accountingPeriods.confirm' }))
        expect(await screen.findByRole('alert')).toHaveTextContent('PERIOD_CONFLICT: reload')
        await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())
        expect(state.post).toHaveBeenCalledTimes(1)
    })

    it('hides all management actions without finance.admin even when finance.view is present', async () => {
        state.user = { role: 'Manager', effectivePermissions: ['finance.view'] }
        renderPage()
        fireEvent.click(await screen.findByRole('button', { name: period.code }))
        await screen.findByRole('region', { name: 'accountingPeriods.details' })
        expect(screen.queryByRole('button', { name: 'accountingPeriods.create' })).not.toBeInTheDocument()
        expect(screen.queryByRole('button', { name: 'accountingPeriods.close' })).not.toBeInTheDocument()
        expect(screen.queryByRole('link', { name: 'accountingPeriods.report' })).not.toBeInTheDocument()
        expect(state.get.mock.calls.some(([url]) => String(url).endsWith('/journals'))).toBe(false)
        expect(state.post).not.toHaveBeenCalled()
    })

    it('shows a closed-period warning and only an explicit authorized reopening action', async () => {
        state.get.mockImplementation((url: string) => Promise.resolve({ data: url.endsWith('/journals') ? { items: [], total: 0 } : url.endsWith('/period-1') ? { period: { ...period, status: 2 }, totals: { journalCount: 0 }, history: [] } : { items: [{ ...period, status: 2 }], total: 1, pageSize: 50 } }))
        renderPage()
        fireEvent.click(await screen.findByRole('button', { name: period.code }))
        expect(await screen.findByText('accountingPeriods.closedWarning')).toBeInTheDocument()
        expect(screen.getByRole('button', { name: 'accountingPeriods.reopen' })).toBeInTheDocument()
        expect(screen.queryByRole('button', { name: 'accountingPeriods.close' })).not.toBeInTheDocument()
        expect(state.post).not.toHaveBeenCalled()
    })
})
