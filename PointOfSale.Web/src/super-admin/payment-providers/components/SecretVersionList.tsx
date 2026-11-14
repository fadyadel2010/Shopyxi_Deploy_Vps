import { useSuperAdminLocale } from '../../i18n/useSuperAdminLocale'
import { saTable } from '../../ui/SuperAdminUI'
import type { PaymentProviderSecretMetadata } from '../paymentProviderTypes'
import { SecretStatusBadge } from './SecretStatusBadge'

export function SecretVersionList({ versions }: { versions: PaymentProviderSecretMetadata[] }) {
    const { tr, formatDateTime } = useSuperAdminLocale()
    const notRecorded = tr('Not recorded', 'غير مسجل')

    if (versions.length === 0) {
        return (
            <div className="rounded-xl border border-dashed border-slate-300 bg-white px-3.5 py-3 text-sm text-slate-600">
                {tr('No secret versions have been created yet.', 'لم يُنشأ أي إصدار من الأسرار بعد.')}
            </div>
        )
    }

    return (
        <div className={saTable.shell}>
            <table className={saTable.table}>
                <thead className={saTable.head}>
                    <tr>
                        <th className={saTable.headerCell}>{tr('Version', 'الإصدار')}</th>
                        <th className={saTable.headerCell}>{tr('Status', 'الحالة')}</th>
                        <th className={saTable.headerCell}>{tr('Created', 'أُنشئ')}</th>
                        <th className={saTable.headerCell}>{tr('Activated', 'فُعّل')}</th>
                        <th className={saTable.headerCell}>{tr('Retired', 'سُحب')}</th>
                        <th className={saTable.headerCell}>{tr('Fingerprint', 'البصمة')}</th>
                    </tr>
                </thead>
                <tbody className="divide-y divide-slate-100 bg-white">
                    {versions.map((version) => (
                        <tr key={`${version.secretId}-${version.version}`}>
                            <td className={`${saTable.cell} font-mono text-xs font-semibold text-slate-950`} dir="ltr">
                                v{version.version}
                            </td>
                            <td className={saTable.cell}>
                                <SecretStatusBadge status={version.status} />
                            </td>
                            <td className={`${saTable.cell} text-xs text-slate-700`} dir="auto">
                                {formatDateTime(version.createdAtUtc, notRecorded)}
                            </td>
                            <td className={`${saTable.cell} text-xs text-slate-700`} dir="auto">
                                {formatDateTime(version.activatedAtUtc, notRecorded)}
                            </td>
                            <td className={`${saTable.cell} text-xs text-slate-700`} dir="auto">
                                {formatDateTime(version.retiredAtUtc, notRecorded)}
                            </td>
                            <td className={`${saTable.cell} font-mono text-xs text-slate-700`} dir="ltr">
                                {version.fingerprint}
                            </td>
                        </tr>
                    ))}
                </tbody>
            </table>
        </div>
    )
}
