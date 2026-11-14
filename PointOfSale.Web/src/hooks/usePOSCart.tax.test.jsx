import { act, renderHook } from '@testing-library/react'
import { describe, expect, it, vi } from 'vitest'
import { usePOSCart } from './usePOSCart'

const configuration = vi.hoisted(() => ({ enableTax: true }))
vi.mock('../contexts/SettingsContext', () => ({ useSettings: () => ({ settings: configuration }) }))
vi.mock('react-i18next', () => ({ useTranslation: () => ({ t: (key) => key }) }))

describe('POS product tax preview', () => {
    it.each([
        [true, false, 300, 2, 10, 0, 60],
        [false, false, 300, 2, 10, 0, 0],
        [true, true, 330, 2, 10, 0, 0],
        [true, false, 300, 2, 0, 0, 0],
        [true, false, 300, 2, 15, 50, 82.5],
        [true, false, 0.05, 3, 10, 0, 0.02],
    ])('uses product rate and tenant enable with line rounding (%s/%s/%s)',
        (enabled, inclusive, price, quantity, rate, discount, tax) => {
            configuration.enableTax = enabled
            const { result } = renderHook(() => usePOSCart())
            act(() => result.current.retrieveSale({ items: [{ productId: 'p519', quantity, unitPrice: price, taxRate: rate,
                discountAmount: discount, priceIncludesTax: inclusive }] }))
            expect(result.current.calculateTotals().tax).toBe(tax)
            expect(result.current.calculateTotals().total).toBe(Number(price) * Number(quantity) - Number(discount) + Number(tax))
        })
})
