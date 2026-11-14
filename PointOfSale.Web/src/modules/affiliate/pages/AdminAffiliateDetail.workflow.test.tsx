import { act, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { MemoryRouter, Route, Routes } from 'react-router-dom'
import i18n from '../../../i18n/config'
// @ts-expect-error Existing JavaScript page has no declaration file.
import AdminAffiliateDetail from './AdminAffiliateDetail'

const api = vi.hoisted(() => ({ getAffiliateDetail: vi.fn(), getAffiliateOrders: vi.fn(), getAffiliateEarnings: vi.fn(),
    getAffiliateWithdrawals: vi.fn(), approveCommissions: vi.fn(), recordPayment: vi.fn(), markPayoutPaid: vi.fn() }))
vi.mock('../../../services/adminAffiliateApi', () => ({ default: api }))
vi.mock('../../../contexts/SettingsContext', () => ({ useSettings: () => ({ formatCurrency: (amount: number, currency: string) => `${currency} ${amount.toFixed(2)}` }) }))

const summary = { currency: 'EGP', totalEarned: 1700, totalApproved: 850, totalPaid: 0, totalReversed: 0, netEarnings: 1700 }
const renderDetail = () => render(<MemoryRouter initialEntries={['/admin/affiliates/fixture']}><Routes><Route path="/admin/affiliates/:id" element={<AdminAffiliateDetail />} /></Routes></MemoryRouter>)
async function tab(name: string) { fireEvent.click(await screen.findByRole('button', { name: new RegExp(i18n.t(`adminAffiliateDetail.tabs.${name}`)) })) }

describe('P5.11 canonical settlement guidance and safe failures', () => {
    beforeEach(async () => {
        vi.resetAllMocks(); await i18n.changeLanguage('en')
        api.getAffiliateDetail.mockResolvedValue({ displayName: 'Fixture only', status: 'Active', cashboxBalance: 0, earningsByCurrency: [summary] })
        api.getAffiliateOrders.mockImplementation((_id: string, _page: number, _size: number, status: string) => Promise.resolve({
            orders: status === 'Approved' ? [{ orderId: 'order-57', orderNumber: 'FIXTURE-57', totalCommission: 850, currency: 'EGP' }] : [],
        }))
        api.getAffiliateEarnings.mockResolvedValue([summary])
        api.getAffiliateWithdrawals.mockResolvedValue({ totalCount: 1, items: [{ id: 'request', requestedAmount: 850,
            approvedAmount: 850, currency: 'EGP', status: 'Approved' }] })
    })
    afterEach(() => vi.unstubAllEnvs())

    it.each(['en', 'ar'])('shows the complete %s workflow even without requests and directs settlement intent to Withdrawals', async language => {
        await i18n.changeLanguage(language)
        api.getAffiliateWithdrawals.mockResolvedValue({ items: [] })
        renderDetail(); await tab('settlement')
        const help = await screen.findByRole('region', { name: i18n.t('adminAffiliateDetail.workflow.title') })
        expect(within(help).getAllByRole('listitem')).toHaveLength(6)
        for (const step of ['review', 'destination', 'request', 'approve', 'evidence', 'verify', 'approvalNotPayment', 'qa'])
            expect(within(help).getByText(i18n.t(`adminAffiliateDetail.workflow.${step}`))).toBeVisible()
        expect(help).toHaveTextContent('SIMULATED — NO REAL MONEY TRANSFER')
        expect(help).toHaveTextContent('SIMULATED QA PAYOUT - NO MONEY TRANSFER')
        await screen.findByText('FIXTURE-57')
        expect(screen.queryByRole('checkbox')).not.toBeInTheDocument()
        expect(screen.queryByRole('button', { name: i18n.t('adminAffiliateDetail.actions.recordPayment') })).not.toBeInTheDocument()
        fireEvent.click(within(help).getByRole('button'))
        expect(await screen.findByText(i18n.t('adminAffiliateDetail.withdrawals.empty.title'))).toBeVisible()
        expect(screen.getByRole('region', { name: i18n.t('adminAffiliateDetail.workflow.title') })).toBeVisible()
        expect(api.recordPayment).not.toHaveBeenCalled(); expect(api.markPayoutPaid).not.toHaveBeenCalled()
    })

    it.each(['en', 'ar'])('keeps trusted evidence rejection actionable and persistent in %s, without claiming Paid', async language => {
        await i18n.changeLanguage(language)
        const code = 'AFFILIATE_PAYOUT_VERIFIED_EXTERNAL_EVIDENCE_REQUIRED'
        api.markPayoutPaid.mockRejectedValue({ response: { status: 409, data: { code, message: 'A reference alone is not confirmation.' } } })
        renderDetail(); await tab('withdrawals')
        fireEvent.click(await screen.findByRole('button', { name: i18n.t('adminAffiliateDetail.withdrawals.actions.markPaid') }))
        const dialog = screen.getByRole('dialog')
        expect(dialog).toHaveTextContent(i18n.t('adminAffiliateDetail.workflow.evidence'))
        fireEvent.change(within(dialog).getByLabelText(i18n.t('adminAffiliateDetail.withdrawals.modal.paymentReference')), { target: { value: 'UNVERIFIED-FIXTURE' } })
        fireEvent.change(within(dialog).getByLabelText(i18n.t('adminAffiliateDetail.withdrawals.modal.adminNotes')), { target: { value: 'Not payment evidence' } })
        fireEvent.click(within(dialog).getByRole('button', { name: i18n.t('adminAffiliateDetail.withdrawals.actions.markPaid') }))
        const alert = await within(dialog).findByRole('alert')
        expect(alert).toHaveTextContent(i18n.t(`adminAffiliateDetail.financialErrors.${code}`))
        fireEvent.click(within(alert).getByText(i18n.t('adminAffiliateDetail.workflow.supportDetails')))
        expect(within(alert).getByText(code)).toBeVisible()
        expect(screen.getByText(i18n.t('adminAffiliateDetail.withdrawals.statuses.Approved'), { selector: 'span' })).toBeVisible()
        expect(screen.queryByText(i18n.t('adminAffiliateDetail.withdrawals.toast.markedPaid'))).not.toBeInTheDocument()
        expect(api.markPayoutPaid).toHaveBeenCalledExactlyOnceWith('request', 'UNVERIFIED-FIXTURE', 'Not payment evidence')
        expect(api.recordPayment).not.toHaveBeenCalled()
    })

    it('retains an unmapped server rejection reason in the dialog rather than replacing it with success', async () => {
        api.markPayoutPaid.mockRejectedValue({ response: { data: { code: 'AFFILIATE_PAYOUT_NEW_AUTHORITY_CONFLICT', message: 'The destination bank confirmation must be reconciled again.' } } })
        renderDetail(); await tab('withdrawals')
        fireEvent.click(await screen.findByRole('button', { name: 'Register verified payout' }))
        fireEvent.click(within(screen.getByRole('dialog')).getByRole('button', { name: 'Register verified payout' }))
        expect(await screen.findByRole('alert')).toHaveTextContent('The destination bank confirmation must be reconciled again.')
    })

    it.each(['development', 'production', 'staging', 'qa'])('has no simulation execution or enable control under default %s configuration', async mode => {
        vi.stubEnv('MODE', mode)
        renderDetail(); await tab('withdrawals')
        await screen.findByText('EGP 850.00')
        expect(screen.queryByRole('button', { name: /simulat|محاكاة/i })).not.toBeInTheDocument()
        expect(screen.queryByRole('checkbox')).not.toBeInTheDocument()
        expect(api.markPayoutPaid).not.toHaveBeenCalled()
    })

    it.each(['en', 'ar'])('identifies excluded synthetic commission history and historical simulated payouts in %s', async language => {
        await i18n.changeLanguage(language)
        api.getAffiliateOrders.mockImplementation((_id: string, _page: number, _size: number, status: string) => Promise.resolve({
            orders: status === 'Approved' ? [{ orderId: 'synthetic', orderNumber: 'SYNTHETIC-FIXTURE', totalCommission: 5, currency: 'EGP', isSyntheticQa: true }] : [],
        }))
        api.getAffiliateWithdrawals.mockResolvedValue({ items: [{ id: 'simulated', status: 'Paid', requestedAmount: 850, currency: 'EGP', paymentReference: 'QA-SIMULATED-NO-TRANSFER:fixture' }] })
        renderDetail(); await tab('settlement')
        expect(await screen.findByText(i18n.t('adminAffiliateDetail.labels.syntheticExcluded'))).toBeVisible()
        await tab('withdrawals')
        expect(await screen.findByText(i18n.t('adminAffiliateDetail.labels.simulatedPayout'))).toBeVisible()
        expect(screen.queryByRole('button', { name: i18n.t('adminAffiliateDetail.withdrawals.actions.markPaid') })).not.toBeInTheDocument()
        expect(api.markPayoutPaid).not.toHaveBeenCalled()
    })

    it('uses server summary amounts, never adds approval events to earned or treats an approved request as Paid', async () => {
        const { container } = renderDetail(); await tab('earnings')
        await waitFor(() => expect(api.getAffiliateEarnings).toHaveBeenCalledWith('fixture'))
        const earnings = within((await screen.findByText(i18n.t('adminAffiliateDetail.workflow.summary'))).parentElement!)
        const earned = earnings.getByText(i18n.t('adminAffiliateDetail.earnings.cards.totalEarned'), { selector: 'p' })
        expect(earned.parentElement).toHaveTextContent('EGP 1700.00')
        expect(earnings.getByText(i18n.t('adminAffiliateDetail.earnings.cards.approved'), { selector: 'p' }).parentElement).toHaveTextContent('EGP 850.00')
        expect(earnings.getByText(i18n.t('adminAffiliateDetail.earnings.cards.paid'), { selector: 'p' }).parentElement).toHaveTextContent('EGP 0.00')
        expect(screen.queryByText('EGP 2550.00')).not.toBeInTheDocument()
        expect(container.querySelector('.bg-emerald-300.transition-all')).toHaveStyle({ width: '50%' })
        await act(() => i18n.changeLanguage('ar'))
        expect(screen.getByText(i18n.t('adminAffiliateDetail.workflow.summary'))).toBeVisible()
    })
})
