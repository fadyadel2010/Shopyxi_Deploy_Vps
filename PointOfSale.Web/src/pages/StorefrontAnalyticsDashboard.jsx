import { useCallback, useEffect, useMemo, useState } from 'react'
import { useTranslation } from 'react-i18next'
import {
    Activity,
    ArrowDownRight,
    CreditCard,
    RefreshCw,
    ShoppingCart,
    Target,
    TrendingUp,
    Users
} from 'lucide-react'
import { useLatestRequestGuard } from '../hooks/useLatestRequestGuard'
import { useSettings } from '../contexts/SettingsContext'
import storefrontAnalyticsApi from '../services/storefrontAnalyticsApi'
import StorefrontLiveMonitoring from '../components/StorefrontLiveMonitoring'
import { Button, Card } from '../design-system/primitives'
import { useRTL } from '../hooks/useRTL'
import { formatNumber } from '../utils/dateFormat'
import { isNumber, useDashboardActorScope, useDashboardSource, useDashboardUtcDay } from '../components/dashboard/usePosDashboardData'
import DashboardDataState from '../components/dashboard/DashboardDataState'
import DashboardMetric from '../components/dashboard/DashboardMetric'
import DashboardRankedAmounts from '../components/dashboard/DashboardRankedAmounts'

const RANGE_OPTIONS = [
    { value: 'today', label: 'Today' },
    { value: '7d', label: '7D' },
    { value: '30d', label: '30D' },
    { value: 'this_month', label: 'This Month' }
]

const CARD_STYLES = {
    blue: 'from-sky-50 to-blue-100 border-blue-200 text-blue-900',
    emerald: 'from-emerald-50 to-green-100 border-green-200 text-emerald-900',
    amber: 'from-amber-50 to-orange-100 border-orange-200 text-amber-900',
    rose: 'from-rose-50 to-red-100 border-red-200 text-rose-900',
    violet: 'from-violet-50 to-purple-100 border-purple-200 text-violet-900',
    slate: 'from-slate-50 to-gray-100 border-gray-200 text-slate-900'
}

function MetricCard({ label, value, hint, color, icon: Icon }) {
    return (
        <div className={`rounded-2xl border bg-gradient-to-br p-5 shadow-sm ${CARD_STYLES[color]}`}>
            <div className="flex items-start justify-between gap-4">
                <div>
                    <p className="text-sm font-medium text-gray-600">{label}</p>
                    <p className="mt-3 text-3xl font-bold">{value}</p>
                    {hint ? <p className="mt-2 text-sm text-gray-500">{hint}</p> : null}
                </div>
                <div className="rounded-xl bg-white/70 p-3 shadow-sm">
                    <Icon size={22} />
                </div>
            </div>
        </div>
    )
}

function SectionCard({ title, subtitle, children }) {
    return (
        <section className="rounded-2xl border border-gray-200 bg-white p-6 shadow-sm">
            <div className="mb-5 flex items-start justify-between gap-4">
                <div>
                    <h3 className="text-lg font-semibold text-gray-900">{title}</h3>
                    {subtitle ? <p className="mt-1 text-sm text-gray-500">{subtitle}</p> : null}
                </div>
            </div>
            {children}
        </section>
    )
}

const summaryFields = ['visits', 'orders', 'revenue', 'conversionRate', 'addToCart', 'checkoutStarted', 'abandonedCheckouts', 'abandonedRevenue', 'averageOrderValue']
const funnelFields = ['pageView', 'viewProduct', 'addToCart', 'beginCheckout', 'purchaseCompleted']
const validRootSummary = value => value && summaryFields.every(key => isNumber(value[key]))
    && value.funnel && funnelFields.every(key => isNumber(value.funnel[key]))
    && Array.isArray(value.topSellingProducts)
    && value.topSellingProducts.every(row => row && typeof row.productName === 'string' && row.productName.trim() && isNumber(row.sales))

export default function StorefrontAnalyticsDashboard({ presentation } = {}) {
    const actor = useDashboardActorScope()
    const day = useDashboardUtcDay()
    return presentation === 'dashboard' ? <RootStorefrontAnalytics /> : <DefaultStorefrontAnalyticsDashboard key={`${actor}:${day}`} />
}

