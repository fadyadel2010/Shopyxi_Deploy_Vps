import { useCallback, useEffect, useState } from 'react'
import { useTranslation } from 'react-i18next'
import { CreditCard, Eye, Package, RefreshCw, ShoppingCart } from 'lucide-react'
import { useLatestRequestGuard } from '../hooks/useLatestRequestGuard'
import storefrontAnalyticsApi from '../services/storefrontAnalyticsApi'
import { Button, Card } from '../design-system/primitives'
import { useRTL } from '../hooks/useRTL'
import { formatDateTime, formatNumber } from '../utils/dateFormat'
import { isNumber, useDashboardActorScope, useDashboardSource } from './dashboard/usePosDashboardData'
import DashboardDataState from './dashboard/DashboardDataState'
import DashboardMetric from './dashboard/DashboardMetric'

const POLL_INTERVAL_MS = 10000

const LIVE_CARD_STYLES = {
    cyan: 'from-cyan-50 to-sky-100 border-cyan-200 text-cyan-900',
    amber: 'from-amber-50 to-orange-100 border-amber-200 text-amber-900',
    indigo: 'from-indigo-50 to-blue-100 border-indigo-200 text-indigo-900',
    emerald: 'from-emerald-50 to-green-100 border-emerald-200 text-emerald-900'
}

function LiveMetricCard({ label, value, icon: Icon, color }) {
    return (
        <div className={`rounded-2xl border bg-gradient-to-br p-5 shadow-sm ${LIVE_CARD_STYLES[color]}`}>
            <div className="flex items-center justify-between gap-4">
                <div>
                    <p className="text-sm font-medium text-gray-600">{label}</p>
                    <p className="mt-3 text-3xl font-bold">{value}</p>
                </div>
                <div className="rounded-xl bg-white/70 p-3 shadow-sm">
                    <Icon size={22} />
                </div>
            </div>
        </div>
    )
}

function formatRelativeTime(occurredAtUtc, locale) {
    const seconds = Math.max(0, Math.floor((Date.now() - new Date(occurredAtUtc).getTime()) / 1000))
    const formatter = new Intl.RelativeTimeFormat(locale, { numeric: 'always', style: 'short' })
    if (seconds < 60) return formatter.format(-seconds, 'second')
    const minutes = Math.floor(seconds / 60)
    if (minutes < 60) return formatter.format(-minutes, 'minute')
    const hours = Math.floor(minutes / 60)
    return formatter.format(-hours, 'hour')
}

function getActivityLabel(activity, t) {
    switch (activity.type) {
        case 'AddToCart':
            return t('storefrontLive.activity.addToCart', 'Added to cart')
        case 'BeginCheckout':
            return t('storefrontLive.activity.beginCheckout', 'Checkout started')
        case 'OrderCompleted':
            return t('storefrontLive.activity.orderCompleted', 'Order completed')
        default:
            return activity.type
    }
}

export default function StorefrontLiveMonitoring({ presentation } = {}) {
    const actor = useDashboardActorScope()
    return presentation === 'dashboard' ? <RootStorefrontLiveMonitoring /> : <DefaultStorefrontLiveMonitoring key={actor} />
}

const validLivePayload = value => value && ['liveVisitors', 'activeCarts', 'checkoutInProgress', 'recentOrders'].every(key => isNumber(value[key]))
    && Array.isArray(value.recentActivity)
    && value.recentActivity.every(row => row && typeof row.type === 'string' && typeof row.occurredAtUtc === 'string' && Number.isFinite(Date.parse(row.occurredAtUtc)))

