import { renderToStaticMarkup } from 'react-dom/server';
import { afterEach, beforeEach, expect, it, vi } from 'vitest';
import en from '@/i18n/locales/en.json';
import ar from '@/i18n/locales/ar.json';
import { acceptAffiliateRefVerdict, clearAffiliateRef, initAffiliateRefTracker } from '@/bootstrap/affiliateRefTracker';
import { AffiliateBanner } from './AffiliateBanner';

const state = vi.hoisted(() => ({ language: 'en' }));
vi.mock('@/bootstrap/tenantScopedStorage', () => ({ tryGetTenantScopedStorageKey: () => null }));
vi.mock('@/i18n/useTranslation', () => ({ useTranslation: () => ({
    direction: state.language === 'ar' ? 'rtl' : 'ltr',
    t: (key: string) => (state.language === 'ar' ? ar : en).landing[key.split('.')[1] as keyof typeof en.landing],
}) }));

beforeEach(() => {
    clearAffiliateRef();
    vi.stubGlobal('window', { location: { search: '?ref=SECOND', pathname: '/products/test-product' } });
    initAffiliateRefTracker();
});
afterEach(() => vi.unstubAllGlobals());

it.each(['en', 'ar'])('shows the effective original journey rather than the new URL in %s', language => {
    state.language = language;
    acceptAffiliateRefVerdict('true', 'FIRST', null, { productEntry: true, journey: 'active' });
    const markup = renderToStaticMarkup(<AffiliateBanner />);
    expect(markup).toContain((language === 'ar' ? ar : en).landing.activeJourneyReferralBanner);
    expect(markup).toContain('(FIRST)');
    expect(markup).not.toContain('SECOND');
    expect(markup).toContain(`dir="${language === 'ar' ? 'rtl' : 'ltr'}"`);
});

it('does not claim an unverified or invalid referral is accepted', () => {
    expect(renderToStaticMarkup(<AffiliateBanner />)).toBe('');
    acceptAffiliateRefVerdict('false', undefined, null, { productEntry: true });
    expect(renderToStaticMarkup(<AffiliateBanner />)).toBe('');
});