function DefaultStorefrontAnalyticsDashboard() {
    const { t } = useTranslation()
    const { formatCurrency } = useSettings()

    const [range, setRange] = useState('7d')
    // A successful response is stored with the exact range that produced it, so a
    // figure can never be presented under a different range's label.
    const [result, setResult] = useState({ range: null, payload: null })
    const [loading, setLoading] = useState(true)
    const [errorKind, setErrorKind] = useState(null)
    const { beginRequest, isLatestRequest } = useLatestRequestGuard()

    const fetchDashboard = useCallback(async (silent = false) => {
        const requestId = beginRequest()
        const requestedRange = range

        try {
            if (!silent) {
                setLoading(true)
            }

            setErrorKind(null)
            const data = await storefrontAnalyticsApi.getStorefrontAnalyticsDashboard(requestedRange)

            if (!isLatestRequest(requestId)) {
                return
            }

            if (!validRootSummary(data)) {
                setErrorKind('malformed')
                return
            }

            setResult({ range: requestedRange, payload: data })
        } catch (err) {
            if (!isLatestRequest(requestId)) {
                return
            }

            const status = err?.response?.status
            if (status === 401 || status === 403) setResult({ range: null, payload: null })
            console.error('[Storefront Analytics] Failed to fetch dashboard:', { status })
            setErrorKind(status === 401 || status === 403 ? 'unauthorized' : 'failed')
        } finally {
            if (isLatestRequest(requestId)) {
                setLoading(false)
            }
        }
    }, [beginRequest, isLatestRequest, range])

    useEffect(() => {
        void fetchDashboard()
    }, [fetchDashboard])

    // Only the selected range's own successful response is displayable.
    const dashboard = result.range === range ? result.payload : null
    const topProducts = dashboard?.topSellingProducts || []
    const funnel = useMemo(() => dashboard?.funnel || {}, [dashboard?.funnel])
    const funnelSteps = useMemo(() => ([
        { key: 'pageView', label: t('storefrontAnalytics.pageView', 'Page View'), count: funnel.pageView || 0 },
        { key: 'viewProduct', label: t('storefrontAnalytics.viewProduct', 'View Product'), count: funnel.viewProduct || 0 },
        { key: 'addToCart', label: t('storefrontAnalytics.addToCart', 'Add To Cart'), count: funnel.addToCart || 0 },
        { key: 'beginCheckout', label: t('storefrontAnalytics.beginCheckout', 'Begin Checkout'), count: funnel.beginCheckout || 0 },
        { key: 'purchaseCompleted', label: t('storefrontAnalytics.purchaseCompleted', 'Purchase Completed'), count: funnel.purchaseCompleted || 0 }
    ]), [funnel, t])

    const maxProductSales = Math.max(...topProducts.map(product => product.sales), 0)
    const maxFunnelCount = Math.max(...funnelSteps.map(step => step.count), 0)
    const hasDashboard = dashboard !== null
    const stateMessageKey = errorKind === 'unauthorized'
        ? 'accessDenied'
        : errorKind === 'malformed'
            ? 'malformed'
            : 'unavailableTitle'

    return (
        <div className="space-y-6">
            <div className="flex flex-col gap-4 lg:flex-row lg:items-center lg:justify-between">
                <div>
                    <h2 className="text-3xl font-bold text-gray-900">
                        {t('storefrontAnalytics.title', 'Storefront Analytics')}
                    </h2>
                    <p className="mt-1 text-gray-600">
                        {t('storefrontAnalytics.subtitle', 'A dedicated storefront dashboard built from existing analytics, order, and checkout data.')}
                    </p>
                </div>

                <div className="flex flex-wrap items-center gap-2">
                    <div role="group" aria-label={t('dashboard.phase42.storefront.range')} className="inline-flex max-w-full flex-wrap rounded-xl border border-gray-200 bg-white p-1 shadow-sm">
                        {RANGE_OPTIONS.map(option => (
                            <button
                                key={option.value}
                                aria-pressed={range === option.value}
                                onClick={() => setRange(option.value)}
                                className={`rounded-lg px-4 py-2 text-sm font-medium transition-colors ${range === option.value
                                    ? 'bg-indigo-600 text-white shadow-sm'
                                    : 'text-gray-600 hover:bg-gray-50'
                                    }`}
                            >
                                {t(`storefrontAnalytics.ranges.${option.value}`, option.label)}
                            </button>
                        ))}
                    </div>

                    <button
                        onClick={() => fetchDashboard()}
                        disabled={loading}
                        className="inline-flex items-center gap-2 rounded-xl border border-gray-200 bg-white px-4 py-2 text-sm font-medium text-gray-700 shadow-sm transition-colors hover:bg-gray-50 disabled:opacity-60"
                    >
                        <RefreshCw size={16} className={loading ? 'animate-spin' : ''} />
                        {t('common.refresh', 'Refresh')}
                    </button>
                </div>
            </div>

            {errorKind && hasDashboard ? (
                <div className="rounded-2xl border border-red-200 bg-red-50 p-4 text-sm text-red-700" role="status">
                    <p className="font-medium">{t(`storefrontAnalytics.${stateMessageKey}`, 'Storefront analytics unavailable')}</p>
                    <p className="mt-1">{t('storefrontAnalytics.staleResult', 'Showing the last successful result for this range.')}</p>
                    <button
                        type="button"
                        onClick={() => fetchDashboard()}
                        disabled={loading}
                        className="mt-3 rounded-lg border border-red-300 bg-white px-3 py-1.5 text-sm font-medium text-red-700 disabled:opacity-60"
                    >
                        {t('common.retry', 'Retry')}
                    </button>
                </div>
            ) : null}

            <StorefrontLiveMonitoring />

            {hasDashboard ? (
                <>
                    <div className="grid grid-cols-1 gap-4 md:grid-cols-2 xl:grid-cols-4">
                <MetricCard
                    label={t('storefrontAnalytics.visits', 'Visits')}
                    value={(dashboard?.visits || 0).toLocaleString()}
                    hint={t('storefrontAnalytics.uniqueSessions', 'Unique sessions from storefront page views')}
                    color="blue"
                    icon={Users}
                />
                <MetricCard
                    label={t('storefrontAnalytics.orders', 'Orders')}
                    value={(dashboard?.orders || 0).toLocaleString()}
                    hint={t('storefrontAnalytics.storefrontOrders', 'Storefront-origin orders only')}
                    color="emerald"
                    icon={ShoppingCart}
                />
                <MetricCard
                    label={t('storefrontAnalytics.revenue', 'Revenue')}
                    value={formatCurrency(dashboard?.revenue || 0)}
                    hint={isNumber(dashboard.recognizedRevenue) && isNumber(dashboard.revenueAdjustments)
                        ? t('storefrontAnalytics.revenueBreakdown', {
                            recognized: formatCurrency(dashboard.recognizedRevenue),
                            adjustments: formatCurrency(dashboard.revenueAdjustments),
                        }) : t('storefrontAnalytics.grossRevenue')}
                    color="amber"
                    icon={TrendingUp}
                />
                <MetricCard
                    label={t('storefrontAnalytics.conversionRate', 'Conversion Rate')}
                    value={`${(dashboard?.conversionRate || 0).toFixed(2)}%`}
                    hint={t('storefrontAnalytics.visitsToOrders', 'Orders divided by unique visits')}
                    color="violet"
                    icon={Target}
                />
            </div>

            <div className="grid grid-cols-1 gap-4 md:grid-cols-2 xl:grid-cols-4">
                <MetricCard
                    label={t('storefrontAnalytics.addToCart', 'Add To Cart')}
                    value={(dashboard?.addToCart || 0).toLocaleString()}
                    hint={t('storefrontAnalytics.cartIntent', 'Storefront intent before checkout')}
                    color="slate"
                    icon={Activity}
                />
                <MetricCard
                    label={t('storefrontAnalytics.checkoutStarted', 'Checkout Started')}
                    value={(dashboard?.checkoutStarted || 0).toLocaleString()}
                    hint={t('storefrontAnalytics.checkoutIntent', 'BeginCheckout analytics events')}
                    color="blue"
                    icon={CreditCard}
                />
                <MetricCard
                    label={t('storefrontAnalytics.abandonedOrders', 'Abandoned Orders')}
                    value={(dashboard?.abandonedCheckouts || 0).toLocaleString()}
                    hint={`${t('storefrontAnalytics.abandonedRevenue', 'Lost revenue')}: ${formatCurrency(dashboard?.abandonedRevenue || 0)}`}
                    color="rose"
                    icon={ArrowDownRight}
                />
                <MetricCard
                    label={t('storefrontAnalytics.averageOrderValue', 'Average Order Value')}
                    value={formatCurrency(dashboard?.averageOrderValue || 0)}
                    hint={t('storefrontAnalytics.revenuePerOrder', 'Revenue divided by storefront orders')}
                    color="emerald"
                    icon={TrendingUp}
                />
            </div>

            <SectionCard
                title={t('storefrontAnalytics.topSellingProducts', 'Top Selling Products')}
                subtitle={t('storefrontAnalytics.topSellingProductsSubtitle', 'Top 5 storefront products ranked by quantity sold.')}
            >
                {topProducts.length === 0 ? (
                    <div className="rounded-xl border border-dashed border-gray-200 bg-gray-50 px-6 py-10 text-center text-sm text-gray-500">
                        {t('storefrontAnalytics.noTopProducts', 'No storefront product sales found for this range.')}
                    </div>
                ) : (
                    <div className="space-y-4">
                        {topProducts.map((product, index) => {
                            const width = maxProductSales > 0 && product.sales > 0 ? Math.max((product.sales / maxProductSales) * 100, 8) : 0

                            return (
                                <div key={`${product.productName}-${index}`} className="rounded-xl border border-gray-100 bg-gray-50 p-4">
                                    <div className="mb-3 flex items-center justify-between gap-4">
                                        <div>
                                            <p className="text-sm font-semibold text-gray-900">{product.productName}</p>
                                            <p className="text-xs text-gray-500">
                                                {t('storefrontAnalytics.unitsSold', 'Units sold')}: {product.sales.toLocaleString()}
                                            </p>
                                        </div>
                                        <div className="rounded-lg bg-white px-3 py-2 text-sm font-semibold text-gray-900 shadow-sm">
                                            #{index + 1}
                                        </div>
                                    </div>
                                    <div className="h-3 rounded-full bg-white">
                                        <div
                                            className="h-3 rounded-full bg-gradient-to-r from-indigo-500 to-cyan-400"
                                            style={{ width: `${width}%` }}
                                        />
                                    </div>
                                </div>
                            )
                        })}
                    </div>
                )}
            </SectionCard>

            <SectionCard
                title={t('storefrontAnalytics.funnelOverview', 'Funnel Overview')}
                subtitle={t('storefrontAnalytics.funnelOverviewSubtitle', 'Simplified purchase funnel using the existing analytics funnel steps.')}
            >
                <div className="grid grid-cols-1 gap-4 xl:grid-cols-5">
                    {funnelSteps.map((step, index) => {
                        const width = maxFunnelCount > 0 && step.count > 0 ? Math.max((step.count / maxFunnelCount) * 100, 6) : 0

                        return (
                            <div key={step.key} className="rounded-xl border border-gray-100 bg-gray-50 p-4">
                                <div className="mb-3 flex items-center justify-between">
                                    <span className="text-xs font-semibold uppercase tracking-wide text-gray-400">
                                        {index + 1}
                                    </span>
                                    <span className="text-lg font-bold text-gray-900">
                                        {step.count.toLocaleString()}
                                    </span>
                                </div>
                                <p className="mb-3 text-sm font-semibold text-gray-900">{step.label}</p>
                                <div className="h-2 rounded-full bg-white">
                                    <div
                                        className="h-2 rounded-full bg-gradient-to-r from-emerald-500 to-indigo-500"
                                        style={{ width: `${width}%` }}
                                    />
                                </div>
                            </div>
                        )
                    })}
                </div>
            </SectionCard>
                </>
            ) : loading ? (
                <div className="flex h-80 items-center justify-center">
                    <div className="text-center">
                        <div className="mx-auto h-12 w-12 animate-spin rounded-full border-b-2 border-indigo-600"></div>
                        <p className="mt-4 text-gray-600">{t('common.loading', 'Loading...')}</p>
                    </div>
                </div>
            ) : (
                <div className="rounded-2xl border border-red-200 bg-red-50 p-6 text-center text-sm text-red-700" role="status">
                    <p className="text-lg font-semibold">{t(`storefrontAnalytics.${stateMessageKey}`, 'Storefront analytics unavailable')}</p>
                    <p className="mt-2">
                        {errorKind === 'unauthorized'
                            ? t('storefrontAnalytics.accessDeniedMessage', 'You do not have permission to view storefront analytics.')
                            : errorKind === 'malformed'
                                ? t('storefrontAnalytics.malformedMessage', 'The analytics response was unusable, so no figures are shown. This is not a zero result.')
                                : t('storefrontAnalytics.unavailableMessage', 'We could not load storefront analytics. No figures are shown, and this is not a zero result.')}
                    </p>
                    <button
                        type="button"
                        onClick={() => fetchDashboard()}
                        disabled={loading}
                        className="mt-4 rounded-lg border border-red-300 bg-white px-4 py-2 font-medium text-red-700 disabled:opacity-60"
                    >
                        {t('common.retry', 'Retry')}
                    </button>
                </div>
            )}
        </div>
    )
}

