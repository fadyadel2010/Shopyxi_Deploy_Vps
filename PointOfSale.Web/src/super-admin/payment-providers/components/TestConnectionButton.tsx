import { RefreshCw, Wifi } from 'lucide-react'
import { useSuperAdminLocale } from '../../i18n/useSuperAdminLocale'

export function TestConnectionButton({
    running,
    disabled,
    onClick,
}: {
    running: boolean
    disabled: boolean
    onClick: () => void
}) {
    const { tr } = useSuperAdminLocale()

    return (
        <button
            type="button"
            onClick={onClick}
            disabled={disabled || running}
            className="inline-flex items-center justify-center gap-2 rounded-xl bg-slate-950 px-4 py-2 text-sm font-semibold text-white shadow-sm transition hover:bg-slate-800 disabled:cursor-not-allowed disabled:bg-slate-300"
        >
            {running ? <RefreshCw className="h-4 w-4 animate-spin" /> : <Wifi className="h-4 w-4" />}
            {running ? tr('Testing connection', 'جارٍ اختبار الاتصال') : tr('Test connection', 'اختبار الاتصال')}
        </button>
    )
}
