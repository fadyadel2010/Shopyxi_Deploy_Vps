import { renderToStaticMarkup } from 'react-dom/server';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { t } from './languageStore';
import { ACCOUNT_DEACTIVATED_MESSAGE_KEY } from '@/auth/hooks/useAccountStatusGuard';
import { AuthErrorRenderer } from '@/auth/components/AuthErrorRenderer';
import { ProtectedRoute } from '@/core/guards/ProtectedRoute';

const i18nState = vi.hoisted(() => ({ language: 'en' as 'en' | 'ar' }));

vi.mock('@/i18n', async () => {
    const { t: translate } = await import('@/i18n/languageStore');
    return {
        useTranslation: () => ({
            t: (key: string, params?: Record<string, string | number>) =>
                translate(key, params, i18nState.language),
            language: i18nState.language,
            direction: i18nState.language === 'ar' ? 'rtl' : 'ltr',
        }),
    };
});

vi.mock('@/auth/hooks/useAuthGuard', () => ({
    useAuthGuard: () => ({
        isLoading: false,
        isAuthenticated: false,
        isUnauthenticated: false,
        isDegraded: true,
        status: 'degraded',
        showContent: false,
    }),
}));

const setLanguage = (language: 'en' | 'ar') => {
    i18nState.language = language;
};

afterEach(() => setLanguage('en'));

/** User-visible storefront surfaces that previously resolved to raw keys. */
const correctedSurfaces = [
    'auth.guard.degradedTitle',
    'auth.guard.degradedMessage',
    'common.processing',
    'common.remove',
    'filters.noResults',
    'filters.tryDifferent',
    'search.suggestions',
    'chat.minimized',
    'checkout.paymentError',
    'checkout.contactSupport',
    'checkout.manualWalletUnavailable',
    'affiliate.modeActive',
    'affiliate.modeTooltip',
    'affiliate.storeBadge',
];

describe('Storefront guard and surface localization', () => {
    it.each(['en', 'ar'] as const)('resolves every corrected surface key in %s', language => {
        for (const key of correctedSurfaces) {
            expect(t(key, undefined, language), key).not.toBe(key);
        }
    });

    it('provides distinct Arabic and English text for every corrected surface key', () => {
        for (const key of correctedSurfaces) {
            expect(t(key, undefined, 'ar'), key).not.toBe(t(key, undefined, 'en'));
        }
    });

    it('translates the account-deactivation guard message in both languages', () => {
        expect(t(ACCOUNT_DEACTIVATED_MESSAGE_KEY, undefined, 'en')).toBe(
            'Your account has been deactivated. Please contact support.'
        );
        expect(t(ACCOUNT_DEACTIVATED_MESSAGE_KEY, undefined, 'ar')).not.toBe(
            ACCOUNT_DEACTIVATED_MESSAGE_KEY
        );
        expect(t(ACCOUNT_DEACTIVATED_MESSAGE_KEY, undefined, 'ar')).not.toBe(
            t(ACCOUNT_DEACTIVATED_MESSAGE_KEY, undefined, 'en')
        );
    });

    it('renders the degraded guard message in Arabic without exposing raw keys or protected content', () => {
        setLanguage('ar');
        const markup = renderToStaticMarkup(
            <ProtectedRoute>
                <div>protected content</div>
            </ProtectedRoute>
        );

        expect(markup).toContain(t('auth.guard.degradedTitle', undefined, 'ar'));
        expect(markup).toContain(t('auth.guard.degradedMessage', undefined, 'ar'));
        expect(markup).not.toContain('auth.guard.');
        expect(markup).not.toContain('protected content');
    });

    it('renders the degraded guard message in English and keeps protected content blocked', () => {
        setLanguage('en');
        const markup = renderToStaticMarkup(
            <ProtectedRoute>
                <div>protected content</div>
            </ProtectedRoute>
        );

        expect(markup).toContain('We are verifying your session');
        expect(markup).not.toContain('auth.guard.');
        expect(markup).not.toContain('protected content');
    });

    it('falls back to a translated generic error instead of leaking unknown codes or server messages', () => {
        const markup = renderToStaticMarkup(
            <AuthErrorRenderer
                error={{ code: 'INTERNAL_STACK_TRACE', message: 'NullReference at line 42' } as never}
            />
        );

        expect(markup).toContain(t('auth.errors.UNKNOWN_ERROR', undefined, 'en'));
        expect(markup).not.toContain('auth.errors.INTERNAL_STACK_TRACE');
        expect(markup).not.toContain('NullReference');
    });

    it('keeps known authorization codes translated', () => {
        const markup = renderToStaticMarkup(
            <AuthErrorRenderer error={{ code: 'ACCOUNT_DEACTIVATED', message: 'raw backend text' }} />
        );

        expect(markup).toContain(t('auth.errors.ACCOUNT_DEACTIVATED', undefined, 'en'));
        expect(markup).not.toContain('raw backend text');
    });
});
