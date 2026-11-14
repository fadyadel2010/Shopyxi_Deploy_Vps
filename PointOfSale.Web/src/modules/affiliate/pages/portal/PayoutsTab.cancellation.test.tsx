import { act, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { LanguageProvider } from '../../i18n/LanguageContext'
import PayoutsTab from './PayoutsTab'

const mocks = vi.hoisted(() => ({ getPayouts: vi.fn(), cancelPayout: vi.fn() }))
vi.mock('../../../../services/affiliateApi', () => ({ default: mocks }))
const pending = { id: 'owned-request', requestedAmount: 150, remainingAmount: 150, currency: 'EGP', status: 'Pending', canCancel: true, requestedAtUtc: '2026-10-23T12:00:00Z' }
const balance = { availableForWithdrawal: 450, totalApproved: 600, totalPaid: 0, totalPendingRequests: 150, currency: 'EGP', minimumThreshold: 100, canWithdraw: true, history: [pending] }
const completed = { ...balance, availableForWithdrawal: 600, totalPendingRequests: 0, history: [{ ...pending, status: 'Cancelled', canCancel: false }] }
const formatCurrency = (amount: number, currency: string) => `${currency} ${amount.toFixed(2)}`
const mount = () => render(<LanguageProvider><PayoutsTab formatCurrency={formatCurrency} /></LanguageProvider>)

describe('Persisted withdrawal cancellation', () => {
    beforeEach(() => {
        vi.resetAllMocks()
        localStorage.setItem('affiliate_lang', 'en')
        mocks.getPayouts.mockResolvedValue(balance)
        mocks.cancelPayout.mockResolvedValue({ status: 'Cancelled' })
    })

    it.each(['en', 'ar'])('confirms in %s, sends only request/reason, and reloads server balances/history', async language => {
        localStorage.setItem('affiliate_lang', language)
        mocks.getPayouts.mockResolvedValueOnce(balance).mockResolvedValue(completed)
        mount()
        fireEvent.click((await screen.findAllByRole('button', { name: language === 'ar' ? 'إلغاء طلب السحب' : 'Cancel withdrawal' }))[0])
        const dialog = screen.getByRole('dialog')
        expect(mocks.cancelPayout).not.toHaveBeenCalled()
        fireEvent.change(within(dialog).getByRole('textbox'), { target: { value: '  Changed plans  ' } })
        fireEvent.click(within(dialog).getByRole('button', { name: language === 'ar' ? 'تأكيد الإلغاء' : 'Confirm cancellation' }))
        await waitFor(() => expect(mocks.cancelPayout).toHaveBeenCalledExactlyOnceWith('owned-request', 'Changed plans'))
        await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())
        expect(screen.getAllByText(language === 'ar' ? 'ملغي' : 'Cancelled')).toHaveLength(2)
        expect(screen.getAllByText('EGP 600.00').length).toBeGreaterThan(0)
        expect(screen.queryByRole('button', { name: language === 'ar' ? 'إلغاء طلب السحب' : 'Cancel withdrawal' })).not.toBeInTheDocument()
    })

    it('dismisses confirmation without submitting', async () => {
        mount()
        fireEvent.click((await screen.findAllByRole('button', { name: 'Cancel withdrawal' }))[0])
        fireEvent.click(within(screen.getByRole('dialog')).getByRole('button', { name: 'Keep withdrawal' }))
        expect(mocks.cancelPayout).not.toHaveBeenCalled()
    })

    it('ignores repeated clicks and does not release any balance before the server confirms', async () => {
        let resolve!: (result: unknown) => void
        mocks.cancelPayout.mockReturnValue(new Promise(result => { resolve = result }))
        mocks.getPayouts.mockResolvedValueOnce(balance).mockResolvedValue(completed)
        mount()
        fireEvent.click((await screen.findAllByRole('button', { name: 'Cancel withdrawal' }))[0])
        const confirm = within(screen.getByRole('dialog')).getByRole('button', { name: 'Confirm cancellation' })
        fireEvent.click(confirm); fireEvent.click(confirm)
        expect(mocks.cancelPayout).toHaveBeenCalledTimes(1)
        expect(confirm).toBeDisabled()
        await waitFor(() => expect(screen.getByText('EGP 450.00')).toBeVisible())
        await act(async () => resolve({ status: 'Cancelled' }))
        await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())
    })

    it.each([409, 404, 403, 500])('handles HTTP %s safely and refreshes authoritative data', async status => {
        mocks.cancelPayout.mockRejectedValue({ response: { status } })
        mocks.getPayouts.mockResolvedValueOnce(balance).mockResolvedValue({ ...balance, history: [{ ...pending, status: 'Approved', canCancel: false }] })
        mount()
        fireEvent.click((await screen.findAllByRole('button', { name: 'Cancel withdrawal' }))[0])
        fireEvent.click(within(screen.getByRole('dialog')).getByRole('button', { name: 'Confirm cancellation' }))
        await waitFor(() => expect(mocks.getPayouts).toHaveBeenCalledTimes(2))
        await waitFor(() => expect(screen.queryByRole('button', { name: 'Cancel withdrawal' })).not.toBeInTheDocument())
        expect(screen.getByText('EGP 450.00')).toBeVisible()
    })

    it.each(['Approved', 'Paid', 'Rejected', 'Cancelled'])('never offers cancellation for server-ineligible %s history', async status => {
        mocks.getPayouts.mockResolvedValue({ ...balance, history: [{ ...pending, status, canCancel: false }] })
        mount()
        await screen.findByRole('heading', { name: 'Withdrawal History' })
        expect(screen.queryByRole('button', { name: 'Cancel withdrawal' })).not.toBeInTheDocument()
    })
})
