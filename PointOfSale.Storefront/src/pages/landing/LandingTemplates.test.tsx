import { renderToStaticMarkup } from 'react-dom/server';
import { MemoryRouter } from 'react-router-dom';
import { describe, expect, it, vi } from 'vitest';
import { getTemplate } from './index';
import type { LandingTemplateProps } from './TemplateRegistry';
import { useLanguageStore } from '@/i18n/languageStore';

// SSR has no client subscriptions: supply each request's language snapshot,
// retaining the real dictionaries and localized-field implementation.
vi.mock('@/i18n/useTranslation', async importOriginal => {
    const original = await importOriginal<typeof import('@/i18n/useTranslation')>();
    const { useLanguageStore: store, t } = await import('@/i18n/languageStore');
    return { ...original, useTranslation: () => {
        const state = store.getState();
        return { language: state.language, isRTL: state.isRTL(), direction: state.getDirection(), toggleLanguage: state.toggleLanguage, t: (key: string) => t(key, undefined, state.language) };
    } };
});

const props: LandingTemplateProps = {
    data: { templateType: 'Classic:v1', product: { productId: 'product', name: 'Canonical phone', nameAr: 'هاتف', description: 'Actual product copy.', descriptionAr: 'وصف المنتج', slug: 'phone', price: 25, currency: 'EGP', images: [], inStock: true, hasVariants: false, variants: [], hasActivePromotion: false, features: ['Existing feature'] }, tenant: { tenantId: 'store', storeName: 'AhramStore', currency: 'EGP' }, affiliate: { affiliateAccountId: 'affiliate', referralCode: 'REF123', displayName: 'Affiliate', isValid: true }, ctaConfig: { buttonText: 'Buy Now', singleProductMode: true, includeAffiliateAttribution: true, defaultQuantity: 1 } },
    selectedVariant: null, onVariantSelect: () => {}, activeImageIndex: 0, onImageSelect: () => {}, onBuyNow: () => {}, funnelLoading: false, funnelError: null, formatPrice: value => `EGP ${value.toFixed(2)}`, effectivePrice: 25, discountedPrice: null, displayPrice: 25, isInStock: true, isDark: false,
};
function render(template: string, overrides: Partial<LandingTemplateProps> = {}) {
    const Template = getTemplate(template)!;
    return renderToStaticMarkup(<MemoryRouter><Template {...props} {...overrides} /></MemoryRouter>);
}
describe('Functional, data-only public landing templates', () => {
    it.each(['en', 'ar'] as const)('uses three visibly distinct layout structures with canonical product data in %s', language => {
        useLanguageStore.getState().setLanguage(language);
        const classic = render('Classic:v1'); const story = render('Storytelling:v1'); const minimal = render('MinimalFastCheckout:v1');
        expect(classic).toContain('lp-product-classic'); expect(story).toContain('lp-editorial-intro'); expect(story).toContain('lp-editorial-story'); expect(minimal).toContain('lp-product-minimal');
        for (const html of [classic, story, minimal]) {
            expect(html).toContain('EGP 25.00'); expect(html).toContain(language === 'ar' ? 'هاتف' : 'Canonical phone');
            expect(html).toContain(language === 'ar' ? 'اشتر الآن' : 'Buy now'); expect(html).toContain(language === 'ar' ? 'dir="rtl"' : 'dir="ltr"');
            expect(html).toContain('Existing feature'); expect(html).not.toMatch(/commission|testimonials|30 days|Fast Delivery|Verified Store|Premium Quality/);
            expect(html).not.toContain('src=""');
        }
        expect(minimal).not.toContain('Actual product copy.');
    });
    it('handles missing copy/images, unknown stock and funnel errors without claims', () => {
        useLanguageStore.getState().setLanguage('en');
        const html = render('Classic:v1', { data: { ...props.data, product: { ...props.data.product, description: undefined, descriptionAr: undefined } }, isInStock: null, funnelError: 'raw backend error' });
        expect(html).toContain('Product image unavailable'); expect(html).toContain('No description has been provided'); expect(html).toContain('Availability not confirmed');
        expect(html).toContain('disabled=""'); expect(html).toContain('role="alert"'); expect(html).not.toContain('raw backend error');
    });
    it('falls back to Classic without creating another rendering implementation', () => {
        expect(getTemplate('unknown')).toBe(getTemplate('Classic:v1'));
    });
});
