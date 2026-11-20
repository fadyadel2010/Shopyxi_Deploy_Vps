import { act, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { beforeEach, expect, it, vi } from 'vitest'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter } from 'react-router-dom'
import '../../design-system/test/radixJsdomPolyfills'
import RefundsPage, { ActionModal, ExecuteRefundModal } from './RefundsPage'
import { ConfirmDialogProvider } from '../../common/confirm/ConfirmDialogProvider'
import i18n from '../../i18n/config'
import type { SuperAdminRefundDetails } from './refundsService'

const service = vi.hoisted(() => ({ list: vi.fn(), details: vi.fn(), approve: vi.fn() }))
vi.mock('./refundsService', async importOriginal => {
    const original = await importOriginal<typeof import('./refundsService')>()
    return { ...original, getSuperAdminRefunds: service.list, getSuperAdminRefundDetails: service.details,
        approveSuperAdminRefund: service.approve }
})
vi.mock('../orders/ordersService', () => ({ getSuperAdminOrderTenants: async () => [] }))
const id = (n: number) => `${n}${'1'.repeat(7)}-1111-4111-8111-111111111111`
const row = { refundId: id(1), orderId: id(2), orderNumber: 'SYNTHETIC', vendorId: id(3), vendorName: 'Vendor',
    customerId: id(4), customerName: 'Customer', productName: 'Product', quantity: 1, amount: 10, currency: 'EGP',
    status: 'Requested', createdAt: '2026-11-20T00:00:00Z' }
const detail = {
    ...row, reasonCode: 'DamagedItem', reason: 'Damaged', mediaUrls: [], media: [], unitPriceSnapshot: 10,
    updatedAt: '2026-11-20T00:01:00Z', executionAttempts: 0, canApprove: true, canReject: true, canExecute: false, canRetry: false,
    order: { orderId: id(2), orderNumber: 'SYNTHETIC', paymentMethodName: 'Card', paymentStatus: 'Collected',
        totalAmount: 20, shippingTotal: 0, currency: 'EGP', createdAt: row.createdAt },
    customer: { customerId: id(4), name: 'Customer', email: 'synthetic@example.invalid' },
    item: { orderItemId: id(5), subOrderId: id(6), vendorId: id(3), vendorName: 'Vendor', productName: 'Product',
        purchasedQuantity: 1, unitPrice: 10, totalPrice: 10, platformCommissionAmount: 1, vendorEarningAmount: 9, status: 'Delivered' },
    financialPreview: { paymentType: 'Online', hasPayoutOccurred: false, commissionReversalAmount: 1, vendorEarningChange: 9,
        gatewayRefundRequired: true, paymentId: null, paymentProvider: null, paymentRecordStatus: 'Paid',
        paymentRefundedAmountBefore: 0, paymentRefundedAmountAfter: 10, vendorNetEarningsBefore: 9, vendorNetEarningsAfter: 0,
        hasPaidOutVendorEarnings: false, hasPendingPayoutReservation: false, executionPath: 'OnlineBeforePayout',
        platformCashDelta: -10, platformPayableDelta: 0, vendorDebtDelta: 0, commissionReceivableDelta: -1,
        vendorPayableBefore: 9, vendorPayableAfter: 0, vendorDebtBefore: 0, vendorDebtAfter: 0, impactSummary: 'Synthetic preview' },
} as unknown as SuperAdminRefundDetails
const list = { refunds: [row], meta: { totalCount: 1, totalPages: 1, page: 1, pageSize: 20 } }
beforeEach(async () => {
    vi.clearAllMocks()
    await i18n.changeLanguage('en')
    service.list.mockResolvedValue(list)
    service.details.mockResolvedValue(detail)
})

