import { useTranslation } from 'react-i18next'
import { Link } from 'react-router-dom'

const destinations = {
    STOREFRONT_NOT_ENABLED: ['/storefront/status', 'storefront'],
    STOREFRONT_NOT_ACTIVE: ['/storefront/status', 'storefront'],
    PAYMENT_METHOD_REQUIRED: ['/ecommerce/payment-methods', 'payments'],
    SHIPPING_METHOD_REQUIRED: ['/ecommerce/shipping-methods', 'shipping'],
}
const productCodes = new Set(['PRODUCT_NOT_ACTIVE', 'PRODUCT_NOT_PUBLISHED', 'PRODUCT_ARCHIVED',
    'PRODUCT_NOT_SELLABLE', 'PRODUCT_CONFIGURATION_INVALID'])
const groups = [
    ['storefront', 'storefrontReady', code => code.startsWith('STOREFRONT_')],
    ['payment', 'paymentReady', code => code === 'PAYMENT_METHOD_REQUIRED'],
    ['shipping', 'shippingReady', code => code === 'SHIPPING_METHOD_REQUIRED'],
    ['product', 'productSellable', code => productCodes.has(code)],
]

export default function AffiliatePromotionReadiness({ readiness, productId, compact = false }) {
    const { t } = useTranslation()
    const codes = Array.isArray(readiness?.blockerCodes) ? [...new Set(readiness.blockerCodes)] : []
    const renderBlockers = currentCodes => currentCodes.length > 0 && (
        <ul className="mt-1 space-y-1">
            {currentCodes.map(code => {
                const destination = destinations[code] || (productCodes.has(code) ? [`/products/${productId}/edit`, 'product'] : null)
                return <li key={code} data-blocker-code={code}>
                    {t(`affiliatePublishingReadiness.codes.${code}`, { defaultValue: t('affiliatePublishingReadiness.review') })}
                    {destination && <>{' '}<Link className="underline" to={destination[0]}>{t(`affiliatePublishingReadiness.links.${destination[1]}`)}</Link></>}
                </li>
            })}
        </ul>
    )
    return (
        <section className={`${compact ? 'mt-2 text-xs' : 'my-3 rounded-lg border p-3 text-sm'} ${readiness?.isPromotionEligible ? 'text-emerald-800' : 'text-amber-900'}`}
            aria-label={t('affiliatePublishingReadiness.title')} data-testid="affiliate-promotion-readiness">
            <p className="font-semibold">{t(`affiliatePublishingReadiness.${!readiness ? 'unavailable' : readiness.isPromotionEligible ? 'ready' : 'blocked'}`)}</p>
            {compact ? renderBlockers(codes) : <>
                <div className="mt-2 grid gap-3 sm:grid-cols-2">
                    {groups.map(([area, field, matches]) => <div key={area} data-readiness-area={area}>
                        <h4 className="font-medium">{t(`affiliatePublishingReadiness.areas.${area}`)}: {' '}
                            <span>{t(`affiliatePublishingReadiness.states.${!readiness ? 'unknown' : readiness[field] ? 'ready' : 'blocked'}`)}</span>
                        </h4>
                        {renderBlockers(codes.filter(matches))}
                    </div>)}
                </div>
                {renderBlockers(codes.filter(code => !groups.some(([, , matches]) => matches(code))))}
            </>}
        </section>
    )
}
