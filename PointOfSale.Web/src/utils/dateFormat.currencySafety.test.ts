/**
 * Group 5B — D-5 regression protection.
 *
 * `formatCurrency` must never turn an explicitly invalid or unsupported currency
 * code into a visually plausible EGP value, and must never turn a missing/invalid
 * amount into a confirmed zero.
 */
import { describe, expect, it } from 'vitest'
import {
    formatCompactCurrency,
    formatCurrency,
    formatSignedCurrency,
    UNAVAILABLE_CURRENCY_VALUE,
} from './dateFormat'

const CURRENCY_SYMBOL_PATTERN = /EGP|E£|\$|£|€/

describe('formatCurrency explicit currency authority', () => {
    it('formats an explicit supported EGP amount', () => {
        expect(formatCurrency(1234.5, 'EGP', 'en-US')).toContain('1,234.50')
        expect(formatCurrency(1234.5, 'EGP', 'en-US')).toMatch(/EGP|E£/)
    })

    it('formats an explicit supported non-EGP currency', () => {
        const result = formatCurrency(1234.5, 'USD', 'en-US')

        expect(result).toContain('1,234.50')
        expect(result).toMatch(/\$|USD/)
        expect(result).not.toMatch(/EGP|E£/)
    })

    it('requires evidence even when the currency argument is omitted', () => {
        expect(formatCurrency(10)).toBe(UNAVAILABLE_CURRENCY_VALUE)
        expect(formatCurrency(10, undefined, 'en-US')).toBe(UNAVAILABLE_CURRENCY_VALUE)
    })

    it.each([
        ['empty string', ''],
        ['whitespace', '   '],
        ['null', null],
        ['unsupported minor units', 'JOD'],
        ['unsupported 0-decimal currency', 'JPY'],
        ['unknown code', 'INVALID'],
        ['malformed code', 'E'],
        ['object', {}],
    ])('renders the neutral unavailable value for an explicit %s currency', (_label, currency) => {
        const result = formatCurrency(10, currency as string, 'en-US')

        expect(result).toBe(UNAVAILABLE_CURRENCY_VALUE)
        expect(result).not.toMatch(CURRENCY_SYMBOL_PATTERN)
    })

    it('does not normalize one invalid code into a different valid currency', () => {
        expect(formatCurrency(10, 'jod', 'en-US')).toBe(UNAVAILABLE_CURRENCY_VALUE)
        expect(formatCurrency(10, 'egp ', 'en-US')).toContain('10.00')
        expect(formatCurrency(10, 'jod', 'en-US')).not.toBe(formatCurrency(10, 'EGP', 'en-US'))
    })

    it('propagates the neutral unavailable value through compact and signed formatting', () => {
        expect(formatCompactCurrency(10, 'JOD', 'en-US')).toBe(UNAVAILABLE_CURRENCY_VALUE)
        expect(formatSignedCurrency(10, 'JOD', 'en-US')).toBe(UNAVAILABLE_CURRENCY_VALUE)
    })
    it('does not let presentation options replace the authoritative currency', () => {
        expect(formatCurrency(10, 'EGP', 'en-US', { currency: 'USD' })).not.toMatch(/\$/)
    })
    it.each(['$', 'E£', '€'])('does not treat the display glyph %s as a currency identity', symbol => {
        expect(formatCurrency(10, symbol, 'en-US')).toBe(UNAVAILABLE_CURRENCY_VALUE)
    })
})

describe('formatCurrency amount authority', () => {
    it('preserves a legitimate numeric zero', () => {
        expect(formatCurrency(0, 'EGP', 'en-US')).toContain('0.00')
        expect(formatCurrency(0, 'EGP', 'en-US')).not.toBe(UNAVAILABLE_CURRENCY_VALUE)
        expect(formatCurrency('0', 'EGP', 'en-US')).toContain('0.00')
    })

    it('renders a negative amount with its locale sign placement', () => {
        const english = formatCurrency(-1234.5, 'EGP', 'en-US')
        const arabic = formatCurrency(-1234.5, 'EGP', 'ar-EG')

        expect(english).toContain('1,234.50')
        expect(english).not.toBe(UNAVAILABLE_CURRENCY_VALUE)
        expect(arabic).not.toBe(UNAVAILABLE_CURRENCY_VALUE)
        expect(arabic).toContain('١')
    })

    it.each([
        ['null', null],
        ['undefined', undefined],
        ['NaN', Number.NaN],
        ['Infinity', Number.POSITIVE_INFINITY],
        ['overflowing numeric string', '9'.repeat(400)],
        ['empty string', ''],
        ['malformed string', '12,34'],
        ['boolean', true],
        ['object', { amount: 10 }],
    ])('does not present an invalid %s amount as a confirmed zero', (_label, amount) => {
        const result = formatCurrency(amount, 'EGP', 'en-US')

        expect(result).toBe(UNAVAILABLE_CURRENCY_VALUE)
        expect(result).not.toContain('0.00')
    })

    it('decides amount validity independently of currency validity', () => {
        expect(formatCurrency(null, 'JOD', 'en-US')).toBe(UNAVAILABLE_CURRENCY_VALUE)
        expect(formatCurrency(null, 'EGP', 'en-US')).toBe(UNAVAILABLE_CURRENCY_VALUE)
        expect(formatCurrency(0, 'JOD', 'en-US')).toBe(UNAVAILABLE_CURRENCY_VALUE)
    })
})

describe('formatCurrency locale behaviour for supported currencies', () => {
    it('keeps English and Arabic rendering for a supported currency', () => {
        const english = formatCurrency(1234.5, 'USD', 'en-US', { currencyDisplay: 'code' })
        const arabic = formatCurrency(1234.5, 'USD', 'ar-EG', { currencyDisplay: 'code' })

        expect(english).toContain('USD')
        expect(arabic).toContain('١')
        expect(arabic).not.toBe(english)
    })

    it('keeps Arabic EGP rendering localized rather than falling back to en-US', () => {
        const arabic = formatCurrency(10, 'EGP', 'ar-EG')

        expect(arabic).not.toBe(formatCurrency(10, 'EGP', 'en-US'))
        expect(arabic).not.toBe(UNAVAILABLE_CURRENCY_VALUE)
    })
})
