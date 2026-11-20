import React, { createContext, useCallback, useContext, useEffect, useRef, useState } from 'react'
import api from '../services/api'
import i18n from '../i18n/config'
import { useAuthStore } from '../store/authStore'
import {
    getLocaleForLanguage,
    formatCurrency as formatCurrencyValue,
    UNAVAILABLE_CURRENCY_VALUE,
} from '../utils/dateFormat'
import { assertTwoDecimalCurrency, normalizeCurrencyCode } from '../utils/financialMath'

const SettingsContext = createContext()
const DEFAULT_PRINT_FORMAT = 'receipt'
const FALLBACK_CURRENCY_SYMBOLS = {
    EGP: 'E\u00A3',
    USD: '$',
    EUR: '\u20AC',
    GBP: '\u00A3',
}

/**
 * Settings load provenance.
 *
 * `ready` is the only state that is evidence of the active tenant's own settings.
 * A failed read is `error` with a safe classification, never a confirmed default.
 */
export const SETTINGS_STATUS = {
    IDLE: 'idle',
    LOADING: 'loading',
    READY: 'ready',
    ERROR: 'error',
}

/** Safe failure classification. Never carries server text, tokens or credentials. */
export const SETTINGS_ERROR = {
    UNAUTHORIZED: 'unauthorized',
    FAILED: 'failed',
    MALFORMED: 'malformed',
}

/**
 * Currency provenance of the settings held in context:
 * - `confirmed`  — this scope's own successful response.
 * - `stale`      — this scope's last-known-good response, after a later refresh failed.
 * - `unconfirmed`— no successful response for this scope yet (idle/loading/failed).
 */
export const SETTINGS_PROVENANCE = {
    CONFIRMED: 'confirmed',
    STALE: 'stale',
    UNCONFIRMED: 'unconfirmed',
}

const getStoredPrintFormat = () => (
    typeof localStorage !== 'undefined'
        ? localStorage.getItem('printFormat') || DEFAULT_PRINT_FORMAT
        : DEFAULT_PRINT_FORMAT
)

const getCurrencySymbolFromCode = (currencyCode) => {
    const normalizedCode = normalizeCurrencyCode(currencyCode)
    if (!normalizedCode) return ''

    try {
        const currencyPart = new Intl.NumberFormat('en-US', {
            style: 'currency',
            currency: normalizedCode,
            currencyDisplay: 'narrowSymbol',
            minimumFractionDigits: 2,
            maximumFractionDigits: 2,
        }).formatToParts(0).find((part) => part.type === 'currency')?.value?.trim()

        if (currencyPart) {
            return currencyPart
        }
    } catch {
        // Fall through to static mapping.
    }

    return FALLBACK_CURRENCY_SYMBOLS[normalizedCode] || normalizedCode
}

const createDefaultSettings = () => ({
    currencyCode: '',
    currencySymbol: '',
    enableTax: true,
    taxRate: 0,
    companyName: '',
    companyLogo: '',
    companyAddress: '',
    companyPhone: '',
    companyEmail: '',
    receiptHeader: '',
    receiptFooter: '',
    showLogoOnReceipt: true,
    storefrontUrl: '',
    printFormat: getStoredPrintFormat(),
})

const normalizeSettingsPayload = (incomingSettings = {}, previousSettings = createDefaultSettings()) => {
    const previousCurrencyCode = normalizeCurrencyCode(previousSettings.currencyCode)
    const previousCurrencySymbol = previousSettings.currencySymbol || getCurrencySymbolFromCode(previousCurrencyCode)
    const nextCurrencyCode = Object.hasOwn(incomingSettings, 'currencyCode')
        ? normalizeCurrencyCode(incomingSettings.currencyCode) : previousCurrencyCode
    const hasIncomingCurrencySymbol = typeof incomingSettings.currencySymbol === 'string'
        && incomingSettings.currencySymbol.trim().length > 0
    const hasIncomingCurrencyCode = typeof incomingSettings.currencyCode === 'string'
        && incomingSettings.currencyCode.trim().length > 0
    const nextCurrencySymbol = hasIncomingCurrencySymbol
        ? incomingSettings.currencySymbol.trim()
        : hasIncomingCurrencyCode && nextCurrencyCode === previousCurrencyCode
            ? previousCurrencySymbol
            : getCurrencySymbolFromCode(nextCurrencyCode)

    return {
        ...createDefaultSettings(),
        ...previousSettings,
        ...incomingSettings,
        currencyCode: nextCurrencyCode,
        currencySymbol: nextCurrencyCode ? nextCurrencySymbol : '',
        printFormat: incomingSettings.printFormat || previousSettings.printFormat || getStoredPrintFormat(),
    }
}

