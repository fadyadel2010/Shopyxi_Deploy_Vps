import { Card } from '../../design-system/primitives'

// Receives formatted authoritative values. No financial arithmetic or zero fallback.
export default function DashboardMetric({ label, value, caption, children }) {
    return (
        <Card className="pos-dashboard__metric">
            <dl>
                <dt>{label}</dt>
                <dd className="pos-dashboard__value"><bdi>{value ?? '—'}</bdi></dd>
            </dl>
            {caption ? <p className="pos-dashboard__caption">{caption}</p> : null}
            {children}
        </Card>
    )
}
