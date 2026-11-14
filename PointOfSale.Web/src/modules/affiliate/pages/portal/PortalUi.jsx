import { useLanguage } from '../../i18n/LanguageContext'
import { useState } from 'react'

export function PortalProductImage({ src, alt }) {
    const { t } = useLanguage()
    const [failed, setFailed] = useState(null)
    return src && failed !== src ? <img src={src} alt={alt} className="w-full h-full object-contain" onError={() => setFailed(src)} />
        : <div role="img" aria-label={t('ux.mediaUnavailable')} className="w-full h-full flex flex-col items-center justify-center gap-2 text-gray-500"><span aria-hidden="true" className="text-3xl">▧</span><span className="text-xs">{t('ux.mediaUnavailable')}</span></div>
}

export function PortalPanel({ title, children, action }) {
    return <section className="rounded-2xl border border-gray-200 dark:border-white/10 bg-white dark:bg-white/[0.02] p-5 sm:p-6 space-y-4">
        {title && <div className="flex flex-wrap items-center justify-between gap-3"><h3 className="font-semibold text-gray-900 dark:text-white">{title}</h3>{action}</div>}
        {children}
    </section>
}

export function PortalEmpty({ children }) {
    return <div className="rounded-xl border border-dashed border-gray-300 dark:border-white/20 bg-gray-50 dark:bg-white/[0.02] p-6 text-sm text-gray-600 dark:text-gray-400 text-center space-y-3">{children}</div>
}

export function SettlementProgress({ summary }) {
    const { t, formatNumber } = useLanguage()
    // A visualization ratio only, never a financial balance or eligibility calculation.
    const rawRatio = summary.totalEarned > 0 && Number.isFinite(summary.totalPaid)
        ? summary.totalPaid / summary.totalEarned * 100 : 0
    const ratio = Math.max(0, Math.min(100, rawRatio))
    const inconsistent = rawRatio < 0 || rawRatio > 100 || (summary.totalEarned <= 0 && summary.totalPaid > 0)
    return <div className="space-y-2">
        <div className="flex justify-between gap-3 text-sm"><span>{t('ux.paidProgress')}</span><strong>{summary.totalEarned <= 0 && summary.totalPaid > 0 ? '—' : formatNumber(rawRatio / 100, { style: 'percent', maximumFractionDigits: 1 })}</strong></div>
        <div role="progressbar" aria-label={`${t('ux.paidProgress')} (${summary.currency})`} aria-valuenow={ratio} aria-valuemin={0} aria-valuemax={100} className="h-2.5 rounded-full overflow-hidden bg-gray-200 dark:bg-white/10">
            <div data-testid="paid-progress-fill" className="h-full bg-emerald-600 rounded-full" style={{ width: `${ratio}%` }} />
        </div>
        <p className="text-xs text-gray-500 dark:text-gray-400 leading-relaxed">{t('ux.progressNote')}</p>
        {inconsistent && <p role="alert" className="text-sm text-amber-700 dark:text-amber-300">{t('ux.progressInconsistent')}</p>}
    </div>
}

export function CommissionStatus({ status }) {
    const { t } = useLanguage()
    const special = { AttributedPendingPayment: 'ux.attributed', PendingPayment: 'ux.pendingPayment', OnHold: 'ux.onHold', Delivered: 'ux.delivered' }
    const key = special[status] || `portal.ordersTab.status${status}`
    const translated = t(key)
    const tone = ['Paid', 'Approved'].includes(status) ? 'text-emerald-700 bg-emerald-50 dark:text-emerald-300 dark:bg-emerald-500/10'
        : ['Reversed', 'PartiallyReversed', 'Cancelled', 'Refunded'].includes(status) ? 'text-red-700 bg-red-50 dark:text-red-300 dark:bg-red-500/10'
            : 'text-amber-800 bg-amber-50 dark:text-amber-300 dark:bg-amber-500/10'
    return <span className={`inline-flex rounded-full px-2.5 py-1 text-xs font-medium ${tone}`}>{translated !== key ? translated : status || t('ux.unknown')}</span>
}

export function OrderActivity({ data, error, formatCurrency }) {
    const { t, formatDate } = useLanguage()
    if (error) return <p role="alert" className="text-sm text-amber-700 dark:text-amber-300">{t('ux.activityUnavailable')}</p>
    if (!data?.orders?.length) return <PortalEmpty>{t('portal.ordersTab.noOrders')}</PortalEmpty>
    return <ul className="divide-y divide-gray-100 dark:divide-white/10">{data.orders.map(order => <li key={order.orderId} className="py-3 flex flex-wrap items-center justify-between gap-3">
        <div className="min-w-0"><a className="font-medium text-sm text-[#6C63FF]" href="?tab=orders">{order.orderNumber}</a><p className="text-xs text-gray-500 mt-1">{formatDate(order.createdAtUtc)}</p>
            {order.isSyntheticQa && <p className="text-xs text-amber-700 dark:text-amber-300">{t('portal.syntheticQa.label')} — {t('portal.syntheticQa.excluded')}</p>}
        </div>
        <div className="flex flex-wrap items-center gap-3"><strong className="text-sm">{formatCurrency(order.totalCommission, order.currency)}</strong><CommissionStatus status={order.status} /></div>
    </li>)}</ul>
}
