import { useSuperAdminLocale } from '../../i18n/useSuperAdminLocale'

export function ProviderBadge({ enabled, status }: { enabled: boolean; status: string }) {
    const { tr } = useSuperAdminLocale()
    const tone = enabled
        ? 'border-emerald-200 bg-emerald-50 text-emerald-800'
        : 'border-slate-200 bg-slate-50 text-slate-600'

    const statusLabel = () => {
        if (enabled) {
            return tr('Enabled', 'مفعّل')
        }

        if (!status) {
            return tr('Not configured', 'غير مهيأ')
        }

        if (status.toLowerCase() === 'configured') {
            return tr('Configured', 'مُهيأ')
        }

        return status
    }

    return (
        <span className={`inline-flex items-center rounded-full border px-3 py-1 text-xs font-semibold ${tone}`}>
            {statusLabel()}
        </span>
    )
}
