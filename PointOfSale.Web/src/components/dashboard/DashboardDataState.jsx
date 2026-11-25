import { useTranslation } from 'react-i18next'
import { Button } from '../../design-system/primitives'
import { useRTL } from '../../hooks/useRTL'
import { formatDateTime } from '../../utils/dateFormat'

export default function DashboardDataState({ source, children, emptyMessage, showUnavailableChildren = false }) {
    const { t } = useTranslation()
    const { locale } = useRTL()
    const hasData = source.data !== null
    const empty = Array.isArray(source.data) && source.data.length === 0
    return (
        <div className="pos-dashboard__source" aria-busy={source.pending || undefined}>
            {source.error ? (
                <div className="pos-dashboard__error" role="status">
                    <p>{t(`dashboard.phase42.${source.error}`)}</p>
                    {hasData ? <p>{t('dashboard.phase42.stale', { time: formatDateTime(source.lastSuccess, locale) })}</p> : null}
                    <Button variant="secondary" size="sm" disabled={source.pending} onClick={source.retry}>{t('common.retry')}</Button>
                </div>
            ) : null}
            {!hasData ? (
                <>
                    <p className="pos-dashboard__placeholder" role={source.error ? undefined : 'status'}>
                        {source.pending ? t('common.loading') : t('dashboard.phase42.unavailable')}
                    </p>
                    {showUnavailableChildren ? children : null}
                </>
            ) : empty ? <p className="pos-dashboard__empty">{emptyMessage || t('dashboard.phase42.empty')}</p> : children}
        </div>
    )
}
