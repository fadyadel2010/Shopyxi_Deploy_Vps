import { useTranslation } from 'react-i18next'
import { Table, TableBody, TableCaption, TableCell, TableHead, TableHeader, TableRow } from '../../design-system/primitives'
import { useRTL } from '../../hooks/useRTL'
import { formatDate } from '../../utils/dateFormat'

export default function DashboardSalesTrend({ data, period, formatCurrency }) {
    const { t } = useTranslation()
    const { locale } = useRTL()
    // Only geometry is computed: no business amounts are summed or recomputed.
    const min = Math.min(0, ...data.map(row => row.amount))
    const max = Math.max(0, ...data.map(row => row.amount))
    const span = max - min || 1
    const y = amount => 180 - (amount - min) / span * 160
    const points = data.map((row, index) => ({
        x: data.length === 1 ? 300 : 20 + index / (data.length - 1) * 560,
        y: y(row.amount),
    }))
    const label = row => formatDate(
        // The backend emits UTC bins as offsetless midnight. Never shift these bins locally.
        `${row.date.slice(0, 10)}T00:00:00Z`, locale,
        { timeZone: 'UTC', year: 'numeric', month: 'short', ...(period === 'year' ? {} : { day: 'numeric' }) },
    )
    return (
        <div className="pos-dashboard__trend">
            <div className="pos-dashboard__trend-scale"><bdi>{formatCurrency(max)}</bdi><bdi>{formatCurrency(min)}</bdi></div>
            <svg viewBox="0 0 600 200" preserveAspectRatio="none" role="img" aria-label={t('dashboard.phase42.trendAccessible')}>
                <line x1="20" x2="580" y1={y(0)} y2={y(0)} className="pos-dashboard__axis" />
                <polyline fill="none" points={points.map(point => `${point.x},${point.y}`).join(' ')} className="pos-dashboard__line" />
                {points.map((point, index) => <circle key={index} cx={point.x} cy={point.y} r="3" className="pos-dashboard__point" />)}
            </svg>
            <div className="pos-dashboard__trend-ends" dir="ltr"><bdi>{data.length ? label(data[0]) : ''}</bdi><bdi>{data.length ? label(data[data.length - 1]) : ''}</bdi></div>
            <details>
                <summary>{t('dashboard.phase42.viewValues')}</summary>
                <Table>
                    <TableCaption>{t('dashboard.phase42.ledgerBasis')}</TableCaption>
                    <TableHeader><TableRow><TableHead>{t('dashboard.date')}</TableHead><TableHead>{t('dashboard.amount')}</TableHead></TableRow></TableHeader>
                    <TableBody>{data.map((row, index) => <TableRow key={index}><TableCell><bdi>{label(row)}</bdi></TableCell><TableCell className="pos-dashboard__number"><bdi>{formatCurrency(row.amount)}</bdi></TableCell></TableRow>)}</TableBody>
                </Table>
            </details>
        </div>
    )
}