function DefaultStorefrontLiveMonitoring() {
    const { t } = useTranslation()
    const { locale } = useRTL()
    const [data, setData] = useState(null)
    const [loading, setLoading] = useState(true)
    // Only a validated successful response may replace the counters; a failed or
    // malformed response is never presented as a healthy grid of zeros.
    const [errorKind, setErrorKind] = useState(null)
    const [error, setError] = useState(null)
    const { beginRequest, isLatestRequest } = useLatestRequestGuard()

    const fetchLiveMonitoring = useCallback(async (silent = false) => {
        const requestId = beginRequest()

        try {
            if (!silent) {
                setLoading(true)
            }

            setErrorKind(null)
            setError(null)
            const response = await storefrontAnalyticsApi.getStorefrontLiveMonitoring()

            if (!isLatestRequest(requestId)) {
                return
            }

            if (!validLivePayload(response)) {
                setErrorKind('malformed')
                return
            }

            setData(response)
        } catch (err) {
            if (!isLatestRequest(requestId)) {
                return
            }

            const status = err?.response?.status
            if (status === 401 || status === 403) setData(null)
            console.error('[Storefront Live] Failed to fetch live monitoring:', { status })
            // Server messages may contain technical details; only localized copy is shown.
            setError(null)
            setErrorKind('failed')
        } finally {
            if (isLatestRequest(requestId)) {
                setLoading(false)
            }
        }
    }, [beginRequest, isLatestRequest])

    useEffect(() => {
        void fetchLiveMonitoring()

        const intervalId = window.setInterval(() => {
            void fetchLiveMonitoring(true)
        }, POLL_INTERVAL_MS)

        return () => {
            window.clearInterval(intervalId)
        }
    }, [fetchLiveMonitoring])

    const hasData = data !== null
    const malformed = errorKind === 'malformed'

    return (
        <section className="rounded-2xl border border-gray-200 bg-white p-6 shadow-sm">
            <div className="mb-5 flex flex-col gap-3 sm:flex-row sm:items-center sm:justify-between">
                <div>
                    <h3 className="text-lg font-semibold text-gray-900">
                        {t('storefrontLive.title', 'Live Store Monitoring')}
                    </h3>
                    <p className="mt-1 text-sm text-gray-500">
                        {t('storefrontLive.subtitle', 'Real-time storefront activity from the last 5 minutes.')}
                    </p>
                </div>

                <button
                    onClick={() => fetchLiveMonitoring()}
                    disabled={loading}
                    className="inline-flex items-center gap-2 rounded-xl border border-gray-200 bg-white px-4 py-2 text-sm font-medium text-gray-700 transition-colors hover:bg-gray-50 disabled:opacity-60"
                >
                    <RefreshCw size={16} className={loading ? 'animate-spin' : ''} />
                    {t('common.refresh', 'Refresh')}
                </button>
            </div>

            {errorKind && hasData ? (
                <div className="mb-5 rounded-xl border border-red-200 bg-red-50 p-4 text-sm text-red-700" role="status">
                    <p className="font-medium">{malformed
                        ? t('storefrontLive.malformed', 'Live monitoring data was unusable')
                        : t('storefrontLive.unavailableTitle', 'Live store monitoring unavailable')}</p>
                    <p className="mt-1">{t('storefrontLive.stale', 'Showing the last successful result from an earlier refresh.')}</p>
                </div>
            ) : null}

            {!hasData ? (
                <div className="rounded-2xl border border-red-200 bg-red-50 p-6 text-center text-sm text-red-700" role="status">
                    {errorKind ? (
                        <>
                            <p className="text-lg font-semibold">{malformed
                                ? t('storefrontLive.malformed', 'Live monitoring data was unusable')
                                : t('storefrontLive.unavailableTitle', 'Live store monitoring unavailable')}</p>
                            <p className="mt-2">{malformed
                                ? t('storefrontLive.malformedMessage', 'The live monitoring response was unusable, so no counters are shown. This is not a zero result.')
                                : t('storefrontLive.unavailableMessage', 'We could not load live store monitoring. No counters are shown, and this is not a zero result.')}</p>
                            {error && !malformed ? <p className="mt-2 text-xs">{error}</p> : null}
                            <button
                                type="button"
                                onClick={() => fetchLiveMonitoring()}
                                disabled={loading}
                                className="mt-4 rounded-lg border border-red-300 bg-white px-4 py-2 font-medium text-red-700 disabled:opacity-60"
                            >
                                {t('common.retry', 'Retry')}
                            </button>
                        </>
                    ) : (
                        <p className="text-gray-600">{t('common.loading', 'Loading...')}</p>
                    )}
                </div>
            ) : (
                <>
            <div className="grid grid-cols-1 gap-4 md:grid-cols-2 xl:grid-cols-4">
                <LiveMetricCard
                    label={t('storefrontLive.liveVisitors', 'Live Visitors')}
                    value={data.liveVisitors.toLocaleString()}
                    icon={Eye}
                    color="cyan"
                />
                <LiveMetricCard
                    label={t('storefrontLive.activeCarts', 'Active Carts')}
                    value={data.activeCarts.toLocaleString()}
                    icon={ShoppingCart}
                    color="amber"
                />
                <LiveMetricCard
                    label={t('storefrontLive.checkoutInProgress', 'Checkout In Progress')}
                    value={data.checkoutInProgress.toLocaleString()}
                    icon={CreditCard}
                    color="indigo"
                />
                <LiveMetricCard
                    label={t('storefrontLive.recentOrders', 'Orders in Last 5 Minutes')}
                    value={data.recentOrders.toLocaleString()}
                    icon={Package}
                    color="emerald"
                />
            </div>

            <div className="mt-6 rounded-2xl border border-gray-100 bg-gray-50 p-5">
                <div className="mb-4 flex items-center justify-between gap-4">
                    <div>
                        <h4 className="text-sm font-semibold text-gray-900">
                            {t('storefrontLive.recentActivity', 'Recent Activity')}
                        </h4>
                        <p className="mt-1 text-xs text-gray-500">
                            {t('storefrontLive.recentActivitySubtitle', 'Latest storefront checkout and cart events.')}
                        </p>
                    </div>
                    <div className="text-xs text-gray-400">
                        {t('storefrontLive.polling', 'Updates every 10 seconds')}
                    </div>
                </div>

                {!data.recentActivity.length ? (
                    <div className="rounded-xl border border-dashed border-gray-200 bg-white px-4 py-8 text-center text-sm text-gray-500">
                        {t('storefrontLive.noRecentActivity', 'No recent storefront activity in the live window.')}
                    </div>
                ) : (
                    <div className="space-y-3">
                        {data.recentActivity.map((activity, index) => (
                            <div
                                key={`${activity.type}-${activity.occurredAtUtc}-${index}`}
                                className="flex items-center justify-between gap-4 rounded-xl border border-white bg-white px-4 py-3 shadow-sm"
                            >
                                <div className="min-w-0">
                                    <p className="text-sm font-semibold text-gray-900">
                                        {getActivityLabel(activity, t)}
                                    </p>
                                    <p className="truncate text-xs text-gray-500">
                                        {activity.product || activity.orderNumber || t('storefrontLive.generalActivity', 'Storefront session activity')}
                                    </p>
                                </div>
                                <div className="whitespace-nowrap text-xs font-medium text-gray-400">
                                    {formatRelativeTime(activity.occurredAtUtc, locale)}
                                </div>
                            </div>
                        ))}
                    </div>
                )}
            </div>
                </>
            )}
        </section>
    )
}

