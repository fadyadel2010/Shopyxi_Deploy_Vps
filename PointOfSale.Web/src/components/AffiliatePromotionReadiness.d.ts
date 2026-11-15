import type { ReactElement } from 'react'
export interface PromotionReadiness {
    productFound: boolean
    storefrontReady: boolean
    paymentReady: boolean
    shippingReady: boolean
    productSellable: boolean
    policyEnabled: boolean
    categoryReady: boolean
    prerequisitesReady: boolean
    isPromotionEligible: boolean
    blockerCodes: string[]
}
export default function AffiliatePromotionReadiness(props: {
    readiness?: PromotionReadiness | null
    productId: string
    compact?: boolean
}): ReactElement
