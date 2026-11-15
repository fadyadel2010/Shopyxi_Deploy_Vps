import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import AdminManualPayoutReturnDialog from './AdminManualPayoutReturnDialog'

const review = vi.hoisted(() => vi.fn())
const confirm = vi.hoisted(() => vi.fn())
vi.mock('../../../services/adminAffiliateApi', () => ({ default: { reviewManualPayoutReturn: review, confirmManualPayoutReturn: confirm } }))
vi.mock('react-i18next', () => ({ useTranslation: () => ({ t: (key: string) => key }) }))
const prefix = 'adminAffiliateDetail.manualReturn.'
const snapshot = { requestId: 'request-1', status: 'Paid', amount: 150, currency: 'EGP', sourceAccountCode: 'CASH-LOCAL',
    canReturn: true, reviewFingerprint: 'review-authority', attestation: 'SERVER ATTESTATION', receipt: null }
const mount = (onCommitted = vi.fn()) => render(<AdminManualPayoutReturnDialog requestId="request-1" formatCurrency={(v: number) => `${v} EGP`} onClose={vi.fn()} onCommitted={onCommitted} />)
async function completeForm() {
    await screen.findByLabelText(prefix + 'reference')
    fireEvent.change(screen.getByLabelText(prefix + 'reference'), { target: { value: 'LOCAL-RETURN-RECEIPT' } })
    fireEvent.change(screen.getByLabelText(prefix + 'reason'), { target: { value: 'Full returned cash' } })
    fireEvent.click(screen.getByRole('checkbox'))
}
describe('manual cash return candidate authority', () => {
    beforeEach(() => { review.mockReset(); confirm.mockReset(); review.mockResolvedValue(snapshot) })
    it('requires current review, actual receipt, reason and explicit attestation', async () => {
        mount()
        const action = await screen.findByRole('button', { name: prefix + 'confirm' })
        expect(action).toBeDisabled()
        fireEvent.click(screen.getByRole('checkbox'))
        expect(action).toBeDisabled()
        expect(confirm).not.toHaveBeenCalled()
    })
    it('uses server-bound authority and waits for a durable Returned receipt', async () => {
        const committed = vi.fn()
        confirm.mockResolvedValue({ returnId: 'return-1' })
        review.mockResolvedValueOnce(snapshot).mockResolvedValueOnce({ ...snapshot, canReturn: false, status: 'Returned', receipt: { id: 'return-1' } })
        mount(committed); await completeForm()
        fireEvent.click(screen.getByRole('button', { name: prefix + 'confirm' }))
        await screen.findByText(prefix + 'recorded')
        expect(confirm).toHaveBeenCalledWith('request-1', { reviewFingerprint: 'review-authority', receivedManually: true,
            attestation: 'SERVER ATTESTATION', receiptReference: 'LOCAL-RETURN-RECEIPT', reason: 'Full returned cash' })
        expect(committed).toHaveBeenCalledOnce()
    })
    it('does not optimistically announce success when post-write receipt cannot be proven', async () => {
        const committed = vi.fn()
        confirm.mockResolvedValue({ returnId: 'return-1' })
        review.mockResolvedValueOnce(snapshot).mockResolvedValueOnce(snapshot)
        mount(committed); await completeForm()
        fireEvent.click(screen.getByRole('button', { name: prefix + 'confirm' }))
        await screen.findByRole('alert')
        expect(committed).not.toHaveBeenCalled()
        expect(screen.queryByText(prefix + 'recorded')).not.toBeInTheDocument()
    })
    it('never offers confirmation for a stale or foreign review', async () => {
        review.mockResolvedValue({ ...snapshot, requestId: 'different-request' })
        mount(); await completeForm()
        expect(screen.getByRole('button', { name: prefix + 'confirm' })).toBeDisabled()
        expect(confirm).not.toHaveBeenCalled()
    })
    it('serializes confirmation clicks while a request is in flight', async () => {
        confirm.mockReturnValue(new Promise(() => {}))
        mount(); await completeForm()
        const action = screen.getByRole('button', { name: prefix + 'confirm' })
        fireEvent.click(action); fireEvent.click(action)
        await waitFor(() => expect(confirm).toHaveBeenCalledOnce())
        expect(screen.getByRole('button', { name: 'adminAffiliateDetail.actions.cancel' })).toBeDisabled()
    })
})
