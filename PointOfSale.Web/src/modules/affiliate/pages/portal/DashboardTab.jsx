import { useState, useEffect } from 'react'
import { BarChart, Bar, XAxis, YAxis, CartesianGrid, Tooltip, ResponsiveContainer } from 'recharts'
import affiliateApi from '../../../../services/affiliateApi'
import SyntheticQaNotice from './SyntheticQaNotice'
import { useLanguage } from '../../i18n/LanguageContext'
import { PortalPanel, PortalEmpty, SettlementProgress, OrderActivity } from './PortalUi'

export default function DashboardTab({ formatCurrency }) {
    const { t, formatNumber, formatDate } = useLanguage()
    const [state, setState] = useState({ loading: true })
    const [reload, setReload] = useState(0)
    useEffect(() => {
        let active = true
        setState({ loading: true })
        Promise.allSettled([affiliateApi.getDashboard(), affiliateApi.getAnalyticsSummary(), affiliateApi.getAnalyticsTrend(), affiliateApi.getPayouts(1, 1), affiliateApi.getOrders(1, 5)])
            .then(([dashboard, analytics, trend, balance, orders]) => {
                if (active) setState({ loading: false, dashboard, analytics, trend, balance, orders })
            })
        return () => { active = false }
    }, [reload])
    if (state.loading) return <div role="status" aria-label={t('ui.loading')} className="grid sm:grid-cols-3 gap-4">{[0, 1, 2].map(i => <div key={i} className="h-32 rounded-2xl bg-gray-200 dark:bg-white/5 animate-pulse" />)}</div>
    if (state.dashboard.status === 'rejected') return <PortalEmpty><p role="alert">{t('ui.loadError')}</p><button onClick={() => setReload(v => v + 1)}>{t('ui.retry')}</button></PortalEmpty>
    const data = state.dashboard.value
    const analytics = state.analytics.value
    const trend = Array.isArray(state.trend.value) ? state.trend.value : []
    const balance = state.balance.value
    const summaries = data?.earningsByCurrency || []
    return <div className="space-y-6">
        <div><h1 className="text-2xl font-bold">{t('ux.overview')}</h1><p className="text-sm text-gray-500 mt-2">{t('ux.overviewDesc')}</p></div>
        {!summaries.length && <PortalEmpty><p>{t('ui.emptyDashboard')}</p><a href="?tab=discover" className="text-[#6C63FF]">{t('portal.promote')}</a></PortalEmpty>}
        {summaries.map(summary => <PortalPanel key={summary.currency} title={summary.currency}>
            <SyntheticQaNotice amount={summary.syntheticApprovedExcluded} currency={summary.currency} formatCurrency={formatCurrency} />
            <div className="grid grid-cols-2 lg:grid-cols-3 gap-3">{[
                ['totalEarned', summary.totalEarned], ['approved', summary.totalApproved], ['paid', summary.totalPaid], ['reversed', summary.totalReversed], ['netEarnings', summary.netEarnings],
            ].map(([key, amount]) => <div key={key} className="rounded-xl bg-gray-50 dark:bg-white/[0.03] p-4"><p className="text-xs text-gray-500 dark:text-gray-400">{t(`portal.earningsTab.${key}`)}</p><p className="text-xl font-semibold mt-2 tabular-nums">{formatCurrency(amount, summary.currency)}</p></div>)}</div>
            <SettlementProgress summary={summary} />
        </PortalPanel>)}
        <PortalPanel title={t('portal.payouts')} action={<a href="?tab=payouts" className="text-sm text-[#6C63FF]">{t('portal.payouts')}</a>}>
            {balance ? <div className="grid sm:grid-cols-2 gap-4">{[['available', balance.availableForWithdrawal], ['reserved', balance.totalPendingRequests]].map(([key, amount]) => <div key={key}><p className="text-sm text-gray-500">{t(`ux.${key}`)}</p><p className="text-2xl font-semibold mt-2">{formatCurrency(amount, balance.currency)}</p></div>)}</div> : <p role="alert">{t('ux.balanceUnavailable')}</p>}
            <p className="text-xs text-gray-500 dark:text-gray-400 leading-relaxed">{t('ux.earnedNote')}</p>
        </PortalPanel>
        <PortalPanel title={t('ux.analytics')}>
            <p className="text-xs text-gray-500 dark:text-gray-400">{t('ux.analyticsNote')}</p>
            <div className="grid grid-cols-2 lg:grid-cols-4 gap-3">{[
                [t('ux.commissionOrders'), formatNumber(data.totalOrders || 0)],
                [t('ux.clicks'), analytics ? formatNumber(analytics.totalClicks) : '—'],
                [t('ux.trackedOrders'), analytics ? formatNumber(analytics.totalOrders) : '—'],
                [t('ux.conversion'), analytics?.totalClicks > 0 ? formatNumber(analytics.conversionRate / 100, { style: 'percent', maximumFractionDigits: 2 }) : '—'],
            ].map(([label, value]) => <div key={label} className="p-3 rounded-xl border border-gray-200 dark:border-white/10"><p className="text-xs text-gray-500">{label}</p><p className="text-xl font-semibold mt-2">{value}</p></div>)}</div>
            {(state.analytics.status === 'rejected' || state.trend.status === 'rejected') && <p role="alert" className="text-sm text-amber-700 dark:text-amber-300">{t('ux.analyticsUnavailable')} <button onClick={() => setReload(v => v + 1)}>{t('ui.retry')}</button></p>}
            <div className="grid lg:grid-cols-2 gap-5">{['clicks', 'orders'].map(metric => <div key={metric} className="min-w-0">
                <h4 className="text-sm font-medium mb-3">{t(`portal.dashboardStats.${metric}ChartTitle`)}</h4>
                {state.trend.status === 'fulfilled' && trend.some(row => row[metric] > 0) ? <div style={{ height: 220, minWidth: 0 }}>
                    <ResponsiveContainer width="100%" height="100%" minWidth={0} initialDimension={{ width: 500, height: 220 }}><BarChart data={trend} accessibilityLayer>
                        <CartesianGrid strokeDasharray="3 3" stroke="#9ca3af33" />
                        <XAxis dataKey="date" tickFormatter={v => formatDate(v, { month: 'short', day: 'numeric' })} tick={{ fontSize: 11 }} />
                        <YAxis allowDecimals={false} tickFormatter={formatNumber} width={45} tick={{ fontSize: 11 }} />
                        <Tooltip labelFormatter={v => formatDate(v)} formatter={v => formatNumber(v)} />
                        <Bar dataKey={metric} name={t(`portal.dashboardStats.${metric}`)} fill={metric === 'clicks' ? '#6C63FF' : '#059669'} radius={[5, 5, 0, 0]} />
                    </BarChart></ResponsiveContainer>
                </div> : <PortalEmpty><p>{state.trend.status === 'rejected' ? t('ux.analyticsUnavailable') : t(metric === 'clicks' ? 'ux.noClicks' : 'ux.noOrders')}</p><p>{t('ux.noTrendAction')}</p><a href="?tab=links" className="text-[#6C63FF]">{t('portal.myLinks')}</a></PortalEmpty>}
            </div>)}</div>
        </PortalPanel>
        <PortalPanel title={t('ux.recent')} action={<a href="?tab=orders" className="text-sm text-[#6C63FF]">{t('ux.viewOrders')}</a>}><OrderActivity data={state.orders.value} error={state.orders.status === 'rejected'} formatCurrency={formatCurrency} /></PortalPanel>
        <nav aria-label={t('portal.title')} className="flex flex-wrap gap-3">{['orders', 'earnings', 'links', 'payouts'].map(tab => <a key={tab} href={`?tab=${tab}`} className="rounded-xl border border-gray-200 dark:border-white/10 bg-white dark:bg-white/5 px-4 py-3 text-sm font-medium text-[#6C63FF]">{t(`portal.${tab === 'links' ? 'myLinks' : tab}`)}</a>)}</nav>
    </div>
}
