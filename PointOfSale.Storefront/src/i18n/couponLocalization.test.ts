import { describe, expect, it } from 'vitest';
import { t } from './languageStore';

describe('Coupon qualification feedback localization', () => {
    it.each(['en', 'ar'] as const)('interpolates savings without literal braces and translates cart recovery in %s', language => {
        const amount = language === 'ar' ? '٢٫٥٠ ج.م.' : 'EGP 2.50';
        expect(t('cart.coupon.savedAmount', { amount }, language)).toContain(amount);
        expect(t('cart.coupon.savedAmount', { amount }, language)).not.toMatch(/[{}]/);
        expect(t('cart.retrySync', undefined, language)).not.toBe('cart.retrySync');
        expect(t('cart.dismissError', undefined, language)).not.toBe('cart.dismissError');
    });

    it.each(['COUPON_INVALID', 'COUPON_EXPIRED', 'COUPON_MINIMUM_NOT_MET', 'COUPON_MAX_USAGE_EXCEEDED',
        'COUPON_CUSTOMER_MAX_EXCEEDED', 'COUPON_AUTH_REQUIRED', 'COUPON_RESERVATION_EXISTS', 'COUPON_DISCOUNT_MISMATCH',
        'COUPON_INVALID_CONFIG', 'COUPON_CODE_MISMATCH', 'COUPON_NOT_FOUND'])('translates canonical API code %s in both locales', code => {
        const key = `cart.coupon.errors.${code}`;
        expect(t(key, undefined, 'en')).not.toBe(key);
        expect(t(key, undefined, 'ar')).not.toBe(key);
        expect(t(key, undefined, 'ar')).not.toBe(t(key, undefined, 'en'));
    });
});
