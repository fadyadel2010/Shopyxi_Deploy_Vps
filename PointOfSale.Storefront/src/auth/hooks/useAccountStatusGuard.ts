/**
 * Account Status Guard Hook
 * Phase F2: Storefront Account Area
 * 
 * Guards against deactivated accounts by checking status
 * and forcing logout when necessary.
 * 
 * SECURITY:
 * - Checks account status on mount and periodically
 * - Forces logout if account is deactivated
 * - Clears auth state and redirects to login
 */

import { useCallback, useEffect, useRef } from 'react';
import { useNavigate } from 'react-router-dom';
import { useTranslation } from '@/i18n';
import { useAuthStore } from '../store/authStore';
import { useAccount } from './useAccount';

/** Stable translation key for the account-deactivation guard message. */
export const ACCOUNT_DEACTIVATED_MESSAGE_KEY = 'auth.errors.ACCOUNT_DEACTIVATED';

interface UseAccountStatusGuardOptions {
    // Check interval in milliseconds (default: 60000 = 1 minute)
    checkInterval?: number;
    // Whether to enable periodic checks (default: true)
    enablePeriodicCheck?: boolean;
}

/**
 * Hook that guards against deactivated accounts.
 * Use this on account-related pages to detect admin deactivation.
 */
export function useAccountStatusGuard(options: UseAccountStatusGuardOptions = {}) {
    const {
        checkInterval = 60000, // Default: check every minute
        enablePeriodicCheck = true,
    } = options;

    const navigate = useNavigate();
    const { t } = useTranslation();
    const { status, logout } = useAuthStore();
    const { loadAccountStatus, accountStatus } = useAccount();
    const checkingRef = useRef(false);

    // Check account status and handle deactivation
    const checkStatus = useCallback(async () => {
        if (status !== 'authenticated' || checkingRef.current) return;

        checkingRef.current = true;
        try {
            await loadAccountStatus();
        } finally {
            checkingRef.current = false;
        }
    }, [status, loadAccountStatus]);

    // Handle deactivation detection
    useEffect(() => {
        if (accountStatus && !accountStatus.isActive) {
            // Account has been deactivated by admin
            console.warn('[AccountStatusGuard] Account deactivated - forcing logout');

            // Force logout
            logout().then(() => {
                navigate('/login', {
                    state: {
                        message: t(ACCOUNT_DEACTIVATED_MESSAGE_KEY),
                        messageKey: ACCOUNT_DEACTIVATED_MESSAGE_KEY,
                        type: 'error'
                    }
                });
            });
        }
    }, [accountStatus, logout, navigate, t]);

    // Initial check on mount
    useEffect(() => {
        if (status === 'authenticated') {
            checkStatus();
        }
    }, [status, checkStatus]);

    // Periodic check
    useEffect(() => {
        if (!enablePeriodicCheck || status !== 'authenticated') return;

        const intervalId = setInterval(checkStatus, checkInterval);
        return () => clearInterval(intervalId);
    }, [enablePeriodicCheck, checkInterval, status, checkStatus]);

    return {
        isActive: accountStatus?.isActive ?? true,
        isChecking: checkingRef.current,
        accountStatus,
        checkNow: checkStatus,
    };
}

export default useAccountStatusGuard;
