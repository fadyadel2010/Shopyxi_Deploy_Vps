import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import { beforeEach, describe, expect, it, vi } from 'vitest'
// @ts-expect-error Existing JavaScript page has no declaration file.
import AdminAffiliateDetail from './AdminAffiliateDetail'

const mocks = vi.hoisted(() => ({ detail: vi.fn(), orders: vi.fn(), withdrawals: vi.fn(), approve: vi.fn(), reject: vi.fn(), paid: vi.fn(), legacyPaid: vi.fn() }))
vi.mock('react-router-dom', () => ({ useParams: () => ({ id: 'disposable-affiliate' }), useNavigate: () => vi.fn() }))
vi.mock('react-i18next', () => ({ useTranslation: () => ({ t: (key: string) => key, i18n: { resolvedLanguage: 'en' } }) }))
vi.mock('../../../contexts/SettingsContext', () => ({ useSettings: () => ({ formatCurrency: (amount: number, currency: string) => `${currency} ${amount.toFixed(2)}` }) }))
vi.mock('../../../services/adminAffiliateApi', () => ({ default: {
    getAffiliateDetail: mocks.detail, getAffiliateOrders: mocks.orders, getAffiliateWithdrawals: mocks.withdrawals,
    approvePayoutRequest: mocks.approve, rejectPayoutRequest: mocks.reject, markPayoutPaid: mocks.paid,
    recordPayment: mocks.legacyPaid,
} }))

const action = (name: string) => `adminAffiliateDetail.withdrawals.actions.${name}`
async function open() {
    render(<AdminAffiliateDetail />)
    fireEvent.click(await screen.findByRole('button', { name: /adminAffiliateDetail.tabs.withdrawals/ }))
}