// A settings response is usable only as a plain JSON object.
const isSettingsPayload = (payload) => Boolean(payload) && typeof payload === 'object' && !Array.isArray(payload)

// Only a response that names a currency can be evidence of the tenant currency.
const hasCurrencyEvidence = (payload) => {
    // A display glyph is not a currency identity. Reject unknown/unsupported codes.
    if (typeof payload.currencyCode !== 'string' || !/^[a-z]{3}$/i.test(payload.currencyCode.trim())) return false
    try { assertTwoDecimalCurrency(payload.currencyCode); return true } catch { return false }
}

export function SettingsProvider({ children }) {
    const [settings, setSettings] = useState(() => createDefaultSettings())
    const [status, setStatus] = useState(SETTINGS_STATUS.IDLE)
    const [error, setError] = useState(null)
    // The scope whose own successful response produced the current settings,
    // and the scope whose own response named the tenant currency.
    const [loadedScope, setLoadedScope] = useState(null)
    const [currencyScope, setCurrencyScope] = useState(null)
    const [currencyFresh, setCurrencyFresh] = useState(false)
    const isAuthenticated = useAuthStore((state) => state.isAuthenticated)
    const hasHydrated = useAuthStore((state) => state.hasHydrated)
    const tenantId = useAuthStore((state) => state.user?.tenantId)
    const actor = useAuthStore((state) => state.user)
    const scope = isAuthenticated ? JSON.stringify([tenantId, actor]) : null
    const scopeRef = useRef(scope)
    scopeRef.current = scope
    const requestRef = useRef(0)

    const fetchSettings = useCallback(async () => {
        const requestId = ++requestRef.current
        const requestedScope = scope

        if (!isAuthenticated || requestedScope !== scopeRef.current) {
            if (requestedScope === scopeRef.current) {
                setStatus(SETTINGS_STATUS.IDLE)
                setError(null)
            }
            return false
        }

        setStatus((current) => (current === SETTINGS_STATUS.READY ? current : SETTINGS_STATUS.LOADING))

        const isCurrentRequest = () => requestId === requestRef.current && requestedScope === scopeRef.current

        try {
            const response = await api.get('/settings', {
                headers: {
                    'Cache-Control': 'no-store',
                    Pragma: 'no-cache',
                },
            })
            if (!isCurrentRequest()) return false

            const payload = response?.data

            if (!isSettingsPayload(payload)) {
                setError(SETTINGS_ERROR.MALFORMED)
                setStatus(SETTINGS_STATUS.ERROR)
                return false
            }

            setSettings((prev) => normalizeSettingsPayload(payload, prev))
            setLoadedScope(requestedScope)
            setCurrencyFresh(hasCurrencyEvidence(payload))
            if (hasCurrencyEvidence(payload)) {
                setCurrencyScope(requestedScope)
            } else if (Object.hasOwn(payload, 'currencyCode') || Object.hasOwn(payload, 'currencySymbol')) {
                setCurrencyScope(null)
            }
            setError(null)
            setStatus(SETTINGS_STATUS.READY)
            return true
        } catch (requestError) {
            if (!isCurrentRequest()) return false

            const httpStatus = requestError?.response?.status
            const errorKind = httpStatus === 401 || httpStatus === 403
                ? SETTINGS_ERROR.UNAUTHORIZED
                : SETTINGS_ERROR.FAILED
            if (errorKind === SETTINGS_ERROR.UNAUTHORIZED) {
                setSettings(createDefaultSettings())
                setLoadedScope(null)
                setCurrencyScope(null)
            }

            // Only the HTTP status classification is recorded; response bodies,
            // headers, tokens and connection details are never surfaced.
            if (errorKind !== SETTINGS_ERROR.UNAUTHORIZED) {
                console.error('Error loading settings:', { status: httpStatus })
            }

            setError(errorKind)
            setStatus(SETTINGS_STATUS.ERROR)
            return false
        }
    }, [isAuthenticated, scope])

    useEffect(() => {
        if (!hasHydrated) return
        // A scope change invalidates both the payload and its provenance.
        setSettings(createDefaultSettings())
        setLoadedScope(null)
        setCurrencyScope(null)
        setCurrencyFresh(false)
        setError(null)
        if (isAuthenticated) {
            setStatus(SETTINGS_STATUS.LOADING)
            void fetchSettings()
        } else {
            setStatus(SETTINGS_STATUS.IDLE)
        }
        return () => { requestRef.current += 1 }
    }, [hasHydrated, isAuthenticated, fetchSettings])

    const updateSettings = async (newSettings) => {
        const requestedScope = scope
        const previousSettings = settings
        const previousPrintFormat = typeof localStorage !== 'undefined'
            ? localStorage.getItem('printFormat')
            : null

        try {
            const normalizedSettings = normalizeSettingsPayload(newSettings, previousSettings)

            if (typeof localStorage !== 'undefined') {
                if (normalizedSettings.printFormat) {
                    localStorage.setItem('printFormat', normalizedSettings.printFormat)
                } else {
                    localStorage.removeItem('printFormat')
                }
            }

            await api.put('/settings', normalizedSettings, {
                headers: {
                    'Cache-Control': 'no-store',
                    Pragma: 'no-cache',
                },
            })
            if (requestedScope !== scopeRef.current) return false
            return await fetchSettings()
        } catch (updateError) {
            if (requestedScope !== scopeRef.current) return false
            if ([401, 403].includes(updateError?.response?.status)) {
                setSettings(createDefaultSettings())
                setLoadedScope(null)
                setCurrencyScope(null)
                setCurrencyFresh(false)
                setError(SETTINGS_ERROR.UNAUTHORIZED)
                setStatus(SETTINGS_STATUS.ERROR)
            } else {
                setSettings(previousSettings)
            }

            if (typeof localStorage !== 'undefined') {
                if (previousPrintFormat !== null) {
                    localStorage.setItem('printFormat', previousPrintFormat)
                } else {
                    localStorage.removeItem('printFormat')
                }
            }

            console.error('Error updating settings:', { status: updateError?.response?.status })
            return false
        }
    }

    const updateLocalSettings = (key, value) => {
        setSettings((prev) => normalizeSettingsPayload({ [key]: value }, prev))
        if (key === 'currencyCode' || key === 'currencySymbol') setCurrencyScope(null)
        if (key === 'printFormat') {
            localStorage.setItem('printFormat', value)
        }
    }

    // Only this scope's own successful response may confirm its settings/currency.
    const withProvenance = (confirmedScope) => {
        if (confirmedScope === null || confirmedScope !== scope) {
            return SETTINGS_PROVENANCE.UNCONFIRMED
        }

        return status === SETTINGS_STATUS.READY && !error
            ? SETTINGS_PROVENANCE.CONFIRMED
            : SETTINGS_PROVENANCE.STALE
    }
    const provenance = withProvenance(loadedScope)
    const currencyScopeProvenance = withProvenance(currencyScope)
    const currencyProvenance = currencyScopeProvenance === SETTINGS_PROVENANCE.CONFIRMED && !currencyFresh
        ? SETTINGS_PROVENANCE.STALE : currencyScopeProvenance
    const currencyConfirmed = currencyProvenance !== SETTINGS_PROVENANCE.UNCONFIRMED

    const formatCurrency = (...args) => {
        const [amount, currency] = args
        if (amount === null || amount === undefined) {
            return ''
        }

        const language = i18n.resolvedLanguage || i18n.language
        const locale = getLocaleForLanguage(language)

        // An explicitly supplied currency is the caller's own evidence.
        if (args.length > 1) {
            return formatCurrencyValue(amount, currency, locale)
        }

        // Without a confirmed (or last-known-good) settings read there is no tenant
        // currency evidence, so no currency symbol may be invented for the amount.
        if (!currencyConfirmed) {
            return UNAVAILABLE_CURRENCY_VALUE
        }

        return formatCurrencyValue(amount, settings.currencyCode, locale)
    }

    const value = {
        // Never expose a previous authority's settings in the render before effects.
        settings: loadedScope === scope && scope !== null ? settings : createDefaultSettings(),
        loading: status === SETTINGS_STATUS.LOADING,
        status,
        error,
        provenance,
        currencyProvenance,
        currencyConfirmed,
        updateSettings,
        updateLocalSettings,
        formatCurrency,
        reloadSettings: fetchSettings,
    }

    return (
        <SettingsContext.Provider value={value}>
            {children}
        </SettingsContext.Provider>
    )
}

export function useSettings() {
    const context = useContext(SettingsContext)
    if (!context) {
        throw new Error('useSettings must be used within a SettingsProvider')
    }
    return context
}
