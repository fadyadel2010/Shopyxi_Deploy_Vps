import { normalizeDocumentLanguage } from '../i18n/documentLanguage'
import { assertTwoDecimalCurrency, normalizeCurrencyCode, toSafeNumber } from './financialMath'

const DEFAULT_DATE_OPTIONS = {
    year: 'numeric',
    month: 'short',
    day: 'numeric',
}

const DEFAULT_DATE_TIME_OPTIONS = {
    ...DEFAULT_DATE_OPTIONS,
    hour: '2-digit',
    minute: '2-digit',
}

const DEFAULT_TIME_OPTIONS = {
    hour: '2-digit',
    minute: '2-digit',
}

const DEFAULT_NUMBER_OPTIONS = {
    maximumFractionDigits: 2,
}

const DEFAULT_INTEGER_OPTIONS = {
    maximumFractionDigits: 0,
}

const DEFAULT_COMPACT_NUMBER_OPTIONS = {
    ...DEFAULT_INTEGER_OPTIONS,
    notation: 'compact',
}

/**
 * Neutral "no confirmed value" marker already used by the Dashboard presentation.
 * A currency amount whose currency is unknown, unsupported or whose amount is not
 * a finite number is rendered as this marker, never as a plausible EGP value.
 */
export const UNAVAILABLE_CURRENCY_VALUE = '\u2014'

const getDocumentLanguage = () => (
    typeof document !== 'undefined'
        ? document.documentElement?.lang || document.body?.getAttribute('lang') || undefined
        : undefined
)

export const getLocaleForLanguage = (language) => (
    normalizeDocumentLanguage(language || getDocumentLanguage()) === 'ar' ? 'ar-EG' : 'en-US'
)

const normalizeLocale = (locale) => {
    if (!locale) {
        return getLocaleForLanguage()
    }

    if (String(locale).includes('-')) {
        return locale
    }

    return getLocaleForLanguage(locale)
}

const createDateInstance = (value) => {
    if (!value) {
        return null
    }

    const date = value instanceof Date ? value : new Date(value)
    return Number.isNaN(date.getTime()) ? null : date
}

export const parseDateInput = (value) => {
    const match = /^(\d{4})-(\d{2})-(\d{2})$/.exec(value || '')
    if (!match) return null

    const [, year, month, day] = match.map(Number)
    const date = new Date(year, month - 1, day)
    return date.getFullYear() === year && date.getMonth() === month - 1 && date.getDate() === day
        ? date
        : null
}

export const formatDateInputValue = (value) => {
    const date = createDateInstance(value)
    if (!date) return ''

    const year = date.getFullYear()
    const month = String(date.getMonth() + 1).padStart(2, '0')
    const day = String(date.getDate()).padStart(2, '0')
    return `${year}-${month}-${day}`
}

const createNumberFormatter = (locale, options = DEFAULT_NUMBER_OPTIONS) => (
    new Intl.NumberFormat(normalizeLocale(locale), options)
)

/**
 * Currency must come from confirmed settings or the record itself. Neither an
 * omitted argument nor an invalid code is evidence of an EGP configuration.
 */
const resolveRequestedCurrencyCode = (currency) => {
    if (typeof currency !== 'string' || !/^[a-z]{3}$/i.test(currency.trim())) return null
    try {
        return assertTwoDecimalCurrency(normalizeCurrencyCode(currency))
    } catch {
        return null
    }
}

// Amount validity is decided independently of currency validity.
const isFormattableAmount = (value) => {
    if (typeof value === 'number') {
        return Number.isFinite(value)
    }

    if (typeof value === 'string') {
        return /^[+-]?(?:\d+(?:\.\d*)?|\.\d+)$/.test(value.trim()) && Number.isFinite(Number(value))
    }

    return false
}

export const formatDate = (value, locale, options = DEFAULT_DATE_OPTIONS) => {
    const date = createDateInstance(value)
    if (!date) {
        return ''
    }

    return date.toLocaleDateString(normalizeLocale(locale), options)
}

export const formatDateTime = (value, locale, options = DEFAULT_DATE_TIME_OPTIONS) => {
    const date = createDateInstance(value)
    if (!date) {
        return ''
    }

    return date.toLocaleString(normalizeLocale(locale), options)
}

export const formatTime = (value, locale, options = DEFAULT_TIME_OPTIONS) => {
    const date = createDateInstance(value)
    if (!date) {
        return ''
    }

    return date.toLocaleTimeString(normalizeLocale(locale), options)
}

export const formatNumber = (value, locale, options = DEFAULT_NUMBER_OPTIONS) => (
    createNumberFormatter(locale, options).format(toSafeNumber(value))
)

export const formatInteger = (value, locale, options = DEFAULT_INTEGER_OPTIONS) => (
    formatNumber(value, locale, options)
)

export const formatCompactNumber = (value, locale, options = DEFAULT_COMPACT_NUMBER_OPTIONS) => (
    formatNumber(value, locale, options)
)

export const formatCurrency = (value, currency, locale, options = {}) => {
    if (!isFormattableAmount(value)) {
        return UNAVAILABLE_CURRENCY_VALUE
    }

    const normalizedCurrency = resolveRequestedCurrencyCode(currency)

    if (normalizedCurrency === null) {
        return UNAVAILABLE_CURRENCY_VALUE
    }

    try {
        return createNumberFormatter(locale, {
            minimumFractionDigits: 2,
            maximumFractionDigits: 2,
            ...options,
            style: 'currency',
            currency: normalizedCurrency,
        }).format(toSafeNumber(value))
    } catch {
        // A formatter failure is not evidence that the currency is EGP.
        return UNAVAILABLE_CURRENCY_VALUE
    }
}

export const formatCompactCurrency = (value, currency, locale, options = {}) => (
    formatCurrency(value, currency, locale, {
        notation: 'compact',
        minimumFractionDigits: 0,
        maximumFractionDigits: 1,
        ...options,
    })
)

export const formatSignedCurrency = (value, currency, locale, options = {}) => {
    return formatCurrency(value, currency, locale, { ...options, signDisplay: 'always' })
}