describe('Existing Admin withdrawal review surface', () => {
    it('directs commission-payment intent to withdrawals without calling the legacy payment endpoint, even with an empty cashbox', async () => {
        mocks.orders.mockImplementation((_id: string, _page: number, _size: number, status: string) => Promise.resolve({
            orders: status === 'Approved' ? [{ orderId: 'approved-order', orderNumber: 'TASK-850', totalCommission: 850, currency: 'EGP' }] : [],
        }))
        render(<AdminAffiliateDetail />)
        fireEvent.click(await screen.findByRole('button', { name: /adminAffiliateDetail.tabs.settlement/ }))
        fireEvent.click(await screen.findByRole('button', { name: 'adminAffiliateDetail.settlement.readyForPayment.manageWithdrawals' }))
        await waitFor(() => expect(mocks.withdrawals).toHaveBeenCalled())
        expect(mocks.legacyPaid).not.toHaveBeenCalled()
        expect(mocks.paid).not.toHaveBeenCalled()
        expect(screen.getByText('adminAffiliateDetail.workflow.approvalNotPayment')).toBeVisible()
    })
    it('shows persisted cancellation in history without any payout actions', async () => {
        mocks.withdrawals.mockResolvedValue({ totalCount: 1, totalPages: 1, items: [{
            id: 'cancelled-task-request', affiliateName: 'Disposable affiliate', status: 'Cancelled',
            requestedAmount: 150, remainingAmount: 150, currency: 'EGP', requestedAtUtc: '2026-10-23T00:00:00Z',
        }] })
        render(<AdminAffiliateDetail />)
        fireEvent.click(await screen.findByRole('button', { name: /adminAffiliateDetail.tabs.withdrawals/ }))
        expect(await screen.findByText('adminAffiliateDetail.statuses.cancelled', { selector: 'span' })).toBeVisible()
        expect(screen.queryByRole('button', { name: 'adminAffiliateDetail.withdrawals.actions.approve' })).not.toBeInTheDocument()
        expect(screen.queryByRole('button', { name: 'adminAffiliateDetail.withdrawals.actions.markPaid' })).not.toBeInTheDocument()
    })
    beforeEach(() => {
        vi.clearAllMocks()
        mocks.detail.mockResolvedValue({ displayName: 'Disposable affiliate', status: 'Active', earningsByCurrency: [{ currency: 'EGP', totalApproved: 500, syntheticApprovedExcluded: 900 }] })
        mocks.orders.mockResolvedValue({ orders: [] })
        mocks.withdrawals.mockResolvedValue({ items: [{ id: 'test-request', affiliateName: 'Disposable affiliate', requestedAmount: 150, currency: 'EGP', status: 'Pending' }] })
        mocks.approve.mockResolvedValue({})
        mocks.reject.mockResolvedValue({})
    })

    it('renders server currency summaries without block elements inside paragraphs', async () => {
        const errors = vi.spyOn(console, 'error')
        try {
            const { container } = render(<AdminAffiliateDetail />)
            await screen.findByRole('heading', { name: 'Disposable affiliate' })
            expect(screen.getByText('EGP 500.00')).toBeVisible()
            expect(container.querySelector('p div')).toBeNull()
            expect(errors.mock.calls.some(args => args.some(arg => String(arg).includes('validateDOMNesting')))).toBe(false)
        } finally {
            errors.mockRestore()
        }
    })

    it('keeps withdrawal loading rows inside a table body', async () => {
        mocks.withdrawals.mockReturnValue(new Promise(() => {}))
        const errors = vi.spyOn(console, 'error')
        try {
            const { container } = render(<AdminAffiliateDetail />)
            fireEvent.click(await screen.findByRole('button', { name: /adminAffiliateDetail.tabs.withdrawals/ }))
            expect(container.querySelectorAll('table > tbody > tr')).toHaveLength(3)
            expect(container.querySelector('div > tr')).toBeNull()
            expect(errors.mock.calls.some(args => args.some(arg => String(arg).includes('validateDOMNesting')))).toBe(false)
        } finally {
            errors.mockRestore()
        }
    })

    it('uses affiliate-scoped reads, displays server currency/amount, and submits approval only after confirmation', async () => {
        await open()
        expect(await screen.findByText('EGP 150.00')).toBeVisible()
        expect(mocks.withdrawals).toHaveBeenCalledWith('disposable-affiliate', '', 1, 20)
        fireEvent.click(screen.getByRole('button', { name: action('approve') }))
        expect(mocks.approve).not.toHaveBeenCalled()
        fireEvent.click(screen.getAllByRole('button', { name: action('approve') }).slice(-1)[0])
        await waitFor(() => expect(mocks.approve).toHaveBeenCalledExactlyOnceWith('test-request', ''))
        expect(mocks.paid).not.toHaveBeenCalled()
        expect(screen.queryByText('EGP 1400.00')).not.toBeInTheDocument()
    })

    it('retains the stale approval error returned by authority instead of fabricating success', async () => {
        mocks.approve.mockRejectedValue({ response: { status: 409, data: { message: 'INSUFFICIENT_ELIGIBLE_FUNDS' } } })
        await open()
        fireEvent.click(await screen.findByRole('button', { name: action('approve') }))
        fireEvent.click(screen.getAllByRole('button', { name: action('approve') }).slice(-1)[0])
        const alert = await screen.findByRole('alert')
        expect(alert).toHaveTextContent('INSUFFICIENT_ELIGIBLE_FUNDS')
        fireEvent.click(screen.getByText('adminAffiliateDetail.workflow.supportDetails'))
        expect(screen.getByText('INSUFFICIENT_ELIGIBLE_FUNDS', { selector: 'code' })).toBeVisible()
        expect(screen.queryByText('adminAffiliateDetail.withdrawals.toast.approved')).not.toBeInTheDocument()
    })

    it('rejects through the existing confirmation action and refetches persisted status', async () => {
        await open()
        fireEvent.click(await screen.findByRole('button', { name: action('reject') }))
        mocks.withdrawals.mockResolvedValue({ items: [{ id: 'test-request', requestedAmount: 150, currency: 'EGP', status: 'Rejected' }] })
        fireEvent.click(screen.getAllByRole('button', { name: action('reject') }).slice(-1)[0])
        await waitFor(() => expect(mocks.reject).toHaveBeenCalledExactlyOnceWith('test-request', ''))
        expect(await screen.findByText('adminAffiliateDetail.statuses.rejected', { selector: 'span' })).toBeVisible()
        expect(screen.queryByRole('button', { name: action('approve') })).not.toBeInTheDocument()
        expect(screen.queryByRole('button', { name: action('reject') })).not.toBeInTheDocument()
    })

    it.each(['Rejected', 'Paid'])('offers no approval/rejection retry for terminal %s', async status => {
        mocks.withdrawals.mockResolvedValue({ items: [{ id: 'test-request', requestedAmount: 150, currency: 'EGP', status }] })
        await open()
        await screen.findByText('EGP 150.00')
        expect(screen.queryByRole('button', { name: action('approve') })).not.toBeInTheDocument()
        expect(screen.queryByRole('button', { name: action('reject') })).not.toBeInTheDocument()
        expect(mocks.approve).not.toHaveBeenCalled()
        expect(mocks.reject).not.toHaveBeenCalled()
    })
})
