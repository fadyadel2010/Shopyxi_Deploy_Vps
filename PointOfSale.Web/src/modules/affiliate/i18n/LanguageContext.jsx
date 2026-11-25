/**
 * AF-15: Affiliate Language Context
 * 
 * Provides language switching (en/ar) with:
 * - Persistent selection via localStorage
 * - RTL direction auto-applied to wrapper
 * - Simple `t('section.key')` accessor
 */
import { createContext, useContext, useState, useCallback, useEffect } from 'react'
import translations from './translations'
import i18n from '../../../i18n/config'
import { syncDocumentLanguage } from '../../../i18n/documentLanguage'

const LanguageContext = createContext(null)

const STORAGE_KEY = 'affiliate_lang'

export function LanguageProvider({ children }) {
    const [lang, setLangState] = useState(() => {
        try { return localStorage.getItem(STORAGE_KEY) === 'ar' ? 'ar' : 'en' } catch { return 'en' }
    })

    const setLang = useCallback((newLang) => {
        if (newLang !== 'en' && newLang !== 'ar') return
        setLangState(newLang)
        try { localStorage.setItem(STORAGE_KEY, newLang) } catch { /* In-memory switching remains available. */ }
    }, [])

    const toggleLang = useCallback(() => {
        setLang(lang === 'en' ? 'ar' : 'en')
    }, [lang, setLang])

    const t = useCallback((path, params = {}) => {
        const keys = path.split('.')
        let val = translations[lang]
        for (const k of keys) {
            val = val?.[k]
        }
        if (typeof val !== 'string') {
            val = keys.reduce((value, key) => value?.[key], translations.en)
        }
        return typeof val === 'string' ? val.replace(/\{(\w+)\}/g, (match, key) => params[key] ?? match) : path
    }, [lang])

    const dir = translations[lang]?.dir || 'ltr'
    const isRTL = dir === 'rtl'
    const locale = lang === 'ar' ? 'ar-EG' : 'en-US'
    const formatNumber = (value, options) => new Intl.NumberFormat(locale, options).format(value)
    const formatDate = (value, options) => {
        const date = value ? new Date(value) : null
        return date && !Number.isNaN(date.getTime()) ? date.toLocaleDateString(locale, options) : '—'
    }
    const formatMoney = (value, currency, options = {}) => {
        if (value == null) return '—'
        const code = typeof currency === 'string' ? currency.trim().toUpperCase() : ''
        if (!/^[A-Z]{3}$/.test(code)) return t('ui.currencyUnavailable')
        return new Intl.NumberFormat(locale, { style: 'currency', currency: code, minimumFractionDigits: 2, ...options }).format(value)
    }

    // Apply font for Arabic
    useEffect(() => {
        const applyAffiliateLanguage = () => syncDocumentLanguage(lang)
        applyAffiliateLanguage()
        // The Affiliate choice stays independent even when the host preference changes.
        i18n.on('languageChanged', applyAffiliateLanguage)
        return () => {
            i18n.off('languageChanged', applyAffiliateLanguage)
            syncDocumentLanguage(i18n.resolvedLanguage || i18n.language)
        }
    }, [lang, dir])

    useEffect(() => {
        if (lang === 'ar') {
            const link = document.getElementById('affiliate-arabic-font')
            if (!link) {
                const el = document.createElement('link')
                el.id = 'affiliate-arabic-font'
                el.rel = 'stylesheet'
                el.href = 'https://fonts.googleapis.com/css2?family=Noto+Kufi+Arabic:wght@400;500;600;700;800&display=swap'
                document.head.appendChild(el)
            }
        }
    }, [lang])

    return (
        <LanguageContext.Provider value={{ lang, setLang, toggleLang, t, dir, isRTL, locale, formatNumber, formatDate, formatMoney }}>
            {children}
        </LanguageContext.Provider>
    )
}

export function useLanguage() {
    const ctx = useContext(LanguageContext)
    if (!ctx) throw new Error('useLanguage must be used within LanguageProvider')
    return ctx
}
