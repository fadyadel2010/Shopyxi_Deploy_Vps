import { describe, expect, it } from 'vitest'
import {
    formatCompactCurrency,
    formatCurrency,
    formatDateInputValue,
    formatSignedCurrency,
    parseDateInput,
    UNAVAILABLE_CURRENCY_VALUE,
} from './dateFormat'

describe('dateFormat currency helpers', () => {
    it('never relabels an unsupported two-decimal-unsafe currency as EGP', () => {
        const result = formatCurrency(10, 'JOD', 'en-US')

        expect(result).toBe(UNAVAILABLE_CURRENCY_VALUE)
        expect(result).not.toMatch(/EGP|E£|\$|\u00A3|\u20AC/)
    })

    it('never relabels an unknown currency as EGP across compact currency formatting too', () => {
        const result = formatCompactCurrency(10, 'INVALID', 'en-US')

        expect(result).toBe(UNAVAILABLE_CURRENCY_VALUE)
        expect(result).not.toMatch(/EGP|E£|\$|\u00A3|\u20AC/)
    })

    it('requires currency evidence rather than inventing a default when omitted', () => {
        expect(formatCurrency(10, undefined, 'en-US')).toBe(UNAVAILABLE_CURRENCY_VALUE)
    })

    it('delegates signed currency rendering to Intl so bidi sign placement stays locale-aware', () => {
        const options = { currencyDisplay: 'code' } satisfies Intl.NumberFormatOptions

        expect(formatSignedCurrency(10, 'USD', 'ar-EG', options)).toBe(
            formatCurrency(10, 'USD', 'ar-EG', {
                ...options,
                signDisplay: 'always',
            }),
        )
        expect(formatSignedCurrency(-10, 'USD', 'ar-EG', options)).toBe(
            formatCurrency(-10, 'USD', 'ar-EG', {
                ...options,
                signDisplay: 'always',
            }),
        )
    })
})

describe('date input helpers', () => {
    it('round-trips a calendar date without UTC conversion', () => {
        const date = parseDateInput('2026-09-16')

        expect(date).not.toBeNull()
        expect(date?.getFullYear()).toBe(2026)
        expect(date?.getMonth()).toBe(8)
        expect(date?.getDate()).toBe(16)
        expect(formatDateInputValue(date)).toBe('2026-09-16')
    })

    it('rejects blank and impossible calendar dates', () => {
        expect(parseDateInput('')).toBeNull()
        expect(parseDateInput('2026-02-30')).toBeNull()
        expect(formatDateInputValue(new Date('invalid'))).toBe('')
    })
})
