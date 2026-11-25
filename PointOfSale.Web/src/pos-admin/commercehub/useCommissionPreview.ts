import { useEffect, useMemo, useRef, useState } from 'react'
import api from '../../services/api'
import { useDebouncedValue } from '../../hooks/useDebouncedValue'

interface CommissionPreviewApiResponse {
    vendorId?: string | null
    variantId?: string | null
    parentCategoryId?: string | null
    categoryId?: string | null
    pricingMode?: string | null
    currency?: string | null
    commissionBase?: string | null
    sourceProductPrice?: number | null
    marketplacePrice?: number | null
    rate?: number | null
    commissionAmount?: number | null
    merchantNet?: number | null
    source?: string | null
    ruleId?: string | null
    priority?: number | null
}

interface UseCommissionPreviewInput {
    vendorId?: string | null
    variantId?: string | null
    parentCategoryId?: string | null
    categoryId?: string | null
    pricingMode: 'source' | 'custom'
    marketplacePrice?: number | null
    debounceMs?: number
}

export interface UseCommissionPreviewResult {
    sourceProductPrice: number | null
    marketplacePrice: number | null
    rate: number | null
    commissionAmount: number | null
    vendorReceives: number | null
    commissionBase: string | null
    source: string | null
    ruleId: string | null
    priority: number | null
    loading: boolean
    errorCode: string | null
    hasCategory: boolean
    isResolved: boolean
}

interface RequestIdentity {
    vendorId: string | null
    variantId: string | null
    parentCategoryId: string | null
    categoryId: string | null
    pricingMode: 'source' | 'custom'
    marketplacePrice: number | null
}

const DEFAULT_DEBOUNCE_MS = 400

export function useCommissionPreview({
    vendorId,
    variantId,
    parentCategoryId,
    categoryId,
    pricingMode,
    marketplacePrice,
    debounceMs = DEFAULT_DEBOUNCE_MS,
}: UseCommissionPreviewInput): UseCommissionPreviewResult {
    const [resolution, setResolution] = useState<CommissionPreviewApiResponse | null>(null)
    const [loading, setLoading] = useState(false)
    const [errorCode, setErrorCode] = useState<string | null>(null)
    const requestIdRef = useRef(0)
    const requestIdentity = useMemo<RequestIdentity>(() => ({
        vendorId: vendorId ?? null,
        variantId: variantId ?? null,
        parentCategoryId: parentCategoryId ?? null,
        categoryId: categoryId ?? null,
        pricingMode,
        marketplacePrice: pricingMode === 'custom' && Number.isFinite(marketplacePrice)
            ? Number(marketplacePrice)
            : null,
    }), [categoryId, marketplacePrice, parentCategoryId, pricingMode, variantId, vendorId])
    const debouncedRequest = useDebouncedValue(requestIdentity, debounceMs)

    useEffect(() => () => { requestIdRef.current += 1 }, [])

    useEffect(() => {
        requestIdRef.current += 1
        setResolution(null)
        setErrorCode(null)
        setLoading(Boolean(requestIdentity.categoryId && requestIdentity.parentCategoryId && requestIdentity.variantId))
    }, [requestIdentity])

    useEffect(() => {
        if (!debouncedRequest.categoryId || !debouncedRequest.parentCategoryId || !debouncedRequest.variantId) {
            return undefined
        }
        if (debouncedRequest.pricingMode === 'custom' && (!debouncedRequest.marketplacePrice || debouncedRequest.marketplacePrice <= 0)) {
            setLoading(false)
            setErrorCode('INVALID_CUSTOM_PRICE')
            return undefined
        }

        const controller = new AbortController()
        const requestId = ++requestIdRef.current
        setLoading(true)
        setErrorCode(null)

        async function resolvePreview() {
            try {
                const response = await api.get<CommissionPreviewApiResponse>('/admin/commercehub/commission/resolve-preview', {
                    params: {
                        vendorId: debouncedRequest.vendorId,
                        variantId: debouncedRequest.variantId,
                        parentCategoryId: debouncedRequest.parentCategoryId,
                        categoryId: debouncedRequest.categoryId,
                        pricingMode: debouncedRequest.pricingMode,
                        marketplacePrice: debouncedRequest.pricingMode === 'custom'
                            ? debouncedRequest.marketplacePrice
                            : undefined,
                    },
                    signal: controller.signal,
                })
                if (requestId !== requestIdRef.current) return
                const data = response.data
                if (!data?.ruleId || data.rate == null || data.commissionAmount == null || data.merchantNet == null) {
                    setResolution(null)
                    setErrorCode('INVALID_PREVIEW_RESPONSE')
                    return
                }
                setResolution(data)
                setErrorCode(null)
            } catch (error: unknown) {
                if (controller.signal.aborted || requestId !== requestIdRef.current) return
                const apiError = error as { response?: { status?: number; data?: { code?: string } } }
                setResolution(null)
                setErrorCode(apiError.response?.data?.code || (apiError.response?.status === 409
                    ? 'COMMISSION_AUTHORITY_MISSING'
                    : 'PREVIEW_UNAVAILABLE'))
            } finally {
                if (requestId === requestIdRef.current) setLoading(false)
            }
        }

        void resolvePreview()

        return () => controller.abort()
    }, [debouncedRequest])

    return {
        sourceProductPrice: resolution?.sourceProductPrice ?? null,
        marketplacePrice: resolution?.marketplacePrice ?? null,
        rate: resolution?.rate ?? null,
        commissionAmount: resolution?.commissionAmount ?? null,
        vendorReceives: resolution?.merchantNet ?? null,
        commissionBase: resolution?.commissionBase ?? null,
        source: resolution?.source ?? null,
        ruleId: resolution?.ruleId ?? null,
        priority: resolution?.priority ?? null,
        loading,
        errorCode,
        hasCategory: Boolean(categoryId),
        isResolved: Boolean(resolution?.ruleId),
    }
}
