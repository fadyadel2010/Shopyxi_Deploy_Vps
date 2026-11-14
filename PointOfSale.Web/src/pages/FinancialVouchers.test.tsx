import { beforeEach, describe, expect, it, vi } from 'vitest'
import { fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import FinancialVouchers from './FinancialVouchers'

const state = vi.hoisted(() => ({
    user: { effectivePermissions: ['finance.view', 'finance.operate', 'customers.balance.manage', 'suppliers.balance.manage', 'reports.financial.view'] },
    get: vi.fn(), post: vi.fn(), invalidate: vi.fn(), print: vi.fn(), printOptions: null as null | { contentRef: { current: HTMLElement | null }; onPrintError: () => void }, t: (key: string) => key,
}))
vi.mock('react-i18next', () => ({ useTranslation: () => ({ t: state.t }) }))
vi.mock('../services/api', () => ({ default: { get: state.get, post: state.post }, isRequestCanceled: () => false }))
vi.mock('../store/authStore', () => ({ useAuthStore: (selector: (x: unknown) => unknown) => selector({ user: state.user }) }))
vi.mock('../hooks/useRTL', () => ({ useRTL: () => ({ dir: 'rtl' }) }))
vi.mock('../services/posRuntimeCache', () => ({ invalidatePosRuntimeCache: state.invalidate }))
vi.mock('react-to-print', () => ({ useReactToPrint: (options: NonNullable<typeof state.printOptions>) => { state.printOptions = options; return state.print } }))
const voucher = { id: 'voucher-1', number: 'QA-ERP-R10-20261113-RV', kind: 0, partyName: 'QA customer', amount: 10, currency: 'EGP', paymentDate: '2026-11-13',
    sourcePaymentId: 'payment-1', journalId: 'journal-1', status: 0, version: 1, reason: 'QA source receipt' }
beforeEach(() => {
    vi.clearAllMocks()
    state.user = { effectivePermissions: ['finance.view', 'finance.operate', 'customers.balance.manage', 'suppliers.balance.manage', 'reports.financial.view'] }
    state.get.mockImplementation((url: string) => Promise.resolve({ data: url.endsWith('/sources')
        ? { items: [{ id: 'payment-1', referenceNumber: 'QA-PAYMENT', partyName: 'QA customer', amount: 10, posted: true }, { id: 'pending', partyName: 'QA pending', amount: 1, posted: false }], total: 2, pageSize: 50 }
        : url.endsWith('/voucher-1') ? { voucher, history: [] } : { items: [voucher], total: 1, pageSize: 50 } }))
    state.post.mockResolvedValue({ data: voucher })
})
describe('Financial vouchers', () => {
    it('prints only the selected RTL document, excluding the Admin layout and voucher list', async () => {
        render(<FinancialVouchers />)
        fireEvent.click(await screen.findByRole('button', { name: voucher.number }))
        const details = await screen.findByRole('region', { name: 'financialVouchers.details' })
        expect(state.printOptions?.contentRef.current).toBe(details)
        expect(details).toHaveAttribute('dir', 'rtl')
        expect(details).not.toContainElement(screen.getByRole('button', { name: 'financialVouchers.issueReceipt' }))
        expect(details).not.toContainElement(screen.getByLabelText('financialVouchers.search'))
        fireEvent.click(screen.getByRole('button', { name: 'financialVouchers.print' }))
        expect(state.print).toHaveBeenCalledTimes(1)
        expect(state.post).not.toHaveBeenCalled()
    })

    it('recovers a committed issuance by reading the operation ID without sending another write', async () => {
        const original = state.get.getMockImplementation()!
        state.post.mockRejectedValue(new Error('response lost'))
        state.get.mockImplementation((url: string, ...args: unknown[]) => /^\/financial-vouchers\/[0-9a-f-]{36}$/.test(url)
            ? Promise.resolve({ data: { voucher: { ...voucher, id: url.split('/').pop() }, history: [] } }) : original(url, ...args))
        render(<FinancialVouchers />)
        fireEvent.click(screen.getByRole('button', { name: 'financialVouchers.issueReceipt' }))
        const dialog = screen.getByRole('dialog')
        await within(dialog).findByRole('option', { name: 'QA-PAYMENT — QA customer — 10' })
        fireEvent.change(within(dialog).getByLabelText('financialVouchers.source'), { target: { value: 'payment-1' } })
        fireEvent.change(within(dialog).getByLabelText('financialVouchers.reason'), { target: { value: 'QA response loss' } })
        fireEvent.click(within(dialog).getByRole('button', { name: 'financialVouchers.confirm' }))
        await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())
        await screen.findByRole('region', { name: 'financialVouchers.details' })
        expect(state.post).toHaveBeenCalledTimes(1)
        expect(state.get).toHaveBeenCalledWith(`/financial-vouchers/${state.post.mock.calls[0][1].operationId}`)
        expect(state.invalidate).toHaveBeenCalledWith({ reason: 'voucher-workflow-recovered' })
        expect(screen.queryByRole('alert')).not.toBeInTheDocument()
    })

    it('clears the selected payment when its search changes rather than submitting an invisible source', async () => {
        render(<FinancialVouchers />)
        fireEvent.click(screen.getByRole('button', { name: 'financialVouchers.issueReceipt' }))
        const dialog = screen.getByRole('dialog')
        await within(dialog).findByRole('option', { name: 'QA-PAYMENT — QA customer — 10' })
        fireEvent.change(within(dialog).getByLabelText('financialVouchers.source'), { target: { value: 'payment-1' } })
        fireEvent.change(within(dialog).getByLabelText('financialVouchers.sourceSearch'), { target: { value: 'another party' } })
        await waitFor(() => expect(within(dialog).getByLabelText('financialVouchers.source')).toHaveValue(''))
        expect(state.post).not.toHaveBeenCalled()
    })
    it('issues only a document linked to an existing posted payment without accepting arbitrary amounts or accounts', async () => {
        render(<FinancialVouchers />)
        fireEvent.click(screen.getByRole('button', { name: 'financialVouchers.issueReceipt' }))
        const dialog = screen.getByRole('dialog')
        await within(dialog).findByRole('option', { name: 'QA-PAYMENT — QA customer — 10' })
        expect(within(dialog).getByRole('option', { name: /QA pending/ })).toBeDisabled()
        fireEvent.change(within(dialog).getByLabelText('financialVouchers.source'), { target: { value: 'payment-1' } })
        fireEvent.change(within(dialog).getByLabelText('financialVouchers.reason'), { target: { value: 'QA issue document' } })
        expect(within(dialog).queryByRole('spinbutton')).not.toBeInTheDocument()
        fireEvent.click(within(dialog).getByRole('button', { name: 'financialVouchers.confirm' }))
        await waitFor(() => expect(state.post).toHaveBeenCalledWith('/financial-vouchers', { operationId: expect.any(String), kind: 0, sourcePaymentId: 'payment-1', reason: 'QA issue document' }))
    })
    it('confirms a versioned full compensation explicitly and never retries a lost response', async () => {
        state.post.mockRejectedValue({ response: { data: { message: 'VOUCHER_CONFLICT: reload' } } })
        render(<FinancialVouchers />)
        fireEvent.click(await screen.findByRole('button', { name: voucher.number }))
        fireEvent.click(await screen.findByRole('button', { name: 'financialVouchers.reverse' }))
        fireEvent.change(screen.getByLabelText('financialVouchers.reason'), { target: { value: 'QA compensation' } })
        fireEvent.click(screen.getByRole('button', { name: 'financialVouchers.confirm' }))
        await waitFor(() => expect(state.post).toHaveBeenCalledWith('/financial-vouchers/voucher-1/reverse', { version: 1, reason: 'QA compensation' }))
        expect(await screen.findByRole('alert')).toHaveTextContent('VOUCHER_CONFLICT')
        expect(state.post).toHaveBeenCalledTimes(1)
    })
    it('hides issuance and compensation without finance and party-balance authority', async () => {
        state.user = { effectivePermissions: ['finance.view'] }
        render(<FinancialVouchers />)
        fireEvent.click(await screen.findByRole('button', { name: voucher.number }))
        await screen.findByRole('region', { name: 'financialVouchers.details' })
        expect(screen.queryByRole('button', { name: 'financialVouchers.issueReceipt' })).not.toBeInTheDocument()
        expect(screen.queryByRole('button', { name: 'financialVouchers.reverse' })).not.toBeInTheDocument()
        expect(state.get.mock.calls.some(([url]) => String(url).endsWith('/sources'))).toBe(false)
    })
    it('renders reversed vouchers with their frozen amount/currency and no further compensation action', async () => {
        const original = state.get.getMockImplementation()!
        state.get.mockImplementation((url: string, ...args: unknown[]) => url.endsWith('/voucher-1') ? Promise.resolve({ data: { voucher: { ...voucher, status: 1, currency: 'USD', reversalReason: 'QA compensated' }, history: [] } }) : original(url, ...args))
        render(<FinancialVouchers />)
        fireEvent.click(await screen.findByRole('button', { name: voucher.number }))
        const details = await screen.findByRole('region', { name: 'financialVouchers.details' })
        expect(details).toHaveTextContent('10 USD')
        expect(screen.queryByRole('button', { name: 'financialVouchers.reverse' })).not.toBeInTheDocument()
    })
})