it.each(['en', 'ar'])('action dialog has labels, contained pending focus and no pending dismissal in %s', async lang => {
    await i18n.changeLanguage(lang)
    const cancel = vi.fn()
    const props = { title: 'Review refund', description: 'Review current refund', confirmLabel: 'Approve',
        confirmTone: '', noteLabel: 'Internal note', noteValue: '', notePlaceholder: '', onNoteChange: vi.fn(),
        onCancel: cancel, onConfirm: vi.fn() }
    const view = render(<ActionModal {...props} isSubmitting={false} />)
    const dialog = screen.getByRole('dialog')
    expect(dialog).toHaveAccessibleName('Review refund')
    expect(within(dialog).getByLabelText('Internal note')).toBeInTheDocument()
    expect(dialog).toHaveAttribute('dir', lang === 'ar' ? 'rtl' : 'ltr')
    view.rerender(<ActionModal {...props} isSubmitting />)
    await userEvent.setup().tab()
    expect(dialog).toContainElement(document.activeElement as HTMLElement)
    fireEvent.keyDown(dialog, { key: 'Escape' })
    expect(cancel).not.toHaveBeenCalled()
    expect(within(dialog).getByRole('button', { name: i18n.t('superAdmin.refundsPage.actions.close') })).toBeDisabled()
})

it('manual payout fields have labels and pending close is disabled', () => {
    render(<ExecuteRefundModal refund={detail} method="VodafoneCash" transactionReference="" notes=""
        confirmMoneySent={false} confirmMethodOverride={false} isSubmitting
        onMethodChange={vi.fn()} onTransactionReferenceChange={vi.fn()} onNotesChange={vi.fn()}
        onConfirmMoneySentChange={vi.fn()} onConfirmMethodOverrideChange={vi.fn()} onCancel={vi.fn()} onConfirm={vi.fn()} />)
    expect(screen.getByLabelText(i18n.t('superAdmin.refundsPage.execute.method'))).toBeDisabled()
    expect(screen.getByLabelText(i18n.t('superAdmin.refundsPage.execute.transactionReference'))).toBeDisabled()
})

it('locks before authority re-entry and keeps the pending modal through mutation settlement', async () => {
    const client = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } })
    render(<QueryClientProvider client={client}><MemoryRouter initialEntries={[`/super-admin/refunds?refundId=${row.refundId}`]}>
        <ConfirmDialogProvider><RefundsPage /></ConfirmDialogProvider>
    </MemoryRouter></QueryClientProvider>)
    const drawer = await screen.findByRole('dialog')
    const approveName = i18n.t('superAdmin.refundsPage.actions.approveAndStartReturnFlow')
    const open = await within(drawer).findByRole('button', { name: approveName })
    fireEvent.click(open)
    const action = screen.getByRole('dialog', { name: i18n.t('superAdmin.refundsPage.actionModal.titles.approve') })
    let authorize!: (value: unknown) => void
    let complete!: (value: unknown) => void
    service.list.mockReturnValueOnce(new Promise(resolve => { authorize = resolve }))
    service.approve.mockReturnValueOnce(new Promise(resolve => { complete = resolve }))
    const confirm = within(action).getByRole('button', { name: i18n.t('superAdmin.refundsPage.actionModal.confirmLabels.approve') })
    const before = service.list.mock.calls.length
    act(() => { fireEvent.click(confirm); fireEvent.click(confirm) })
    expect(service.list.mock.calls.length).toBe(before + 1)
    expect(action).toBeInTheDocument()
    expect(within(action).getByRole('button', { name: i18n.t('superAdmin.refundsPage.actions.cancel') })).toBeDisabled()
    expect(service.approve).not.toHaveBeenCalled()
    await act(async () => { authorize(list) })
    await waitFor(() => expect(service.approve).toHaveBeenCalledTimes(1))
    expect(action).toBeInTheDocument()
    await act(async () => { complete({ ...detail, status: 'Approved' }) })
    await waitFor(() => expect(screen.queryByRole('dialog', { name: i18n.t('superAdmin.refundsPage.actionModal.titles.approve') })).toBeNull())
    client.clear()
})