function RootStorefrontAnalytics() {
    const { t } = useTranslation()
    const { locale } = useRTL()
    const { formatCurrency } = useSettings()
    const actor = useDashboardActorScope()
    const day = useDashboardUtcDay()
    const [range, setRange] = useState('7d')
    const load = useCallback(() => storefrontAnalyticsApi.getStorefrontAnalyticsDashboard(range), [range])
    const source = useDashboardSource({ scope: `${actor}:${day}:${range}`, load, valid: validRootSummary })
    const summary = source.data
    const copy = key => t(`dashboard.phase42.storefront.${key}`)
    const number = value => formatNumber(value, locale)
    const metrics = [
        ['visits', 'visits', 'visitsBasis'],
        ['orders', 'orders', 'ordersBasis'],
        ['revenue', 'revenue', 'revenueBasis', formatCurrency],
        ['conversionRate', 'conversion', 'conversionBasis', value => `${number(value)}%`],
        ['addToCart', 'addToCart', 'cartBasis'],
        ['checkoutStarted', 'checkoutStarted', 'checkoutBasis'],
        ['abandonedCheckouts', 'abandonedCheckouts', 'abandonedBasis'],
        ['averageOrderValue', 'averageOrderValue', 'averageBasis', formatCurrency],
    ]
    return (
        <div className="pos-dashboard__storefront">
            <div className="pos-dashboard__toolbar">
                <div>
                    <h2>{t('storefrontAnalytics.title', 'Storefront Analytics')}</h2>
                    <p className="pos-dashboard__caption">{copy('summaryBasis')}</p>
                    <div className="pos-dashboard__actions" role="group" aria-label={copy('range')}>
                        {RANGE_OPTIONS.map(option => <Button key={option.value} variant={range === option.value ? 'primary' : 'secondary'} aria-pressed={range === option.value} onClick={() => setRange(option.value)}>{copy(`ranges.${option.value}`)}</Button>)}
                    </div>
                </div>
                <Button variant="secondary" disabled={source.pending} onClick={source.retry}>{t('common.refresh')}</Button>
            </div>
            <DashboardDataState source={source}>
                <div className="pos-dashboard__kpis">
                    {metrics.map(([field, label, basis, formatter = number]) => <DashboardMetric key={field} label={copy(label)} value={summary ? formatter(summary[field]) : undefined} caption={copy(basis)}>
                        {field === 'revenue' && isNumber(summary?.recognizedRevenue) && isNumber(summary?.revenueAdjustments) ? <p className="pos-dashboard__caption">{t('storefrontAnalytics.revenueBreakdown', {
                            recognized: formatCurrency(summary.recognizedRevenue),
                            adjustments: formatCurrency(summary.revenueAdjustments),
                        })}</p> : null}
                        {field === 'abandonedCheckouts' && summary ? <p className="pos-dashboard__caption">{copy('cartValue')}: <bdi>{formatCurrency(summary.abandonedRevenue)}</bdi></p> : null}
                    </DashboardMetric>)}
                </div>
            </DashboardDataState>
            {/* Independent five-minute scope and 10-second refresh; never tied to summary range. */}
            <StorefrontLiveMonitoring presentation="dashboard" key={actor} />
            {summary ? (
                <div className="pos-dashboard__pair">
                    <Card as="section" className="pos-dashboard__panel">
                        <h3>{copy('productsTitle')}</h3>
                        <p className="pos-dashboard__caption">{copy('productsBasis')}</p>
                        {summary?.topSellingProducts.length ? <DashboardRankedAmounts data={summary.topSellingProducts.map(row => ({ label: row.productName, value: row.sales }))} caption={copy('productsBasis')} labelHeading={t('dashboard.product')} valueHeading={copy('units')} formatValue={number} /> : <p className="pos-dashboard__empty">{t('dashboard.phase42.empty')}</p>}
                    </Card>
                    <Card as="section" className="pos-dashboard__panel">
                        <h3>{copy('activityCounts')}</h3>
                        <p className="pos-dashboard__caption">{copy('funnelBasis')}</p>
                        <DashboardRankedAmounts data={funnelFields.map(key => ({ label: copy(`steps.${key}`), value: summary?.funnel[key] }))} caption={copy('activityCounts')} labelHeading={t('dashboard.phase42.name')} valueHeading={copy('count')} formatValue={number} />
                    </Card>
                </div>
            ) : null}
        </div>
    )
}
