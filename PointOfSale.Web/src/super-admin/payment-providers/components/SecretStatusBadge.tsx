import { CheckCircle2, Clock3, Archive, XCircle } from 'lucide-react'
import { useSuperAdminLocale } from '../../i18n/useSuperAdminLocale'

export function SecretStatusBadge({ status }: { status: string }) {
    const { tr } = useSuperAdminLocale()
    const normalized = status.toLowerCase()
    const style = normalized === 'active'
        ? 'border-emerald-200 bg-emerald-50 text-emerald-800'
        : normalized === 'pendingactivation'
            ? 'border-amber-200 bg-amber-50 text-amber-800'
            : normalized === 'retired'
                ? 'border-slate-200 bg-slate-50 text-slate-700'
                : 'border-zinc-200 bg-zinc-50 text-zinc-700'
    const Icon = normalized === 'active'
        ? CheckCircle2
        : normalized === 'pendingactivation'
            ? Clock3
            : normalized === 'retired'
                ? XCircle
                : Archive

    const label =
        normalized === 'active'
            ? tr('Active', 'نشط')
            : normalized === 'pendingactivation'
              ? tr('Pending activation', 'بانتظار التفعيل')
              : normalized === 'retired'
                ? tr('Retired', 'مسحوب')
                : status

    return (
        <span className={`inline-flex items-center gap-1 rounded-md border px-2 py-1 text-xs font-semibold ${style}`}>
            <Icon className="h-3.5 w-3.5" />
            {label}
        </span>
    )
}
