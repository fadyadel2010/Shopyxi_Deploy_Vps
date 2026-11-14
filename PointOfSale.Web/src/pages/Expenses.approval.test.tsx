import { beforeEach, describe, expect, it, vi } from 'vitest'
import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import Expenses from './Expenses'
import en from '../i18n/locales/en.json'
import ar from '../i18n/locales/ar.json'

const s = vi.hoisted(() => ({ get: vi.fn(), post: vi.fn(), confirm: vi.fn(), error: vi.fn(), success: vi.fn(),
    approved: true, t: (key: string) => key, format: (v: number) => `EGP ${v.toFixed(2)}` }))
vi.mock('react-i18next', () => ({ useTranslation: () => ({ t: s.t }) }))
vi.mock('../contexts/SettingsContext', () => ({ useSettings: () => ({ formatCurrency: s.format }) }))
vi.mock('../common/confirm/ConfirmDialogProvider', () => ({ useConfirmDialog: () => ({ confirm: s.confirm }) }))
vi.mock('../services/api', () => ({ default: { get: s.get, post: s.post } }))
vi.mock('../components/Toast', () => ({ useToast: () => ({ showSuccess: s.success, showError: s.error }) }))
vi.mock('../hooks/useRTL', () => ({ useRTL: () => ({ dir: 'ltr', isRTL: false, locale: 'en' }) }))
vi.mock('../components/AsyncEntitySelect', () => ({ default: () => null }))

beforeEach(() => {
    vi.clearAllMocks(); s.approved = true; s.confirm.mockResolvedValue(true)
    s.get.mockImplementation((url: string) => Promise.resolve({ data: url.startsWith('/expenses?')
        ? { items: [{ id: 'qa-expense', expenseDate: '2026-11-13', amount: 0.01, description: 'QA synthetic expense', paymentMethod: 'Cash', requiresApproval: true, isApproved: s.approved }], totalPages: 1 }
        : [] }))
    s.post.mockImplementation(async () => { s.approved = !s.approved; return { data: {} } })
})

describe('Expense approval and canonical compensation UI', () => {
    it('exposes explicit confirmed reversal and sends matching mandatory idempotency headers', async () => {
        render(<Expenses />)
        fireEvent.click(await screen.findByRole('button', { name: 'expenses.reverseApproval' }))
        await waitFor(() => expect(s.post).toHaveBeenCalledTimes(1))
        expect(s.confirm).toHaveBeenCalledWith(expect.objectContaining({ description: 'expenses.reverseApprovalPolicy' }))
        const [url, body, options] = s.post.mock.calls[0]
        expect(url).toBe('/expenses/qa-expense/approve'); expect(body).toEqual({ expenseId: 'qa-expense', approved: false })
        expect(options.headers['Idempotency-Key']).toEqual(expect.any(String))
        expect(options.headers['X-Request-Id']).toBe(options.headers['Idempotency-Key'])
        await screen.findByRole('button', { name: 'expenses.approve' })
    })

    it('does not reverse when the explicit confirmation is dismissed', async () => {
        s.confirm.mockResolvedValue(false); render(<Expenses />)
        fireEvent.click(await screen.findByRole('button', { name: 'expenses.reverseApproval' }))
        await waitFor(() => expect(s.confirm).toHaveBeenCalledTimes(1))
        expect(s.post).not.toHaveBeenCalled()
    })

    it('retains a failed reversal identity for explicit retry without automatic resubmission', async () => {
        s.post.mockRejectedValue({ response: { data: { message: 'QA uncertain outcome' } } }); render(<Expenses />)
        fireEvent.click(await screen.findByRole('button', { name: 'expenses.reverseApproval' }))
        await waitFor(() => expect(s.error).toHaveBeenCalledWith('QA uncertain outcome'))
        expect(s.post).toHaveBeenCalledTimes(1)
        const first = s.post.mock.calls[0][2].headers['Idempotency-Key']
        fireEvent.click(screen.getByRole('button', { name: 'expenses.reverseApproval' }))
        await waitFor(() => expect(s.post).toHaveBeenCalledTimes(2))
        expect(s.post.mock.calls[1][2].headers['Idempotency-Key']).toBe(first)
    })

    it('uses the same guarded contract for approval, not only reversal', async () => {
        s.approved = false; render(<Expenses />)
        fireEvent.click(await screen.findByRole('button', { name: 'expenses.approve' }))
        await waitFor(() => expect(s.post).toHaveBeenCalledTimes(1))
        expect(s.post.mock.calls[0][1].approved).toBe(true)
        expect(s.post.mock.calls[0][2].headers['Idempotency-Key']).toEqual(expect.any(String))
        expect(s.confirm).not.toHaveBeenCalled()
    })

    it('labels fixed POS discount in EGP in both languages without a currency switch', () => {
        expect(en.pos.fixedDiscount).toBe('Fixed Amount (EGP)')
        expect(ar.pos.fixedDiscount).toBe('مبلغ ثابت (ج.م)')
        expect(en.expenses.reverseApprovalPolicy).toContain('Original expense and journal history remain intact')
    })
})
