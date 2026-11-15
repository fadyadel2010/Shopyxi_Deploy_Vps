import { render, screen } from '@testing-library/react'
import { describe, expect, it } from 'vitest'
import { MemoryRouter } from 'react-router-dom'
import { I18nextProvider } from 'react-i18next'
import { createInstance } from 'i18next'
import en from '../i18n/locales/en.json'
import ar from '../i18n/locales/ar.json'
import AffiliatePromotionReadiness from './AffiliatePromotionReadiness'

async function show(readiness: any, language = 'en') {
    const i18n = createInstance()
    await i18n.init({ lng: language, resources: { en: { translation: en }, ar: { translation: ar } } })
    render(<MemoryRouter><I18nextProvider i18n={i18n}><AffiliatePromotionReadiness readiness={readiness} productId="product-id" /></I18nextProvider></MemoryRouter>)
}

describe('authoritative Affiliate promotion readiness', () => {
    it.each(['en', 'ar'])('shows four independently evaluated sections in %s', async language => {
        await show({ isPromotionEligible: false, storefrontReady: true, paymentReady: false,
            shippingReady: true, productSellable: true, blockerCodes: ['PAYMENT_METHOD_REQUIRED'] }, language)
        const messages = language === 'ar' ? ar : en
        expect(document.querySelectorAll('[data-readiness-area]')).toHaveLength(4)
        expect(screen.getAllByText(messages.affiliatePublishingReadiness.states.ready)).toHaveLength(3)
        expect(screen.getByText(messages.affiliatePublishingReadiness.states.blocked)).toBeInTheDocument()
    })
    it('does not present enabled participation as eligible', async () => {
        await show({ isPromotionEligible: false, policyEnabled: true, blockerCodes: ['PAYMENT_METHOD_REQUIRED'] })
        expect(screen.getByText(en.affiliatePublishingReadiness.blocked)).toBeInTheDocument()
        expect(screen.queryByText(en.affiliatePublishingReadiness.ready)).not.toBeInTheDocument()
        expect(screen.getByRole('link', { name: 'Payment Methods' })).toHaveAttribute('href', '/ecommerce/payment-methods')
    })
    it('shows all simultaneous blockers with real corrective routes', async () => {
        await show({ isPromotionEligible: false, blockerCodes: ['STOREFRONT_NOT_ENABLED', 'PAYMENT_METHOD_REQUIRED', 'SHIPPING_METHOD_REQUIRED', 'PRODUCT_ARCHIVED'] })
        expect(screen.getAllByRole('listitem')).toHaveLength(4)
        expect(screen.getByRole('link', { name: 'Storefront Settings' })).toHaveAttribute('href', '/storefront/status')
        expect(screen.getByRole('link', { name: 'Shipping Methods' })).toHaveAttribute('href', '/ecommerce/shipping-methods')
        expect(screen.getByRole('link', { name: 'Manage product' })).toHaveAttribute('href', '/products/product-id/edit')
    })
    it('renders ready only from overall server admission', async () => {
        await show({ isPromotionEligible: true, blockerCodes: [] })
        expect(screen.getByText(en.affiliatePublishingReadiness.ready)).toBeInTheDocument()
        expect(screen.queryByRole('list')).not.toBeInTheDocument()
    })
    it('fails closed when no readiness response is available', async () => {
        await show(null)
        expect(screen.getByText(en.affiliatePublishingReadiness.unavailable)).toBeInTheDocument()
    })
    it('deduplicates blockers and never renders raw unknown server text', async () => {
        await show({ blockerCodes: ['PRODUCT_ARCHIVED', 'PRODUCT_ARCHIVED', '<private-sql>'] })
        expect(screen.getAllByRole('listitem')).toHaveLength(2)
        expect(screen.getByText(en.affiliatePublishingReadiness.review)).toBeInTheDocument()
        expect(screen.queryByText('<private-sql>')).not.toBeInTheDocument()
    })
    it('provides Arabic blockers and management guidance', async () => {
        await show({ blockerCodes: ['PAYMENT_METHOD_REQUIRED', 'PRODUCT_NOT_ACTIVE'] }, 'ar')
        expect(screen.getByText(ar.affiliatePublishingReadiness.codes.PAYMENT_METHOD_REQUIRED)).toBeInTheDocument()
        expect(screen.getByRole('link', { name: ar.affiliatePublishingReadiness.links.product })).toHaveAttribute('href', '/products/product-id/edit')
        expect(screen.queryByText(en.affiliatePublishingReadiness.blocked)).not.toBeInTheDocument()
    })
    it('has matching bilingual message keys for every server blocker', () => {
        expect(Object.keys(ar.affiliatePublishingReadiness.codes).sort()).toEqual(Object.keys(en.affiliatePublishingReadiness.codes).sort())
    })
})
