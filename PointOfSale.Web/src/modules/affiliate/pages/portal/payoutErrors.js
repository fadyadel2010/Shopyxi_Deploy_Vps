// Only known stable codes are translated. Never render raw exception/transport text.
const keys = {
    AFFILIATE_PAYOUT_WALLET_REQUIRED: 'walletRequired',
    AFFILIATE_PAYOUT_WALLET_CHANGED_AFTER_REQUEST: 'walletChanged',
    AFFILIATE_PAYOUT_WALLET_SNAPSHOT_REQUIRED: 'walletChanged',
    AFFILIATE_PAYOUT_INSUFFICIENT_ELIGIBLE_FUNDS: 'balanceChanged',
    AFFILIATE_PAYOUT_STORE_REQUIRED: 'storeRequired',
    AFFILIATE_PAYOUT_ACCOUNT_INACTIVE: 'accountInactive',
    AFFILIATE_PAYOUT_BELOW_MINIMUM: 'belowMinimum',
    AFFILIATE_PAYOUT_INVALID_PRECISION: 'invalidPrecision',
    AFFILIATE_PAYOUT_REQUEST_STATUS_INVALID: 'stateChanged',
    AFFILIATE_CONCURRENCY_CONFLICT: 'stateChanged',
}

export function payoutErrorFeedback(error) {
    const code = error?.response?.data?.code
    return {
        type: 'error',
        key: `ux.payoutErrors.${keys[code] || 'unknown'}`,
        code: typeof code === 'string' && /^AFFILIATE_[A-Z_]{1,80}$/.test(code) ? code : null,
        walletRequired: code === 'AFFILIATE_PAYOUT_WALLET_REQUIRED',
    }
}
