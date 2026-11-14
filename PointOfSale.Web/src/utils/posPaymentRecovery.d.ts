export interface PendingPosPaymentRequest {
    key: string;
    fingerprint: string;
    payload: Record<string, unknown>;
    preparedPosCartId: string | null;
    paymentAttemptId: string;
    editingSaleId: string | null;
}

export interface PendingPosPaymentSubmission {
    cartFingerprint: string;
    createdAt: number;
    expiresAt: number;
    request: PendingPosPaymentRequest;
}

export const POS_PAYMENT_RECOVERY_MAX_AGE_MS: number;

export function persistPendingPosPayment(options: {
    request: PendingPosPaymentRequest;
    cartFingerprint: string;
    now?: number;
}): boolean;

export function readPendingPosPayment(options?: {
    cartFingerprint?: string;
    now?: number;
}): {
    submission: PendingPosPaymentSubmission | null;
    rejectionReason: 'invalid' | 'expired' | 'cart-mismatch' | null;
};

export function clearPendingPosPayment(): void;
