import { Activity, KeyRound, Layers3, Power } from 'lucide-react'
import { useSuperAdminLocale } from '../../i18n/useSuperAdminLocale'
import type { PaymentProviderDashboardSummary } from '../paymentProviderTypes'

export function ProviderStatusOverview({ summary }: { summary: PaymentProviderDashboardSummary }) {
    const { tr, formatNumber } = useSuperAdminLocale()
    const tiles = [
        { key: 'providers', label: tr('Providers', 'المزودون'), value: summary.providerCount, icon: Layers3 },
        { key: 'enabled', label: tr('Enabled', 'المفعّلة'), value: summary.enabledCount, icon: Power },
        { key: 'healthy', label: tr('Healthy', 'السليمة'), value: summary.healthyCount, icon: Activity },
        { key: 'secrets', label: tr('Secrets', 'الأسرار'), value: summary.configuredSecretCount, icon: KeyRound },
    ]

    return (
        <section className="grid gap-3 md:grid-cols-4">
            {tiles.map(({ key, label, value, icon: Icon }) => (
                <div key={key} className="rounded-lg border border-slate-200 bg-white p-4 shadow-sm">
                    <div className="flex items-center justify-between gap-3">
                        <p className="text-xs font-semibold uppercase tracking-wide text-slate-500">{label}</p>
                        <Icon className="h-4 w-4 text-slate-500" />
                    </div>
                    <p className="mt-2 text-2xl font-semibold text-slate-950" dir="auto">{formatNumber(value)}</p>
                </div>
            ))}
        </section>
    )
}
