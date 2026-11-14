/**
 * AF-12 PATCH 4: Affiliate Mode Banner
 * 
 * Displays a subtle, non-intrusive banner at the top of the storefront
 * when the user is browsing via an affiliate referral link.
 * 
 * This gives the user transparency that they're in affiliate mode
 * and that the products shown are curated by the affiliate.
 */

import React, { useSyncExternalStore } from 'react';
import { subscribeAffiliateRef, getConfirmedAffiliateRefCode, isAffiliateJourneyRetained } from '@/bootstrap/affiliateRefTracker';
import { useTranslation } from '@/i18n/useTranslation';

export const AffiliateBanner: React.FC = () => {
    const { t, direction } = useTranslation();
    const refCode = useSyncExternalStore(subscribeAffiliateRef, getConfirmedAffiliateRefCode, getConfirmedAffiliateRefCode);
    const retained = useSyncExternalStore(subscribeAffiliateRef, isAffiliateJourneyRetained, isAffiliateJourneyRetained);
    if (!refCode) return null;

    return (
        <div
            id="affiliate-mode-banner"
            dir={direction}
            style={{
                background: 'linear-gradient(135deg, #667eea 0%, #764ba2 100%)',
                color: '#ffffff',
                padding: '6px 16px',
                fontSize: '0.8rem',
                fontWeight: 500,
                textAlign: 'center',
                letterSpacing: '0.02em',
                display: 'flex',
                alignItems: 'center',
                justifyContent: 'center',
                gap: '6px',
            }}
        >
            <span style={{ fontSize: '0.9rem' }}>🔗</span>
            <span>
                {t(retained ? 'landing.activeJourneyReferralBanner' : 'landing.referralBanner')}
                {refCode && (
                    <bdi dir="ltr" style={{ marginInlineStart: '6px' }}>({refCode})</bdi>
                )}
            </span>
        </div>
    );
};
