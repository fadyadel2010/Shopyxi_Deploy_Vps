import { useEffect, useState } from 'react'
import affiliateApi from '../../../../services/affiliateApi'
import { useLanguage } from '../../i18n/LanguageContext'
import { CommissionStatus, PortalPanel } from './PortalUi'
import CanonicalOrderDetails from './CanonicalOrderDetails'

export function SourceOrder({ orderId, formatCurrency }) {
    const { t } = useLanguage()
    const [state, setState] = useState({ loading: true })
    const [reload, setReload] = useState(0)
    useEffect(() => {
        let active = true
        setState({ loading: true })
        affiliateApi.getSourceOrder(orderId).then(order => { if (active) setState({ order }) })
            .catch(() => { if (active) setState({ error: true }) })
        return () => { active = false }
    }, [orderId, reload])
    return <PortalPanel title={t('ux.readModel.sourceOrder')}>
        {state.loading ? <p role="status">{t('ui.loading')}</p> : state.error ? <div role="alert"><p>{t('ux.readModel.sourceUnavailable')}</p><button onClick={() => setReload(value => value + 1)}>{t('ui.retry')}</button></div> : <CanonicalOrderDetails order={state.order} formatCurrency={formatCurrency} showItems />}
    </PortalPanel>
}

export default function CommissionActivity({ formatCurrency }) {
    const { t, formatDate, formatNumber } = useLanguage()
    const [page, setPage] = useState(1)
    const [reload, setReload] = useState(0)
    const [state, setState] = useState({ loading: true })
    useEffect(() => {
        let active = true
        setState({ loading: true })
        affiliateApi.getCommissionActivity(page, 10).then(data => {
            if (!Array.isArray(data?.entries)) throw new Error('Invalid activity response')
            if (active) setState({ data })
        })
            .catch(() => { if (active) setState({ error: true }) })
        return () => { active = false }
    }, [page, reload])
    const label = key => t(`ux.readModel.${key}`)
    return <PortalPanel title={label('ledgerTitle')}>
        <p className="text-xs text-gray-500 mb-4">{label('ledgerNote')}</p>
        {state.loading ? <p role="status">{t('ui.loading')}</p> : state.error ? <div role="alert"><p>{label('ledgerUnavailable')}</p><button onClick={() => setReload(value => value + 1)}>{t('ui.retry')}</button></div> : !state.data?.entries?.length ? <p>{label('ledgerEmpty')}</p> : <>
            <ol aria-label={label('ledgerTitle')} className="space-y-3">{state.data.entries.map(entry => <li key={entry.entryId} className="rounded-lg border border-gray-200 dark:border-white/10 p-4 space-y-2 min-w-0">
                <div className="flex flex-wrap items-center justify-between gap-3"><CommissionStatus status={entry.entryType} /><strong>{formatCurrency(entry.amount, entry.currency)}</strong><time dateTime={entry.recordedAtUtc} className="text-xs text-gray-500">{formatDate(entry.recordedAtUtc, { year: 'numeric', month: 'short', day: 'numeric', hour: '2-digit', minute: '2-digit', second: '2-digit' })}</time></div>
                {entry.sourceOrderAvailable ? <a className="text-sm text-[#6C63FF] underline" href={`?tab=orders&orderId=${encodeURIComponent(entry.orderId)}`}>{label('sourceOrder')}: {entry.orderNumber}</a> : <p className="text-sm">{label('sourceUnavailable')}</p>}
                {entry.productName && <p className="text-sm">{entry.productName}</p>}
                <p className={`text-xs ${entry.isSyntheticQa ? 'text-amber-700 dark:text-amber-300' : 'text-gray-500'}`}>{entry.isSyntheticQa ? `${t('portal.syntheticQa.label')} — ${t('portal.syntheticQa.excluded')}` : label(entry.classification === 'UnclassifiedLineage' ? 'unclassified' : 'historyOnly')}</p>
                {entry.reasonCode && <p className="text-xs text-gray-500">{label('reasonUnavailable')}</p>}
                <details className="text-xs text-gray-500"><summary className="cursor-pointer">{label('lineage')}</summary><dl className="space-y-2 mt-2 break-all">{[['entryId', entry.entryId], ['parentEntry', entry.originalEntryId], ['orderLine', entry.orderItemId], ['attributionId', entry.attributionId], ['store', entry.storeTenantId]].map(([key, value]) => <div key={key}><dt>{label(key)}</dt><dd dir="ltr">{value || t('ux.unknown')}</dd></div>)}</dl><p className="mt-2">{label('allocationUnavailable')}</p></details>
            </li>)}</ol>
            {state.data.totalPages > 1 && <nav aria-label={label('ledgerPaging')} className="flex flex-wrap justify-between gap-3 mt-4 text-sm"><span>{t('portal.ordersTab.page')} {formatNumber(page)} {t('portal.ordersTab.of')} {formatNumber(state.data.totalPages)}</span><div className="flex gap-4"><button disabled={page <= 1} onClick={() => setPage(value => value - 1)}>{t('portal.ordersTab.prev')}</button><button disabled={page >= state.data.totalPages} onClick={() => setPage(value => value + 1)}>{t('portal.ordersTab.next')}</button></div></nav>}
        </>}
    </PortalPanel>
}
