import { fireEvent, render, screen, within } from '@testing-library/react'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import i18n from '../../../i18n/config'
// @ts-expect-error Existing JavaScript component has no declaration.
import AdminManualPayoutDialog from './AdminManualPayoutDialog'

const api = vi.hoisted(() => ({ reviewManualPayout: vi.fn(), confirmManualPayout: vi.fn() }))
vi.mock('../../../services/adminAffiliateApi', () => ({ default: api }))
const review = { requestId: 'request', affiliateName: 'Task-owned affiliate', amount: 150, currency: 'EGP', status: 'Approved',
    destination: { type: 'Other', number: 'AUTHORITATIVE-DESTINATION' }, destinationFingerprint: 'A'.repeat(64),
    sources: [{ cashboxId: 'cashbox', cashboxName: 'Task-owned cashbox', accountCode: '1010-TASK', sourceFingerprint: 'reviewed-source', availableFunds: 500 },
        { cashboxId: 'empty', cashboxName: 'Empty cashbox', accountCode: '1010-EMPTY', sourceFingerprint: 'empty-source', availableFunds: 0 }],
    eligibleCommissions: [{ id: 'entry', orderId: 'order', commissionAmount: 150, currency: 'EGP' }],
    attestation: 'I CONFIRM THE MANUAL PAYOUT HAS BEEN PERFORMED', confirmation: null, journals: [] }
const formatCurrency = (amount: number, currency: string) => `${currency} ${amount.toFixed(2)}`
const t = (key: string) => i18n.t(`adminAffiliateDetail.manual.${key}`)
function setup() {
    const onCommitted = vi.fn(), onClose = vi.fn()
    render(<AdminManualPayoutDialog requestId="request" formatCurrency={formatCurrency} onCommitted={onCommitted} onClose={onClose} />)
    return { onCommitted, onClose }
}
async function selectFunded() { fireEvent.change(await screen.findByLabelText(t('source')), { target: { value: 'cashbox' } }) }

