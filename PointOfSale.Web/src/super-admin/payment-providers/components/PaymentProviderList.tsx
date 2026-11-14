import { Loader2 } from 'lucide-react'
import { useSuperAdminLocale } from '../../i18n/useSuperAdminLocale'
import type { PaymentProviderSummary } from '../paymentProviderTypes'
import { PaymentProviderCard } from './PaymentProviderCard'

export function PaymentProviderList({
    providers,
    loading,
    selectedProviderCode,
    onSelect,
}: {
    providers: PaymentProviderSummary[]
    loading: boolean
    selectedProviderCode: string | null
    onSelect: (providerCode: string) => void
}) {
    const { tr } = useSuperAdminLocale()

    if (loading) {
        return (
            <div className="flex items-center gap-3 rounded-xl border border-slate-200 bg-white px-3.5 py-4 text-sm text-slate-600">
                <Loader2 className="h-4 w-4 animate-spin" />
                {tr('Loading providers…', 'جارٍ تحميل المزودين…')}
            </div>
        )
    }

    if (providers.length === 0) {
        return (
            <div className="rounded-xl border border-dashed border-slate-300 bg-slate-50 px-3.5 py-5 text-sm text-slate-600">
                {tr('No payment providers are registered.', 'لا يوجد مزودو دفع مسجلون.')}
            </div>
        )
    }

    return (
        <div className="space-y-2">
            {providers.map((provider) => (
                <PaymentProviderCard
                    key={provider.providerCode}
                    provider={provider}
                    selected={provider.providerCode === selectedProviderCode}
                    onSelect={onSelect}
                />
            ))}
        </div>
    )
}
