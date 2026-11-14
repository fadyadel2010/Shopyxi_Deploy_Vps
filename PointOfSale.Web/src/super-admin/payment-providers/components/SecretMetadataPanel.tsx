import { useSuperAdminLocale } from '../../i18n/useSuperAdminLocale'
import type { PaymentProviderSecretMetadata } from '../paymentProviderTypes'
import { SecretStatusBadge } from './SecretStatusBadge'

export function SecretMetadataPanel({ secret }: { secret: PaymentProviderSecretMetadata }) {
    const { tr, formatDateTime } = useSuperAdminLocale()
    const notRecorded = tr('Not recorded', 'غير مسجل')

    return (
        <div className="grid gap-3 rounded-xl border border-slate-200 bg-slate-50 p-3.5 text-sm sm:grid-cols-2 lg:grid-cols-3">
            <MetadataItem label={tr('Version', 'الإصدار')} value={`v${secret.version}`} mono />
            <div>
                <p className="text-[10px] font-semibold uppercase tracking-[0.14em] text-slate-500">
                    {tr('Status', 'الحالة')}
                </p>
                <div className="mt-1">
                    <SecretStatusBadge status={secret.status} />
                </div>
            </div>
            <MetadataItem
                label={tr('Created', 'أُنشئ')}
                value={formatDateTime(secret.createdAtUtc, notRecorded)}
            />
            <MetadataItem
                label={tr('Last rotated', 'آخر تدوير')}
                value={formatDateTime(secret.lastRotatedAtUtc, notRecorded)}
            />
            <MetadataItem
                label={tr('Last used', 'آخر استخدام')}
                value={formatDateTime(secret.lastUsedAtUtc, notRecorded)}
            />
            <MetadataItem
                label={tr('Created by', 'أنشأه')}
                value={secret.createdBy || tr('Unknown', 'غير معروف')}
            />
            <MetadataItem
                label={tr('Fingerprint', 'البصمة')}
                value={secret.fingerprint}
                mono
                fullWidth
            />
        </div>
    )
}

function MetadataItem({
    label,
    value,
    mono = false,
    fullWidth = false,
}: {
    label: string
    value: string
    mono?: boolean
    fullWidth?: boolean
}) {
    return (
        <div className={fullWidth ? 'sm:col-span-2 lg:col-span-3' : undefined}>
            <p className="text-[10px] font-semibold uppercase tracking-[0.14em] text-slate-500">{label}</p>
            <p
                className={`mt-0.5 break-words font-medium text-slate-900 ${mono ? 'font-mono text-xs' : ''}`}
                dir="auto"
            >
                {value}
            </p>
        </div>
    )
}
