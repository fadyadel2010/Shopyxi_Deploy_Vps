import { afterEach, describe, expect, it } from 'vitest'
import {
    POS_PAYMENT_RECOVERY_MAX_AGE_MS,
    clearPendingPosPayment,
    persistPendingPosPayment,
    readPendingPosPayment,
} from './posPaymentRecovery'
import { stableStringify } from './requestSafety'

const cartFingerprint = '{"cart":"original"}'
const payload = {
    warehouseId: 'warehouse-1',
    paymentMethod: 'Cash',
    paidAmount: 100,
    cashAmount: 100,
    cardAmount: 0,
    items: [{ productId: 'product-1', quantity: 1, unitPrice: 100 }],
}
const request = {
    key: 'pos-sale-create-1',
    fingerprint: stableStringify(payload)!,
    payload,
    preparedPosCartId: '11111111-1111-4111-8111-111111111111',
    paymentAttemptId: 'attempt-1',
    editingSaleId: null,
}

afterEach(() => {
    clearPendingPosPayment()
})

describe('POS payment recovery authority', () => {
    it('restores the exact request only for the matching cart within the bounded lifetime', () => {
        const now = 1_700_000_000_000

        expect(persistPendingPosPayment({ request, cartFingerprint, now })).toBe(true)
        expect(readPendingPosPayment({ cartFingerprint, now: now + 1_000 })).toEqual({
            submission: {
                cartFingerprint,
                createdAt: now,
                expiresAt: now + POS_PAYMENT_RECOVERY_MAX_AGE_MS,
                request,
            },
            rejectionReason: null,
        })
    })

    it('rejects and clears a request when the recovered cart fingerprint differs', () => {
        const now = 1_700_000_000_000
        expect(persistPendingPosPayment({ request, cartFingerprint, now })).toBe(true)

        expect(readPendingPosPayment({ cartFingerprint: 'different-cart', now })).toEqual({
            submission: null,
            rejectionReason: 'cart-mismatch',
        })
        expect(readPendingPosPayment({ cartFingerprint, now })).toEqual({
            submission: null,
            rejectionReason: null,
        })
    })

    it('rejects and clears expired recovery authority', () => {
        const now = 1_700_000_000_000
        expect(persistPendingPosPayment({ request, cartFingerprint, now })).toBe(true)

        expect(readPendingPosPayment({
            cartFingerprint,
            now: now + POS_PAYMENT_RECOVERY_MAX_AGE_MS,
        })).toEqual({
            submission: null,
            rejectionReason: 'expired',
        })
    })

    it('refuses internally inconsistent payload fingerprints before writing', () => {
        expect(persistPendingPosPayment({
            request: {
                ...request,
                payload: { ...payload, paidAmount: 200 },
            },
            cartFingerprint,
        })).toBe(false)
        expect(readPendingPosPayment({ cartFingerprint })).toEqual({
            submission: null,
            rejectionReason: null,
        })
    })
})
