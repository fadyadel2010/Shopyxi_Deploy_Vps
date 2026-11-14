import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { act, render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { I18nextProvider } from 'react-i18next'
import i18n from 'i18next'
import PaymentModal from './PaymentModal.jsx'
import { getMixedPaymentState, getSaleSubmissionErrorMessage, parseAmountInput } from './paymentSubmission'
import { SettingsProvider } from '../contexts/SettingsContext'
import { clearPendingPosPayment, readPendingPosPayment } from '../utils/posPaymentRecovery'

type SubmissionFailure = { message?: string; code?: string; response?: { data?: { message?: string } } }

const apiMock = vi.hoisted(() => ({
    get: vi.fn(),
    post: vi.fn(),
    put: vi.fn(),
}))

vi.mock('../services/api', () => ({
    default: apiMock,
    getSafeApiErrorMessage: (error: SubmissionFailure) => error.message || error.response?.data?.message || 'Unknown error',
    isRequestTimeout: (error: SubmissionFailure) => error.code === 'ECONNABORTED' || error.message?.includes('timeout'),
}))

// Initialize i18n for tests
i18n.init({
    lng: 'en',
    fallbackLng: 'en',
    resources: {
        en: {
            translation: {
                'paymentModal.title': 'Payment',
                'paymentModal.cashAmount': 'Cash Amount',
                'paymentModal.creditButton': 'Credit',
                'paymentModal.customerAccountCharge': 'Customer Account',
                'paymentModal.completeSale': 'Complete Sale',
                'paymentModal.confirmPayment': 'Confirm Payment',
                'paymentModal.confirmPaymentTitle': 'Confirm Payment',
                'paymentModal.confirmPaymentMessage': 'Please confirm the payment details',
                'paymentModal.totalAmount': 'Total Amount',
                'paymentModal.confirmCreditTitle': 'Confirm Credit Sale',
                'paymentModal.confirmCreditMessage': 'Review customer account charge',
                'paymentModal.validation.cashAmountTooLow': 'Cash amount is too low',
                'paymentModal.validation.creditCustomerRequired': 'Select a customer',
                'common.cancel': 'Cancel',
                'common.close': 'Close',
            },
        },
    },
})

beforeEach(() => {
    clearPendingPosPayment()
    // A failed assertion must not leak unconsumed one-shot responses into
    // another payment test and manufacture a successful first submission.
    apiMock.get.mockReset()
    apiMock.post.mockReset()
    apiMock.put.mockReset()
    apiMock.get.mockResolvedValue({
        data: {
            invoiceNumber: 'INV-001',
        },
    })
    apiMock.post.mockResolvedValue({
        data: {
            invoiceId: 'sale-123',
            invoiceNumber: 'INV-001',
        },
    })
    apiMock.put.mockResolvedValue({
        data: {
            invoiceId: 'sale-123',
            invoiceNumber: 'INV-001',
        },
    })
})

afterEach(() => {
    clearPendingPosPayment()
    vi.useRealTimers()
    vi.clearAllMocks()
})

// Test wrapper with required providers
const TestWrapper = ({ children }: { children: import('react').ReactNode }) => (
    <I18nextProvider i18n={i18n}>
        <SettingsProvider>
            {children}
        </SettingsProvider>
    </I18nextProvider>
)

describe('PaymentModal amount parsing', () => {
    it('rejects blank amount input instead of treating it as zero', () => {
        expect(parseAmountInput('')).toMatchObject({ isValid: false, value: null, reason: 'required' })
        expect(parseAmountInput('   ')).toMatchObject({ isValid: false, value: null, reason: 'required' })
        expect(parseAmountInput(null)).toMatchObject({ isValid: false, value: null, reason: 'required' })
    })

    it('rejects unsafe financial formats instead of coercing them', () => {
        expect(parseAmountInput('1e3')).toMatchObject({ isValid: false, value: null, reason: 'invalid_format' })
        expect(parseAmountInput('10.125')).toMatchObject({ isValid: false, value: null, reason: 'too_many_decimals' })
        expect(parseAmountInput('-1')).toMatchObject({ isValid: false, value: null, reason: 'negative' })
        expect(parseAmountInput('1,000')).toMatchObject({ isValid: false, value: null, reason: 'invalid_format' })
    })

    it('accepts finite numeric amount input', () => {
        expect(parseAmountInput('25.50')).toMatchObject({ isValid: true, value: 25.5 })
        expect(parseAmountInput(10)).toMatchObject({ isValid: true, value: 10 })
    })
})

describe('PaymentModal mixed payment state', () => {
    it('marks blank mixed cash amount invalid even though card portion can cover the total', () => {
        const state = getMixedPaymentState(100, '')

        expect(state.hasInvalidCashAmount).toBe(true)
        expect(state.normalizedCashAmount).toBe(0)
        expect(state.normalizedCardAmount).toBe(100)
        expect(state.totalPaid).toBe(100)
    })

    it('derives the card remainder from an explicit two-decimal mixed cash amount', () => {
        const state = getMixedPaymentState(100, '25.12')

        expect(state.hasInvalidCashAmount).toBe(false)
        expect(state.normalizedCashAmount).toBe(25.12)
        expect(state.normalizedCardAmount).toBe(74.88)
        expect(state.totalPaid).toBe(100)
        expect(state.shouldClampCashAmount).toBe(false)
    })

    it('rejects mixed cash amounts that silently collapse to a single payment method', () => {
        expect(getMixedPaymentState(100, '0').hasZeroCashAmount).toBe(true)
        expect(getMixedPaymentState(100, '100').hasCashAmountAtOrAboveTotal).toBe(true)
    })
})

describe('PaymentModal submission concurrency', () => {
    it('submits a customer credit sale with no immediate tender', async () => {
        const user = userEvent.setup()
        const preparedCartId = '11111111-1111-4111-8111-111111111111'

        render(
            <PaymentModal
                total={100}
                items={[{
                    productId: 'prod-1',
                    variantId: 'variant-1',
                    unitId: 'unit-1',
                    name: 'Test Product',
                    quantity: 1,
                    unitPrice: 100,
                    discountAmount: 0,
                    taxRate: 0,
                }]}
                customer={{ id: 'customer-1', name: 'Credit Customer' }}
                discountType="fixed"
                discountValue={0}
                warehouseId="warehouse-1"
                paymentAttemptId="credit-attempt-1"
                posCartId={preparedCartId}
                onRevalidateStock={vi.fn().mockResolvedValue(true)}
                onPreparePosCart={vi.fn().mockResolvedValue(preparedCartId)}
                onClose={vi.fn()}
                onComplete={vi.fn()}
                onError={vi.fn()}
                editingSaleId={null}
            />,
            { wrapper: TestWrapper },
        )

        await user.click(screen.getByRole('button', { name: 'Credit' }))
        expect(screen.queryByLabelText(/cash amount/i)).not.toBeInTheDocument()
        await user.click(screen.getByRole('button', { name: /complete sale/i }))
        expect(await screen.findByText('Confirm Credit Sale')).toBeInTheDocument()
        await user.click(screen.getByRole('button', { name: /confirm payment/i }))

        await waitFor(() => expect(apiMock.post).toHaveBeenCalled())
        expect(apiMock.post.mock.calls[0][1]).toEqual(expect.objectContaining({
            customerId: 'customer-1',
            paymentMethod: 'Credit',
            paidAmount: 0,
            cashAmount: 0,
            cardAmount: 0,
        }))
    })

    it('does not offer credit for a walk-in sale', () => {
        render(
            <PaymentModal
                total={100}
                items={[{
                    productId: 'prod-1',
                    name: 'Test Product',
                    quantity: 1,
                    unitPrice: 100,
                    discountAmount: 0,
                    taxRate: 0,
                }]}
                customer={null}
                discountType="fixed"
                discountValue={0}
                warehouseId="warehouse-1"
                paymentAttemptId="walk-in-attempt"
                onClose={vi.fn()}
                onComplete={vi.fn()}
                onError={vi.fn()}
            />,
            { wrapper: TestWrapper },
        )

        expect(screen.queryByRole('button', { name: 'Credit' })).not.toBeInTheDocument()
    })

    it('restores the draft invoice tender and representative', async () => {
        render(
            <PaymentModal
                total={100}
                items={[{
                    productId: 'prod-1',
                    variantId: null,
                    unitId: null,
                    name: 'Test Product',
                    quantity: 1,
                    unitPrice: 100,
                    discountAmount: 0,
                    taxRate: 0,
                }]}
                customer={null}
                discountType="fixed"
                discountValue={0}
                warehouseId="warehouse-1"
                paymentAttemptId="edit-attempt-restore"
                onRevalidateStock={vi.fn().mockResolvedValue(true)}
                onClose={vi.fn()}
                onComplete={vi.fn()}
                onError={vi.fn()}
                editingSaleId="sale-123"
                editingMutationVersion={4}
                initialPaymentMethod="Mixed"
                initialCashAmount={25}
                initialSalesRepresentative={{ id: 'rep-1', name: 'Sales Rep' }}
            />,
            { wrapper: TestWrapper },
        )

        expect(screen.getByDisplayValue('Sales Rep')).toBeInTheDocument()
        expect(screen.getByLabelText(/cash amount/i)).toHaveValue(25)
        expect(screen.getByRole('button', { name: 'paymentModal.mixedButton' })).toHaveClass('bg-blue-50')
    })

    it('uses a synchronous confirmation lock before stock revalidation starts', async () => {
        const user = userEvent.setup()
        let revalidateResolve!: (value: boolean) => void
        const mockRevalidateStock = vi.fn(() => new Promise<boolean>(resolve => {
            // Store resolve to control when promise completes
            revalidateResolve = resolve
        }))
        const mockOnComplete = vi.fn()
        const mockOnError = vi.fn()
        const mockOnClose = vi.fn()

        const items = [
            {
                productId: 'prod-1',
                variantId: null,
                unitId: null,
                name: 'Test Product',
                quantity: 1,
                unitPrice: 100,
                discountAmount: 0,
                taxRate: 0,
            },
        ]

        render(
            <PaymentModal
                total={100}
                items={items}
                customer={null}
                discountType="fixed"
                discountValue={0}
                warehouseId="warehouse-1"
                paymentAttemptId="attempt-1"
                onRevalidateStock={mockRevalidateStock}
                onClose={mockOnClose}
                onComplete={mockOnComplete}
                onError={mockOnError}
                editingSaleId={null}
            />,
            { wrapper: TestWrapper }
        )

        // Submit the payment to trigger confirmation modal
        const submitButton = screen.getByRole('button', { name: /complete sale/i })
        await user.click(submitButton)

        // Confirmation modal should appear
        const confirmButton = await screen.findByRole('button', { name: /confirm payment/i })
        expect(confirmButton).toBeInTheDocument()
        expect(confirmButton).toBeEnabled()

        // Click confirm - this should set confirmationLockRef.current = true BEFORE calling onRevalidateStock
        await user.click(confirmButton)

        // Verify stock revalidation was called (lock was set synchronously before this async call)
        await waitFor(() => {
            expect(mockRevalidateStock).toHaveBeenCalledTimes(1)
        })

        // While the promise is pending, the button should be disabled (lock is active)
        expect(confirmButton).toBeDisabled()

        // Resolve the stock revalidation
        revalidateResolve(true)

        // After completion, lock should be cleared
        await waitFor(() => {
            expect(mockRevalidateStock).toHaveBeenCalledTimes(1)
        })
    })

    it('uses the freshly prepared server cart id for the final sale request', async () => {
        const user = userEvent.setup()
        const preparedCartId = '11111111-1111-4111-8111-111111111111'
        const onPreparePosCart = vi.fn().mockResolvedValue(preparedCartId)

        render(
            <PaymentModal
                total={100}
                items={[{
                    productId: 'prod-1',
                    variantId: 'variant-1',
                    unitId: 'unit-1',
                    name: 'Test Product',
                    quantity: 1,
                    unitPrice: 100,
                    discountAmount: 0,
                    taxRate: 0,
                }]}
                customer={null}
                discountType="fixed"
                discountValue={0}
                warehouseId="warehouse-1"
                paymentAttemptId="attempt-1"
                posCartId="aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa"
                onRevalidateStock={vi.fn().mockResolvedValue(true)}
                onPreparePosCart={onPreparePosCart}
                onClose={vi.fn()}
                onComplete={vi.fn()}
                onError={vi.fn()}
                editingSaleId={null}
            />,
            { wrapper: TestWrapper },
        )

        await user.click(screen.getByRole('button', { name: /complete sale/i }))
        await user.click(await screen.findByRole('button', { name: /confirm payment/i }))

        await waitFor(() => expect(apiMock.post).toHaveBeenCalled())
        expect(onPreparePosCart).toHaveBeenCalledTimes(1)
        expect(apiMock.post.mock.calls[0][2].headers['X-Pos-Cart-Id']).toBe(preparedCartId)
    })

    it('emits completion once with the fetched receipt instead of unmounting before it arrives', async () => {
        const user = userEvent.setup()
        const receipt = { invoiceNumber: 'INV-001', total: 100 }
        const onComplete = vi.fn()
        apiMock.get.mockResolvedValueOnce({ data: receipt })

        render(
            <PaymentModal
                total={100}
                items={[{
                    productId: 'prod-1',
                    variantId: null,
                    unitId: null,
                    name: 'Test Product',
                    quantity: 1,
                    unitPrice: 100,
                    discountAmount: 0,
                    taxRate: 0,
                }]}
                customer={null}
                discountType="fixed"
                discountValue={0}
                warehouseId="warehouse-1"
                paymentAttemptId="attempt-1"
                posCartId="11111111-1111-4111-8111-111111111111"
                onRevalidateStock={vi.fn().mockResolvedValue(true)}
                onPreparePosCart={vi.fn().mockResolvedValue('11111111-1111-4111-8111-111111111111')}
                onClose={vi.fn()}
                onComplete={onComplete}
                onError={vi.fn()}
                editingSaleId={null}
            />,
            { wrapper: TestWrapper },
        )

        await user.click(screen.getByRole('button', { name: /complete sale/i }))
        await user.click(await screen.findByRole('button', { name: /confirm payment/i }))

        await waitFor(() => expect(onComplete).toHaveBeenCalledTimes(1))
        expect(onComplete).toHaveBeenCalledWith(expect.objectContaining({ receipt }))
    })

    it.each([
        { kind: 'timeout', error: { code: 'ECONNABORTED', message: 'Request timeout' } },
        { kind: 'HTTP 408', error: { response: { status: 408, data: { message: 'Request timeout' } } } },
        { kind: 'post-commit HTTP 500', error: { response: { status: 500, data: { message: 'Unknown outcome' } } } },
        { kind: 'upstream HTTP 502', error: { response: { status: 502, data: { message: 'Bad gateway' } } } },
        { kind: 'upstream HTTP 503', error: { response: { status: 503, data: { message: 'Service unavailable' } } } },
        { kind: 'upstream HTTP 504', error: { response: { status: 504, data: { message: 'Gateway timeout' } } } },
        { kind: 'unknown network failure', error: { code: 'ERR_NETWORK', message: 'Network Error' } },
        { kind: 'missing-response failure', error: new Error('Connection closed before a response') },
        {
            kind: 'active submission conflict',
            error: { response: { status: 409, data: { code: 'SALE_IN_PROGRESS', message: 'Sale is processing.' } } },
        },
    ])('retries $kind with the frozen request without stock revalidation or cart recreation', async ({ error }) => {
        const user = userEvent.setup()
        const preparedCartId = '11111111-1111-4111-8111-111111111111'
        const recreatedCartId = '22222222-2222-4222-8222-222222222222'
        // The first request may have sold the last unit and completed the original cart.
        const onRevalidateStock = vi.fn().mockResolvedValueOnce(true).mockResolvedValue(false)
        const onPreparePosCart = vi.fn().mockResolvedValueOnce(preparedCartId).mockResolvedValue(recreatedCartId)
        const onComplete = vi.fn()
        const onError = vi.fn()
        const onClose = vi.fn()
        const props = {
            total: 100,
            items: [{
                productId: 'prod-1',
                variantId: 'variant-1',
                unitId: 'unit-1',
                name: 'Test Product',
                quantity: 1,
                unitPrice: 100,
                discountAmount: 0,
                taxRate: 0,
            }],
            customer: { id: 'customer-1', name: 'Original Customer' },
            discountType: 'fixed',
            discountValue: 0,
            warehouseId: 'warehouse-1',
            paymentAttemptId: 'attempt-1',
            posCartId: 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa',
            onRevalidateStock,
            onPreparePosCart,
            onClose,
            onComplete,
            onError,
            editingSaleId: null,
        }

        let rejectSubmission!: (error: unknown) => void
        let resolveRetry!: (response: { data: { invoiceId: string; invoiceNumber: string } }) => void
        apiMock.post
            .mockReturnValueOnce(new Promise((_, reject) => { rejectSubmission = reject }))
            .mockReturnValueOnce(new Promise(resolve => { resolveRetry = resolve }))

        const { rerender } = render(<PaymentModal {...props} />, { wrapper: TestWrapper })
        await user.click(screen.getByRole('button', { name: /complete sale/i }))
        await user.click(await screen.findByRole('button', { name: /confirm payment/i }))
        await waitFor(() => expect(apiMock.post).toHaveBeenCalledTimes(1))

        const firstRequest = apiMock.post.mock.calls[0]
        expect(firstRequest[2].headers).toEqual(expect.objectContaining({
            'X-Pos-Cart-Id': preparedCartId,
            'X-Payment-Attempt-Id': 'attempt-1',
            'Idempotency-Key': expect.any(String),
        }))
        expect(firstRequest[2].headers['Idempotency-Key']).toBeTruthy()

        await act(async () => { rejectSubmission(error) })
        await waitFor(() => expect(onError).toHaveBeenCalledTimes(1))
        const unknownOutcomeMessage = 'Payment outcome is unknown. Check sales history, then confirm to retry the exact same request. Do not start another payment.'
        expect(onError).toHaveBeenCalledWith(unknownOutcomeMessage)
        expect(screen.getByRole('alert')).toHaveTextContent(unknownOutcomeMessage)
        expect(screen.getByRole('button', { name: /confirm payment/i })).toBeEnabled()
        expect(screen.queryByLabelText(/close/i)).not.toBeInTheDocument()
        for (const button of screen.getAllByRole('button', { name: /cancel/i })) {
            expect(button).toBeDisabled()
            await user.click(button)
        }
        await user.keyboard('{Escape}')
        expect(onClose).not.toHaveBeenCalled()
        expect(screen.getByRole('dialog')).toBeInTheDocument()

        // Live cart/attempt/price refreshes cannot replace an unresolved request's authority.
        rerender(<PaymentModal
            {...props}
            total={125}
            items={[{ ...props.items[0], quantity: 2, unitPrice: 65 }]}
            customer={{ id: 'customer-2', name: 'Changed Customer' }}
            warehouseId="warehouse-2"
            discountValue={5}
            paymentAttemptId="attempt-2"
            posCartId={recreatedCartId}
        />)
        const retryButton = screen.getByRole('button', { name: /confirm payment/i })
        await user.dblClick(retryButton)

        await waitFor(() => expect(apiMock.post).toHaveBeenCalledTimes(2))
        expect(retryButton).toBeDisabled()
        expect(onRevalidateStock).toHaveBeenCalledTimes(1)
        expect(onPreparePosCart).toHaveBeenCalledTimes(1)
        expect(apiMock.post.mock.calls[1]).toEqual(firstRequest)
        expect(apiMock.post.mock.calls[1][1]).toEqual(expect.objectContaining({
            customerId: 'customer-1',
            warehouseId: 'warehouse-1',
            paidAmount: 100,
            discountAmount: 0,
            items: [expect.objectContaining({ quantity: 1, unitPrice: 100 })],
        }))
        expect(onComplete).not.toHaveBeenCalled()
        await user.keyboard('{Escape}')
        expect(onClose).not.toHaveBeenCalled()
        expect(screen.getByRole('dialog')).toBeInTheDocument()

        await act(async () => {
            resolveRetry({ data: { invoiceId: 'sale-123', invoiceNumber: 'INV-001' } })
        })
        await waitFor(() => expect(onComplete).toHaveBeenCalledTimes(1))
        expect(onComplete).toHaveBeenCalledWith(expect.objectContaining({
            invoiceId: 'sale-123',
            attemptId: 'attempt-1',
        }))
        expect(apiMock.post).toHaveBeenCalledTimes(2)
        expect(onRevalidateStock).toHaveBeenCalledTimes(1)
        expect(onPreparePosCart).toHaveBeenCalledTimes(1)
    })

    it('restores an unresolved request after remount and waits for explicit same-authority retry', async () => {
        const user = userEvent.setup()
        const preparedCartId = '11111111-1111-4111-8111-111111111111'
        const cartFingerprint = '{"cart":"payment-recovery"}'
        const props = {
            total: 100,
            items: [{
                productId: 'prod-1',
                variantId: 'variant-1',
                unitId: 'unit-1',
                name: 'Test Product',
                quantity: 1,
                unitPrice: 100,
                discountAmount: 0,
                taxRate: 0,
            }],
            customer: null,
            discountType: 'fixed',
            discountValue: 0,
            warehouseId: 'warehouse-1',
            paymentAttemptId: 'reload-attempt-1',
            posCartId: preparedCartId,
            recoveryContextFingerprint: cartFingerprint,
            onRevalidateStock: vi.fn().mockResolvedValue(true),
            onPreparePosCart: vi.fn().mockResolvedValue(preparedCartId),
            onClose: vi.fn(),
            onComplete: vi.fn(),
            onError: vi.fn(),
            editingSaleId: null,
        }
        apiMock.post
            .mockRejectedValueOnce({ code: 'ERR_NETWORK', message: 'Network Error' })
            .mockResolvedValueOnce({ data: { invoiceId: 'sale-123', invoiceNumber: 'INV-001' } })

        const firstMount = render(<PaymentModal {...props} />, { wrapper: TestWrapper })
        await user.click(screen.getByRole('button', { name: /complete sale/i }))
        await user.click(await screen.findByRole('button', { name: /confirm payment/i }))
        await waitFor(() => expect(props.onError).toHaveBeenCalledTimes(1))

        const firstRequest = apiMock.post.mock.calls[0]
        const recovery = readPendingPosPayment({ cartFingerprint })
        expect(recovery.submission?.request).toEqual(expect.objectContaining({
            key: firstRequest[2].headers['Idempotency-Key'],
            paymentAttemptId: 'reload-attempt-1',
            preparedPosCartId: preparedCartId,
        }))
        firstMount.unmount()

        const recoveredOnComplete = vi.fn()
        const recoveredRevalidation = vi.fn().mockResolvedValue(false)
        const recoveredCartPreparation = vi.fn().mockResolvedValue('different-cart')
        render(
            <PaymentModal
                {...props}
                recoveredSubmission={recovery.submission?.request}
                onComplete={recoveredOnComplete}
                onRevalidateStock={recoveredRevalidation}
                onPreparePosCart={recoveredCartPreparation}
            />,
            { wrapper: TestWrapper },
        )

        expect(screen.getByRole('dialog')).toBeInTheDocument()
        expect(apiMock.post).toHaveBeenCalledTimes(1)
        expect(recoveredOnComplete).not.toHaveBeenCalled()

        await user.click(screen.getByRole('button', { name: /confirm payment/i }))
        await waitFor(() => expect(apiMock.post).toHaveBeenCalledTimes(2))
        expect(apiMock.post.mock.calls[1]).toEqual(firstRequest)
        expect(recoveredRevalidation).not.toHaveBeenCalled()
        expect(recoveredCartPreparation).not.toHaveBeenCalled()
        await waitFor(() => expect(recoveredOnComplete).toHaveBeenCalledWith(expect.objectContaining({
            invoiceId: 'sale-123',
            attemptId: 'reload-attempt-1',
        })))
        expect(readPendingPosPayment({ cartFingerprint })).toEqual({
            submission: null,
            rejectionReason: null,
        })
    })

    it('keeps active edit conflicts in the confirmation dialog and retries with the same idempotency key', async () => {
        const user = userEvent.setup()
        const mockRevalidateStock = vi.fn(() => Promise.resolve(true))
        const mockOnComplete = vi.fn()
        const mockOnError = vi.fn()
        const mockOnClose = vi.fn()

        const items = [
            {
                productId: 'prod-1',
                variantId: null,
                unitId: null,
                name: 'Test Product',
                quantity: 1,
                unitPrice: 100,
                discountAmount: 0,
                taxRate: 0,
            },
        ]

        apiMock.put
            .mockRejectedValueOnce({
                response: {
                    status: 409,
                    data: {
                        message: 'A sale request with the same payment attempt id is already being processed.',
                    },
                },
            })
            .mockResolvedValueOnce({
                data: {
                    invoiceId: 'sale-123',
                    invoiceNumber: 'INV-001',
                },
            })

        render(
            <PaymentModal
                total={100}
                items={items}
                customer={null}
                discountType="fixed"
                discountValue={0}
                warehouseId="warehouse-1"
                paymentAttemptId="edit-attempt-1"
                onRevalidateStock={mockRevalidateStock}
                onClose={mockOnClose}
                onComplete={mockOnComplete}
                onError={mockOnError}
                editingSaleId="sale-123"
                editingMutationVersion={7}
            />,
            { wrapper: TestWrapper }
        )

        await user.click(screen.getByRole('button', { name: /complete sale/i }))
        const confirmButton = await screen.findByRole('button', { name: /confirm payment/i })
        await user.click(confirmButton)

        await waitFor(() => {
            expect(mockOnError).toHaveBeenCalledWith(
                'Payment outcome is unknown. Check sales history, then confirm to retry the exact same request. Do not start another payment.'
            )
        })

        expect(screen.getByRole('button', { name: /confirm payment/i })).toBeInTheDocument()
        const firstHeaders = apiMock.put.mock.calls[0][2].headers

        await user.click(screen.getByRole('button', { name: /confirm payment/i }))

        await waitFor(() => {
            expect(mockOnComplete).toHaveBeenCalledWith(
                expect.objectContaining({
                    invoiceId: 'sale-123',
                    attemptId: 'edit-attempt-1',
                })
            )
        })

        const retryHeaders = apiMock.put.mock.calls[1][2].headers
        expect(retryHeaders['Idempotency-Key']).toBe(firstHeaders['Idempotency-Key'])
        expect(retryHeaders['X-Request-Id']).toBe(firstHeaders['X-Request-Id'])
        expect(retryHeaders['X-Payment-Attempt-Id']).toBe('edit-attempt-1')
        expect(apiMock.put.mock.calls[1][1]).toEqual(expect.objectContaining({
            expectedMutationVersion: 7,
        }))
    })
})

describe('PaymentModal sale submission errors', () => {
    const t = (key: string, fallback?: string) => fallback ?? key

    it('warns cashiers that a timed-out payment attempt is ambiguous before retrying', () => {
        const message = getSaleSubmissionErrorMessage({ code: 'ECONNABORTED' }, t)

        expect(message).toBe('Payment request timed out. Check sales history before trying again.')
        expect(message).toContain('Check sales history')
    })

    it('keeps server validation messages for rejected sale submissions', () => {
        const message = getSaleSubmissionErrorMessage({
            response: {
                status: 409,
                data: {
                    message: 'Insufficient stock',
                },
            },
        }, t)

        expect(message).toBe('Insufficient stock')
    })

    it('keeps normalized active-submission conflicts eligible for same-key retry', async () => {
        const user = userEvent.setup()
        apiMock.put.mockRejectedValueOnce({
            response: {
                status: 409,
                data: {
                    code: 'SALE_IN_PROGRESS',
                    message: 'The same sale update is already being processed.',
                },
            },
        }).mockResolvedValueOnce({
            data: { invoiceId: 'sale-123', invoiceNumber: 'INV-001' },
        })

        render(
            <PaymentModal
                total={100}
                items={[{
                    productId: 'prod-1',
                    variantId: null,
                    unitId: null,
                    name: 'Test Product',
                    quantity: 1,
                    unitPrice: 100,
                    discountAmount: 0,
                    taxRate: 0,
                }]}
                customer={null}
                discountType="fixed"
                discountValue={0}
                warehouseId="warehouse-1"
                paymentAttemptId="edit-attempt-conflict"
                onRevalidateStock={vi.fn().mockResolvedValue(true)}
                onClose={vi.fn()}
                onComplete={vi.fn()}
                onError={vi.fn()}
                editingSaleId="sale-123"
                editingMutationVersion={7}
            />,
            { wrapper: TestWrapper },
        )

        await user.click(screen.getByRole('button', { name: /complete sale/i }))
        await user.click(await screen.findByRole('button', { name: /confirm payment/i }))
        await waitFor(() => expect(apiMock.put).toHaveBeenCalledTimes(1))

        const firstKey = apiMock.put.mock.calls[0][2].headers['Idempotency-Key']
        await user.click(screen.getByRole('button', { name: /confirm payment/i }))
        await waitFor(() => expect(apiMock.put).toHaveBeenCalledTimes(2))

        expect(apiMock.put.mock.calls[1][2].headers['Idempotency-Key']).toBe(firstKey)
    })
})

describe('PaymentModal accessibility guard', () => {
    it('links amount validation feedback to the amount input', async () => {
        const user = userEvent.setup()
        const mockRevalidateStock = vi.fn(() => Promise.resolve(true))
        const mockOnComplete = vi.fn()
        const mockOnError = vi.fn()
        const mockOnClose = vi.fn()

        const items = [
            {
                productId: 'prod-1',
                variantId: null,
                unitId: null,
                name: 'Test Product',
                quantity: 1,
                unitPrice: 100,
                discountAmount: 0,
                taxRate: 0,
            },
        ]

        render(
            <PaymentModal
                total={100}
                items={items}
                customer={null}
                discountType="fixed"
                discountValue={0}
                warehouseId="warehouse-1"
                paymentAttemptId="attempt-1"
                onRevalidateStock={mockRevalidateStock}
                onClose={mockOnClose}
                onComplete={mockOnComplete}
                onError={mockOnError}
                editingSaleId={null}
            />,
            { wrapper: TestWrapper }
        )

        // Find the cash amount input by its label
        const cashAmountInput = screen.getByLabelText(/cash amount/i)
        expect(cashAmountInput).toBeInTheDocument()
        expect(cashAmountInput).toHaveAttribute('id', 'payment-cash-amount')

        // Verify the label element has the correct htmlFor attribute
        const labelElement = screen.getByText(/cash amount/i).closest('label')
        expect(labelElement).toHaveAttribute('for', 'payment-cash-amount')

        // Clear the cash amount to trigger validation error
        await user.clear(cashAmountInput)
        
        // Try to submit with invalid amount
        const submitButton = screen.getByRole('button', { name: /complete sale/i })
        await user.click(submitButton)

        // Validation message should appear with proper ARIA role
        const validationMessage = await screen.findByRole('alert')
        expect(validationMessage).toBeInTheDocument()
        expect(validationMessage).toHaveAttribute('id', 'payment-validation-message')

        // Input should have aria-invalid set to true
        expect(cashAmountInput).toHaveAttribute('aria-invalid', 'true')
        
        // Input should have aria-describedby pointing to the validation message
        expect(cashAmountInput).toHaveAttribute('aria-describedby', 'payment-validation-message')
    })
})
