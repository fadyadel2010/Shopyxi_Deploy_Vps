import { useCallback, useEffect, useRef, useState } from 'react'
import api from '../../services/api'
import { useLatestRequestGuard } from '../../hooks/useLatestRequestGuard'
import { useAuthStore } from '../../store/authStore'
import { hasPosPermission } from '../../auth/posPermissions'

export const isNumber = value => typeof value === 'number' && Number.isFinite(value)
const text = value => typeof value === 'string' && value.trim().length > 0
const date = value => text(value) && Number.isFinite(Date.parse(value))
const rows = check => value => Array.isArray(value) && value.every(row => row && check(row))
// Absent/null is tolerated for backward compatibility; a present value must be well formed.
const optionalText = value => value === undefined || value === null || text(value)
const optionalFlag = value => value === undefined || value === null || typeof value === 'boolean'
const statsFields = [
    'todaySales', 'salesChangePercentage', 'todayTransactionCount', 'lowStockCount',
    'inventoryValue', 'totalProducts', 'totalCustomers', 'weekSales', 'monthSales',
    'yearSales', 'totalRevenue', 'outstandingReceivables', 'outstandingPayables',
]

// Validate only consumed fields; never coerce missing/invalid financial values to zero.
export const POS_SOURCES = {
    stats: { url: () => '/dashboard/stats', valid: value => {
        if (!value) return false
        if (value.financialSummaryAvailable === false)
            return ['totalProducts', 'totalCustomers', 'lowStockCount'].every(key => isNumber(value[key]))
        return statsFields.every(key => (key === 'salesChangePercentage' || key === 'inventoryValue')
            && value.financialSummaryAvailable === true && value[key] == null || isNumber(value[key]))
    } },
    trend: {
        url: period => `/dashboard/sales-trend?period=${period}`,
        valid: value => value && rows(row => date(row.date) && /^\d{4}-\d{2}-\d{2}(?:T|$)/.test(row.date) && text(row.label) && isNumber(row.amount))(value.data),
        select: value => value.data,
    },
    products: { url: () => '/dashboard/top-products?limit=5', valid: rows(row => text(row.productName) && isNumber(row.totalRevenue)) },
    // Recent-sales customer identity is semantic: the walk-in flag decides the localized
    // label, a real name is shown unchanged, and an unavailable name is never invented.
    sales: {
        url: () => '/dashboard/recent-sales?limit=8',
        valid: rows(row => text(row.invoiceNumber) && date(row.invoiceDate) && isNumber(row.totalAmount)
            && optionalText(row.customerName)
            && optionalFlag(row.isWalkInCustomer)),
    },
    stock: { url: () => '/dashboard/low-stock?limit=8', valid: rows(row => text(row.productName) && text(row.warehouseName) && isNumber(row.currentStock) && ['Out of Stock', 'Low Stock'].includes(row.status)) },
    categories: { url: () => '/dashboard/category-sales', valid: rows(row => (row.isUncategorized === true || text(row.categoryName)) && isNumber(row.totalRevenue)) },
    payments: { url: () => '/dashboard/payment-methods', valid: rows(row => text(row.paymentMethod) && isNumber(row.totalAmount)) },
}

const initial = scope => ({ scope, data: null, pending: true, error: null, lastSuccess: null })

export function useDashboardActorScope() {
    const user = useAuthStore(state => state.user)
    // Includes tenant, actor and effective authority/scope, not a tenant supplied to the API.
    return JSON.stringify(user)
}

const utcDay = () => new Date().toISOString().slice(0, 10)

export function useDashboardUtcDay() {
    const [day, setDay] = useState(utcDay)
    useEffect(() => {
        // Invalidate relative UTC periods at their boundary. This is provenance,
        // not a replacement for the backend's period/financial definitions.
        const timer = window.setTimeout(() => setDay(utcDay()), 86400000 - Date.now() % 86400000)
        return () => window.clearTimeout(timer)
    }, [day])
    return day
}

// Page-owned request state, also used by the explicitly opted-in root Storefront views.
// No persistent cache: scope changes hide prior data during render, before effects run.
export function useDashboardSource({ scope, requestKey, load, valid, select, interval, enabled = true }) {
    const [result, setResult] = useState(() => initial(scope))
    const currentScope = useRef(scope)
    currentScope.current = scope
    const { beginRequest, isLatestRequest } = useLatestRequestGuard()
    const fetchSource = useCallback(async () => {
        if (!enabled) return
        const requestId = beginRequest()
        setResult(previous => ({
            ...(previous.scope === scope ? previous : initial(scope)), pending: true,
        }))
        const accepts = () => currentScope.current === scope && isLatestRequest(requestId)
        try {
            const response = await load()
            if (!accepts()) return
            if (!valid(response)) throw new TypeError('dashboard-malformed')
            setResult({ scope, data: select ? select(response) : response, pending: false, error: null, lastSuccess: Date.now() })
        } catch (error) {
            if (!accepts()) return
            const unauthorized = [401, 403].includes(error?.response?.status)
            setResult(previous => ({
                ...(!unauthorized && previous.scope === scope ? previous : initial(scope)),
                pending: false, error: unauthorized ? 'unauthorized' : error instanceof TypeError && error.message === 'dashboard-malformed' ? 'malformed' : 'failed',
            }))
        }
    }, [scope, load, valid, select, beginRequest, isLatestRequest, enabled])

    useEffect(() => {
        void fetchSource()
        const timer = enabled && interval ? window.setInterval(fetchSource, interval) : null
        return () => {
            beginRequest() // Invalidate on period/scope/manual-refresh change and unmount.
            if (timer !== null) window.clearInterval(timer)
        }
    }, [fetchSource, requestKey, interval, beginRequest, enabled])

    return { ...(!enabled ? { ...initial(scope), pending: false, error: 'restricted' }
        : result.scope === scope ? result : initial(scope)), retry: fetchSource }
}

function usePosSource(name, actor, period, refreshKey) {
    const source = POS_SOURCES[name]
    const user = useAuthStore(state => state.user)
    const permission = ['trend', 'products', 'categories', 'payments'].includes(name) ? 'finance.view'
        : name === 'sales' ? 'sales.view' : 'dashboard.view'
    const load = useCallback(async () => (await api.get(source.url(period))).data, [source, period])
    return useDashboardSource({
        scope: `${actor}:${name === 'trend' ? period : name}`,
        requestKey: refreshKey,
        load, valid: source.valid, select: source.select, interval: 60000,
        enabled: hasPosPermission(user, permission),
    })
}

export default function usePosDashboardData(period) {
    const actorScope = useDashboardActorScope()
    const day = useDashboardUtcDay()
    const actor = `${actorScope}:${day}`
    const [refreshKey, setRefreshKey] = useState(0)
    // Independent effects start all seven reads concurrently, just as the former Promise.all.
    const stats = usePosSource('stats', actor, period, refreshKey)
    const trend = usePosSource('trend', actor, period, refreshKey)
    const products = usePosSource('products', actor, period, refreshKey)
    const sales = usePosSource('sales', actor, period, refreshKey)
    const stock = usePosSource('stock', actor, period, refreshKey)
    const categories = usePosSource('categories', actor, period, refreshKey)
    const payments = usePosSource('payments', actor, period, refreshKey)
    return { stats, trend, products, sales, stock, categories, payments, refresh: () => setRefreshKey(key => key + 1) }
}