describe('administrator manual payout confirmation', () => {
    beforeEach(async () => { vi.resetAllMocks(); await i18n.changeLanguage('en'); api.reviewManualPayout.mockResolvedValue(review) })

    it.each(['en', 'ar'])('requires explicit attestation and shows selected cashbox, destination and amount in %s', async language => {
        await i18n.changeLanguage(language); setup(); await selectFunded()
        const dialog = screen.getByRole('dialog')
        expect(dialog).toHaveTextContent('EGP 150.00'); expect(dialog).toHaveTextContent('AUTHORITATIVE-DESTINATION')
        expect(dialog).toHaveTextContent(t('warning')); expect(dialog).toHaveTextContent('Task-owned cashbox (1010-TASK)')
        expect(screen.getByRole('button', { name: t('confirm') })).toBeDisabled()
        fireEvent.click(screen.getByRole('checkbox', { name: t('attestation') }))
        expect(screen.getByRole('button', { name: t('confirm') })).toBeEnabled()
        expect(api.confirmManualPayout).not.toHaveBeenCalled()
    })

    it('does not mark Paid optimistically, prevents duplicate submissions, then reads persisted receipt', async () => {
        let complete!: (value: object) => void
        api.confirmManualPayout.mockReturnValue(new Promise(resolve => { complete = resolve }))
        const { onCommitted } = setup(); await selectFunded()
        fireEvent.click(screen.getByRole('checkbox')); const button = screen.getByRole('button', { name: t('confirm') })
        fireEvent.click(button); fireEvent.click(button)
        expect(api.confirmManualPayout).toHaveBeenCalledTimes(1); expect(button).toBeDisabled()
        expect(onCommitted).not.toHaveBeenCalled(); expect(screen.queryByText(t('recorded'))).not.toBeInTheDocument()
        api.reviewManualPayout.mockResolvedValue({ ...review, status: 'Paid', confirmation: { id: 'persisted-audit',
            executedAtUtc: '2026-10-26T14:00:00Z', sourceAccountName: 'Task-owned cashbox', sourceAccountCode: '1010-TASK',
            administratorId: 'server-actor', administratorRole: 'Admin' }, journals: [{ id: 'journal', journalNumber: 'J-P512' }] })
        complete({ status: 'Paid' })
        expect(await screen.findByText(t('recorded'))).toBeVisible()
        expect(screen.getByRole('status')).toHaveTextContent('persisted-audit'); expect(screen.getByRole('status')).toHaveTextContent('J-P512')
        expect(onCommitted).toHaveBeenCalledTimes(1)
        expect(api.confirmManualPayout).toHaveBeenCalledWith('request', expect.objectContaining({ cashboxId: 'cashbox', amount: 150,
            currency: 'EGP', method: 'Cash', performedManually: true, destinationFingerprint: review.destinationFingerprint,
            sourceFingerprint: 'reviewed-source', attestation: review.attestation }))
    })

    it.each(['en', 'ar'])('blocks underfunding and presents localized stale/permission errors in %s', async language => {
        await i18n.changeLanguage(language); setup()
        fireEvent.change(await screen.findByLabelText(t('source')), { target: { value: 'empty' } })
        expect(screen.getByRole('checkbox')).toBeDisabled(); expect(screen.getByRole('button', { name: t('confirm') })).toBeDisabled()
        expect(screen.getByRole('alert')).toHaveTextContent(i18n.t('adminAffiliateDetail.financialErrors.AFFILIATE_MANUAL_SOURCE_UNDERFUNDED'))
        await selectFunded(); fireEvent.click(screen.getByRole('checkbox'))
        api.confirmManualPayout.mockRejectedValueOnce({ response: { status: 403, data: { code: 'AFFILIATE_MANUAL_ADMIN_REQUIRED' } } })
        fireEvent.click(screen.getByRole('button', { name: t('confirm') }))
        expect(await screen.findByRole('alert')).toHaveTextContent(i18n.t('adminAffiliateDetail.financialErrors.AFFILIATE_MANUAL_ADMIN_REQUIRED'))
        expect(screen.queryByText(t('recorded'))).not.toBeInTheDocument()
    })

    it('Pending review keeps approval separate and cannot attest or execute', async () => {
        api.reviewManualPayout.mockResolvedValue({ ...review, status: 'Pending' }); setup()
        expect(await screen.findByText(t('approveFirst'))).toBeVisible()
        expect(screen.queryByRole('checkbox')).not.toBeInTheDocument()
        expect(screen.queryByRole('button', { name: t('confirm') })).not.toBeInTheDocument()
        expect(api.confirmManualPayout).not.toHaveBeenCalled()
    })

    it('missing source configuration never invents an account', async () => {
        api.reviewManualPayout.mockResolvedValue({ ...review, sources: [] }); setup()
        expect(await screen.findByText(i18n.t('adminAffiliateDetail.financialErrors.AFFILIATE_MANUAL_SOURCE_INVALID'))).toBeVisible()
        expect(within(screen.getByLabelText(t('source'))).getAllByRole('option')).toHaveLength(1)
        expect(screen.getByRole('link', { name: 'Open Cashbox management to create and open a cashbox' }))
            .toHaveAttribute('href', '/treasury/cashboxes')
        expect(screen.getByRole('button', { name: t('confirm') })).toBeDisabled()
    })

    it('an old API response without reviewed source binding cannot enable confirmation', async () => {
        api.reviewManualPayout.mockResolvedValue({ ...review, sources: [{ ...review.sources[0], sourceFingerprint: undefined }] })
        setup(); await selectFunded()
        expect(screen.getByRole('checkbox')).toBeDisabled()
        expect(screen.getByRole('button', { name: t('confirm') })).toBeDisabled()
        expect(screen.getByText(i18n.t('adminAffiliateDetail.financialErrors.AFFILIATE_MANUAL_SOURCE_INVALID'))).toBeVisible()
        expect(api.confirmManualPayout).not.toHaveBeenCalled()
    })
})
