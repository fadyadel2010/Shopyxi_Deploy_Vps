import { useCallback } from 'react'
import { useTranslation } from 'react-i18next'

export type GuideBlock = {
    title: string
    purpose: string
    when: string
    steps: string[]
    statuses?: Array<{ label: string; description: string }>
    safety?: string[]
}

export type BilingualGuide = {
    en: GuideBlock
    ar: GuideBlock
}

const numberLocale = (isArabic: boolean) => (isArabic ? 'ar-EG-u-nu-latn' : 'en-US')

export function useSuperAdminLocale() {
    const { t, i18n } = useTranslation()
    const language = String(i18n?.resolvedLanguage || i18n?.language || 'en')
    const isArabic = language.toLowerCase().startsWith('ar')
    const dir = isArabic ? 'rtl' : 'ltr'
    const locale = numberLocale(isArabic)

    const tr = useCallback(
        (english: string, arabic: string) => (isArabic ? arabic : english),
        [isArabic],
    )

    const formatNumber = (value: number, options?: Intl.NumberFormatOptions) =>
        new Intl.NumberFormat(locale, options).format(Number(value ?? 0))

    const formatCurrency = (value: number, currency = 'EGP') => {
        const normalized = String(currency || 'EGP').trim().toUpperCase()
        try {
            return new Intl.NumberFormat(locale, {
                style: 'currency',
                currency: normalized,
                currencyDisplay: 'code',
                minimumFractionDigits: 2,
                maximumFractionDigits: 2,
            }).format(Number(value ?? 0))
        } catch {
            return `${normalized} ${Number(value ?? 0).toFixed(2)}`
        }
    }

    const formatPercent = (value: number, digits = 1) =>
        new Intl.NumberFormat(locale, {
            style: 'percent',
            minimumFractionDigits: digits,
            maximumFractionDigits: digits,
        }).format(Number(value ?? 0))

    const formatDate = (value?: string | null, fallback = tr('Pending', 'قيد الانتظار')) => {
        if (!value) return fallback
        const date = new Date(value)
        if (Number.isNaN(date.getTime())) return fallback
        return date.toLocaleDateString(locale)
    }

    const formatDateTime = (value?: string | null, fallback = tr('Pending', 'قيد الانتظار')) => {
        if (!value) return fallback
        const date = new Date(value)
        if (Number.isNaN(date.getTime())) return fallback
        return date.toLocaleString(locale, { dateStyle: 'medium', timeStyle: 'short' })
    }

    const formatDateInput = (value?: string | null) => {
        if (!value) return ''
        const date = new Date(value)
        if (Number.isNaN(date.getTime())) return ''
        return date.toISOString().slice(0, 10)
    }

    return {
        t,
        language,
        isArabic,
        dir,
        locale,
        tr,
        formatNumber,
        formatCurrency,
        formatPercent,
        formatDate,
        formatDateTime,
        formatDateInput,
    }
}
