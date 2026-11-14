import { fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { LanguageProvider, useLanguage } from '../../i18n/LanguageContext'
import OrdersTab from './OrdersTab'
import CommissionActivity, { SourceOrder } from './CommissionActivity'

const api = vi.hoisted(() => ({ getOrders: vi.fn(), getCommissionActivity: vi.fn(), getSourceOrder: vi.fn() }))
vi.mock('../../../../services/affiliateApi', () => ({ default: api }))
const money = (value, currency) => value == null ? '—' : `${currency} ${value.toFixed(2)}`
const canonical = { orderId: 'order', orderNumber: 'SQL-ORDER', orderTotal: 145, orderCurrency: 'EGP', shippingCharge: 30, paymentFee: 5,
    paymentStatus: 'CashOnDelivery', orderStatus: 'Pending', fulfillmentStatus: 'Created', referralCode: 'SQL-REF', attributionSource: 'ReferralLink',
    storeName: 'SQL Store', promotionLinkId: 'link-id', promotedProductId: 'product-id', createdAtUtc: '2026-10-25T10:00:00Z',
    items: [{ orderItemId: 'line', productName: 'SQL Product', quantity: 3, linePaymentAllocated: 0, lineRefunded: 0 }] }
const event = { entryId: 'reversal', recordedAtUtc: '2026-10-25T10:00:00Z', entryType: 'Reversed', amount: 2, currency: 'EGP',
    orderId: 'order', orderItemId: 'line', orderNumber: 'SQL-ORDER', productName: 'SQL Product', originalEntryId: 'earned-id', sourceOrderAvailable: true,
    classification: 'HistoryOnlyUseBalanceApi', reasonCode: 'ReversalReasonNotRecorded' }
function Harness({ children }) {
    const { toggleLang, dir } = useLanguage()
    return <div dir={dir}><button onClick={toggleLang}>Switch</button>{children}</div>
}
const open = children => render(<LanguageProvider><Harness>{children}</Harness></LanguageProvider>)
beforeEach(() => {
    vi.resetAllMocks(); localStorage.setItem('affiliate_lang', 'en')
    api.getOrders.mockResolvedValue({ orders: [{ orderId: 'order', orderNumber: 'SQL-ORDER', currency: 'EGP', status: 'Earned', paidAmount: 100,
        totalCommission: 10, totalApproved: 0, totalPaid: 0, totalReversed: 2, canonical, items: [{ ...canonical.items[0], paidAmount: 100, commissionAmount: 10 }] }], totalPages: 1, totalCount: 1 })
    api.getSourceOrder.mockResolvedValue(canonical)
    api.getCommissionActivity.mockResolvedValue({ entries: [event], totalPages: 2, page: 1 })
})
describe('P5.4A canonical read models', () => {
    it('uses persisted product identity when historical product copy is blank', async () => {
        api.getOrders.mockResolvedValue({ orders: [{ orderId: 'order', orderNumber: 'SQL-ORDER', status: 'Pending', currency: 'EGP', totalCommission: 0,
            canonical, items: [{ orderItemId: 'line', productId: 'persisted-product-id', productName: '', quantity: 1 }] }], totalPages: 1 })
        open(<OrdersTab formatCurrency={money} />)
        expect(await screen.findByText('Product ID: persisted-product-id')).toBeVisible()
        expect(screen.queryByText('No commission line details are available.')).not.toBeInTheDocument()
    })
    it('distinguishes actual order total, quantity, payment state and commission base without guessing', async () => {
        open(<OrdersTab formatCurrency={money} />)
        fireEvent.click(await screen.findByRole('button', { name: 'SQL-ORDER' }))
        const details = screen.getByRole('region', { name: 'Canonical order details' })
        expect(within(details).getByText('EGP 145.00')).toBeVisible()
        expect(within(details).getByText('Cash due on delivery')).toBeVisible()
        expect(within(details).getByText('SQL-REF')).toBeVisible()
        expect(screen.getByText('Purchased quantity: 3')).toBeVisible()
        expect(screen.getAllByText('EGP 100.00').length).toBeGreaterThan(0)
        expect(screen.queryByText(/quantity, payment status and referral context are not supplied/)).not.toBeInTheDocument()
        fireEvent.click(screen.getByRole('button', { name: 'Switch' }))
        expect(screen.getByText('الكمية المشتراة: ٣')).toBeVisible()
        expect(screen.getByText('مستحق عند التسليم')).toBeVisible()
        expect(api.getOrders).toHaveBeenCalledTimes(1)
    })
    it('shows immutable chronological events, parent linkage, bounded pages and source URL', async () => {
        open(<CommissionActivity formatCurrency={money} />)
        expect(await screen.findByRole('link', { name: 'Source order: SQL-ORDER' })).toHaveAttribute('href', '?tab=orders&orderId=order')
        expect(screen.getByText('EGP 2.00')).toBeVisible()
        expect(screen.getByText(/does not record a dedicated reversal reason/)).toBeVisible()
        expect(screen.getByText('earned-id')).toBeInTheDocument()
        api.getCommissionActivity.mockResolvedValue({ entries: [{ ...event, entryId: 'qa', isSyntheticQa: true }], totalPages: 2 })
        fireEvent.click(screen.getByRole('button', { name: 'Next' }))
        await waitFor(() => expect(api.getCommissionActivity).toHaveBeenLastCalledWith(2, 10))
        expect(await screen.findByText(/SYNTHETIC QA/)).toHaveTextContent('excluded from payment and withdrawals')
        fireEvent.click(screen.getByRole('button', { name: 'Switch' }))
        expect(await screen.findByText('دفتر العمولة الزمني')).toBeVisible()
        expect(screen.getByText(/مستبعد من الدفع والسحب/)).toBeVisible()
    })
    it.each([null, { entries: [] }])('distinguishes unavailable/malformed activity from actual empty history: %s', async response => {
        api.getCommissionActivity.mockResolvedValue(response)
        open(<CommissionActivity formatCurrency={money} />)
        if (response) expect(await screen.findByText('No commission ledger events recorded.')).toBeVisible()
        else expect(await screen.findByRole('alert')).toHaveTextContent('does not mean there is no financial history')
    })
    it('loads an independently scoped source order and never substitutes a list/projection fallback on failure', async () => {
        api.getSourceOrder.mockRejectedValue(new Error('Not in scope'))
        open(<SourceOrder orderId="foreign" formatCurrency={money} />)
        expect(await screen.findByRole('alert')).toHaveTextContent('outside your authorized scope')
        expect(screen.queryByText('SQL-ORDER')).not.toBeInTheDocument()
        expect(api.getOrders).not.toHaveBeenCalled()
    })
})