const validRootLive = validLivePayload
const loadRootLive = () => storefrontAnalyticsApi.getStorefrontLiveMonitoring()

function RootStorefrontLiveMonitoring() {
    const { t } = useTranslation()
    const { locale } = useRTL()
    const actor = useDashboardActorScope()
    const source = useDashboardSource({ scope: actor, load: loadRootLive, valid: validRootLive, interval: POLL_INTERVAL_MS })
    const copy = key => t(`dashboard.phase42.storefront.${key}`)
    const activityLabels = { AddToCart: 'addToCart', BeginCheckout: 'checkoutStarted', OrderCompleted: 'orderCompleted' }
    return (
        <Card as="section" className="pos-dashboard__live">
            <div className="pos-dashboard__section-heading">
                <div><h3>{copy('liveTitle')}</h3><p className="pos-dashboard__caption">{copy('liveBasis')}</p></div>
                <Button variant="secondary" disabled={source.pending} onClick={source.retry}>{t('common.refresh')}</Button>
            </div>
            <DashboardDataState source={source}>
                <div className="pos-dashboard__kpis">
                    {['liveVisitors', 'activeCarts', 'checkoutInProgress', 'recentOrders'].map(key => <DashboardMetric key={key} label={copy(key)} value={source.data ? formatNumber(source.data[key], locale) : undefined} caption={copy(`${key}Basis`)} />)}
                </div>
                <h4>{copy('recentActivity')}</h4>
                {source.data?.recentActivity.length ? <ul className="pos-dashboard__activity">
                    {source.data.recentActivity.map((activity, index) => <li key={index}>
                        <strong>{activityLabels[activity.type] ? copy(activityLabels[activity.type]) : <bdi>{activity.type}</bdi>}</strong>
                        <p><bdi>{activity.product || activity.orderNumber || copy('sessionActivity')}</bdi></p>
                        <time dateTime={activity.occurredAtUtc}><bdi>{formatDateTime(activity.occurredAtUtc, locale)}</bdi></time>
                    </li>)}
                </ul> : <p className="pos-dashboard__empty">{copy('noActivity')}</p>}
            </DashboardDataState>
        </Card>
    )
}
