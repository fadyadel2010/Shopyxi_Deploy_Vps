import { CreditCard } from 'lucide-react'
import { useSuperAdminLocale } from '../../i18n/useSuperAdminLocale'
import type { PaymentProviderSummary } from '../paymentProviderTypes'
import { ProviderBadge } from './ProviderBadge'

export function PaymentProviderCard({
    provider,
    selected,
    onSelect,
}: {
    provider: PaymentProviderSummary
    selected: boolean
    onSelect: (providerCode: string) => void
}) {
    const { tr } = useSuperAdminLocale()

    return (
        <button
            type="button"
            onClick={() => onSelect(provider.providerCode)}
            aria-pressed={selected}
            className={[
                'w-full rounded-xl border px-3.5 py-3 text-start transition',
                selected
                    ? 'border-emerald-300 bg-emerald-50 shadow-sm'
                    : 'border-slate-200 bg-white hover:border-slate-300 hover:bg-slate-50',
            ].join(' ')}
        >
            <div className="flex items-start gap-2.5">
                <div className="flex h-9 w-9 shrink-0 items-center justify-center rounded-lg bg-slate-100 text-slate-700">
                    <CreditCard className="h-4 w-4" />
                </div>
                <div className="min-w-0 flex-1">
                    <div className="flex flex-wrap items-center gap-1.5">
                        <p className="truncate text-sm font-semibold text-slate-950">{provider.displayName}</p>
                        <ProviderBadge enabled={provider.enabled} status={provider.status} />
                    </div>
                    <p className="mt-0.5 font-mono text-[11px] font-semibold uppercase tracking-[0.12em] text-slate-500" dir="ltr">
                        {provider.providerCode}
                    </p>
                    <p className="mt-1.5 line-clamp-2 text-xs leading-5 text-slate-600" dir="auto">
                        {provider.description}
                    </p>
                </div>
                <span className="sr-only">
                    {selected ? tr('Selected provider', 'المزود المحدد') : tr('Select provider', 'اختيار المزود')}
                </span>
            </div>
        </button>
    )
}
