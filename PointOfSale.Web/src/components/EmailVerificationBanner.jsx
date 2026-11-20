import { useState, useEffect } from 'react'
import { TriangleAlert, X } from 'lucide-react'
import { useTranslation } from 'react-i18next'
import api from '../services/api'
import { IconButton } from '../design-system/primitives'

/**
 * Email Verification Banner Component
 * Phase 8.4: Email Verification & Account Trust
 * 
 * Shows a non-blocking banner for unverified users.
 * Can be dismissed but reappears on page refresh.
 */
export default function EmailVerificationBanner() {
    const { t } = useTranslation()
    const [status, setStatus] = useState(null) // null = loading, { emailVerified: boolean }
    const [dismissed, setDismissed] = useState(false)
    const [resending, setResending] = useState(false)
    const [resendMessage, setResendMessage] = useState('')

    useEffect(() => {
        checkVerificationStatus()
    }, [])

    const checkVerificationStatus = async () => {
        try {
            const response = await api.get('/auth/verification-status')
            setStatus(response.data)
        } catch {
            // If not authenticated or error, don't show banner
            setStatus({ emailVerified: true })
        }
    }

    const handleResend = async () => {
        setResending(true)
        setResendMessage('')

        try {
            const response = await api.post('/auth/resend-verification')
            setResendMessage(response.data.message || t('emailVerification.sent', 'Verification email sent!'))
        } catch (err) {
            const errorData = err.response?.data
            if (err.response?.status === 429) {
                setResendMessage(t('emailVerification.tooManyRequests', 'Too many requests. Please try again later.'))
            } else if (errorData?.error === 'ALREADY_VERIFIED') {
                setStatus({ emailVerified: true })
            } else {
                setResendMessage(errorData?.message || t('emailVerification.failed', 'Failed to send email. Please try again.'))
            }
        } finally {
            setResending(false)
        }
    }

    const handleDismiss = () => {
        setDismissed(true)
        document.getElementById('pos-main-content')?.focus({ preventScroll: true })
    }

    // Don't render if:
    // - Still loading
    // - Already verified
    // - User dismissed
    if (status === null || status?.emailVerified || dismissed) {
        return null
    }

    return (
        <div className="pos-banner">
            <TriangleAlert className="pos-banner__icon" size={20} aria-hidden="true" />
            <div className="pos-banner__content">
                <p className="pos-banner__title">
                    {t('emailVerification.title', 'Verify your email')}{' '}
                    <span className="pos-banner__message">
                        {t('emailVerification.message', 'to publish your online store.')}
                    </span>
                </p>
                {resendMessage ? (
                    <p className="pos-banner__status" role="status">{resendMessage}</p>
                ) : null}
                <div className="pos-banner__actions">
                    <button
                        type="button"
                        onClick={handleResend}
                        disabled={resending}
                        className="pos-banner__resend"
                    >
                        {resending ? t('emailVerification.sending', 'Sending...') : t('emailVerification.resend', 'Resend email')}
                    </button>
                </div>
            </div>
            <IconButton
                className="pos-banner__dismiss"
                label={t('emailVerification.dismiss', 'Dismiss')}
                icon={<X />}
                size="sm"
                onClick={handleDismiss}
            />
        </div>
    )
}
