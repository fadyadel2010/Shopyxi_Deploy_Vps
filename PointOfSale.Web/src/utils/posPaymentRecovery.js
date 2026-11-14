import { getScopedStorageKey } from './storageScope'
import { stableStringify } from './requestSafety'

const POS_PAYMENT_RECOVERY_PREFIX = 'pos_payment_recovery_v1'
const POS_PAYMENT_RECOVERY_SCHEMA_VERSION = 1
const POS_PAYMENT_RECOVERY_MAX_STORAGE_SIZE = 256 * 1024
export const POS_PAYMENT_RECOVERY_MAX_AGE_MS = 30 * 60 * 1000

const isPlainObject = (value) => value != null && typeof value === 'object' && !Array.isArray(value)
const isNonEmptyString = (value) => typeof value === 'string' && value.length > 0
const isOptionalString = (value) => value == null || typeof value === 'string'
const getRecoveryStorageKey = () => getScopedStorageKey(POS_PAYMENT_RECOVERY_PREFIX, 'active')

const getSessionStorage = () => {
    try {
        return typeof sessionStorage === 'undefined' ? null : sessionStorage
    } catch {
        return null
    }
}

const removeStoredRecovery = (storage) => {
    try {
        storage?.removeItem(getRecoveryStorageKey())
    } catch (error) {
        console.warn('Failed to clear POS payment recovery authority:', error)
    }
}

const isValidRequest = (request) => {
    if (
        !isPlainObject(request)
        || !isNonEmptyString(request.key)
        || !isNonEmptyString(request.fingerprint)
        || !isNonEmptyString(request.paymentAttemptId)
        || !isOptionalString(request.preparedPosCartId)
        || !isOptionalString(request.editingSaleId)
        || !isPlainObject(request.payload)
    ) {
        return false
    }

    return stableStringify(request.payload) === request.fingerprint
}

const parseStoredRecovery = (rawValue, now) => {
    let parsed
    try {
        parsed = JSON.parse(rawValue)
    } catch {
        return { submission: null, rejectionReason: 'invalid' }
    }

    if (
        !isPlainObject(parsed)
        || parsed.version !== POS_PAYMENT_RECOVERY_SCHEMA_VERSION
        || !Number.isFinite(parsed.createdAt)
        || !Number.isFinite(parsed.expiresAt)
        || !isNonEmptyString(parsed.cartFingerprint)
        || !isValidRequest(parsed.request)
        || parsed.createdAt > now + 60_000
        || parsed.expiresAt <= parsed.createdAt
        || parsed.expiresAt - parsed.createdAt > POS_PAYMENT_RECOVERY_MAX_AGE_MS
    ) {
        return { submission: null, rejectionReason: 'invalid' }
    }

    if (now >= parsed.expiresAt) {
        return { submission: null, rejectionReason: 'expired' }
    }

    return {
        submission: {
            cartFingerprint: parsed.cartFingerprint,
            createdAt: parsed.createdAt,
            expiresAt: parsed.expiresAt,
            request: parsed.request,
        },
        rejectionReason: null,
    }
}

export const persistPendingPosPayment = ({ request, cartFingerprint, now = Date.now() }) => {
    const storage = getSessionStorage()
    if (!storage || !isValidRequest(request) || !isNonEmptyString(cartFingerprint) || !Number.isFinite(now)) {
        return false
    }

    const envelope = {
        version: POS_PAYMENT_RECOVERY_SCHEMA_VERSION,
        createdAt: now,
        expiresAt: now + POS_PAYMENT_RECOVERY_MAX_AGE_MS,
        cartFingerprint,
        request,
    }
    const serialized = JSON.stringify(envelope)
    if (serialized.length > POS_PAYMENT_RECOVERY_MAX_STORAGE_SIZE) {
        return false
    }

    try {
        storage.setItem(getRecoveryStorageKey(), serialized)
        return storage.getItem(getRecoveryStorageKey()) === serialized
    } catch (error) {
        console.error('Failed to persist POS payment recovery authority:', error)
        return false
    }
}

export const readPendingPosPayment = ({ cartFingerprint, now = Date.now() } = {}) => {
    const storage = getSessionStorage()
    if (!storage) {
        return { submission: null, rejectionReason: null }
    }

    let rawValue
    try {
        rawValue = storage.getItem(getRecoveryStorageKey())
    } catch (error) {
        console.warn('Failed to read POS payment recovery authority:', error)
        return { submission: null, rejectionReason: null }
    }

    if (!rawValue) {
        return { submission: null, rejectionReason: null }
    }

    const result = parseStoredRecovery(rawValue, now)
    if (!result.submission) {
        removeStoredRecovery(storage)
        return result
    }

    if (!isNonEmptyString(cartFingerprint) || result.submission.cartFingerprint !== cartFingerprint) {
        removeStoredRecovery(storage)
        return { submission: null, rejectionReason: 'cart-mismatch' }
    }

    return result
}

export const clearPendingPosPayment = () => {
    removeStoredRecovery(getSessionStorage())
}
