import { useSettings } from '../contexts/SettingsContext'
import {
    UNAVAILABLE_CURRENCY_VALUE,
    formatCompactNumber as formatCompactNumberValue,
    formatCurrency as formatCurrencyValue,
    formatDate as formatDateValue,
    formatDateTime as formatDateTimeValue,
    formatInteger as formatIntegerValue,
    formatNumber as formatNumberValue,
    formatSignedCurrency as formatSignedCurrencyValue,
    formatTime as formatTimeValue,
} from '../utils/dateFormat'
import { useRTL } from './useRTL'

export function useFormatting() {
    const { settings, status, currencyConfirmed } = useSettings()
    const { dir, isRTL, language, locale } = useRTL()

    // A failed settings read for the active scope has no tenant currency evidence,
    // so no currency symbol may be invented for an amount. Surfaces with no settings
    // read at all (public marketplace) keep their existing record-level behaviour.
    const currencyUnconfirmed = status !== undefined && !currencyConfirmed
    const formatCurrencyWith = (formatter, value, currency, options) => {
        if (currency !== undefined) {
            return formatter(value, currency, locale, options)
        }

        if (currencyUnconfirmed) {
            return UNAVAILABLE_CURRENCY_VALUE
        }

        return formatter(value, settings?.currencyCode, locale, options)
    }

    return {
        dir,
        isRTL,
        language,
        locale,
        formatDate: (value, options) => formatDateValue(value, locale, options),
        formatDateTime: (value, options) => formatDateTimeValue(value, locale, options),
        formatTime: (value, options) => formatTimeValue(value, locale, options),
        formatNumber: (value, options) => formatNumberValue(value, locale, options),
        formatInteger: (value, options) => formatIntegerValue(value, locale, options),
        formatCompactNumber: (value, options) => formatCompactNumberValue(value, locale, options),
        formatCurrency: (value, currency, options) => (
            formatCurrencyWith(formatCurrencyValue, value, currency, options)
        ),
        formatSignedCurrency: (value, currency, options) => (
            formatCurrencyWith(formatSignedCurrencyValue, value, currency, options)
        ),
    }
}

export default useFormatting
