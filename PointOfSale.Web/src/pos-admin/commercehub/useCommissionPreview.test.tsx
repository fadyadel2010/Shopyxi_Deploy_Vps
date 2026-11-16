import { act, renderHook, waitFor } from '@testing-library/react'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import api from '../../services/api'
import en from '../../i18n/locales/en.json'
import ar from '../../i18n/locales/ar.json'
import { useCommissionPreview } from './useCommissionPreview'

vi.mock('../../services/api', () => ({
    default: { get: vi.fn() },
}))

const resolvedPreview = (price: number) => ({
    data: {
        ruleId: 'rule-1',
        rate: 0.1,
        source: 'Category',
        priority: 10,
        sourceProductPrice: 100,
        marketplacePrice: price,
        commissionAmount: price * 0.1,
        merchantNet: price * 0.9,
        commissionBase: 'Subtotal',
    },
})

describe('useCommissionPreview', () => {
    beforeEach(() => vi.mocked(api.get).mockReset())

    it('uses the latest custom price response and ignores an obsolete request', async () => {
        let resolveFirst!: (value: ReturnType<typeof resolvedPreview>) => void
        let resolveSecond!: (value: ReturnType<typeof resolvedPreview>) => void
        vi.mocked(api.get)
            .mockReturnValueOnce(new Promise(resolve => { resolveFirst = resolve }))
            .mockReturnValueOnce(new Promise(resolve => { resolveSecond = resolve }))

        const { result, rerender } = renderHook((props: { price: number }) => useCommissionPreview({
            vendorId: 'vendor', variantId: 'variant', parentCategoryId: 'parent', categoryId: 'child',
            pricingMode: 'custom', marketplacePrice: props.price, debounceMs: 0,
        }), { initialProps: { price: 120 } })

        await waitFor(() => expect(api.get).toHaveBeenCalledTimes(1))
        rerender({ price: 130 })
        await waitFor(() => expect(api.get).toHaveBeenCalledTimes(2))
        await act(async () => resolveSecond(resolvedPreview(130)))
        await waitFor(() => expect(result.current.marketplacePrice).toBe(130))
        await act(async () => resolveFirst(resolvedPreview(120)))
        expect(result.current.marketplacePrice).toBe(130)
    })

    it('fails closed without fabricated zero amounts when preview has no resolved rule', async () => {
        vi.mocked(api.get).mockResolvedValue({ data: {} })
        const { result } = renderHook(() => useCommissionPreview({
            vendorId: 'vendor', variantId: 'variant', parentCategoryId: 'parent', categoryId: 'child',
            pricingMode: 'source', debounceMs: 0,
        }))

        await waitFor(() => expect(result.current.errorCode).toBe('INVALID_PREVIEW_RESPONSE'))
        expect(result.current.isResolved).toBe(false)
        expect(result.current.commissionAmount).toBeNull()
        expect(result.current.vendorReceives).toBeNull()
    })
})

describe('CommerceHub publishing localization', () => {
    it('has complete English and Arabic commission/pricing labels', () => {
        expect(en.commerceHub.commission.title).toBeTruthy()
        expect(en.commerceHub.pricing.customMode).toBeTruthy()
        expect(ar.commerceHub.commission.title).toBe('العمولة ومستحقات التاجر')
        expect(ar.commerceHub.pricing.customMode).toBe('سعر مخصص للسوق')
        expect(ar.commerceHub.commission.errors.missingRule).toBeTruthy()
    })
})
