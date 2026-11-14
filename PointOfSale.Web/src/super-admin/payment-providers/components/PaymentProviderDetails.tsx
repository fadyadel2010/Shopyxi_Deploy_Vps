import { CheckCircle2, CircleOff } from 'lucide-react'
import { useSuperAdminLocale } from '../../i18n/useSuperAdminLocale'
import { StatusBadge } from '../../ui/SuperAdminUI'
import type { PaymentProviderConfiguration } from '../paymentProviderTypes'
import { ProviderBadge } from './ProviderBadge'

export function PaymentProviderDetails({
    provider,
}: {
    provider: PaymentProviderConfiguration | null
}) {
    const { tr } = useSuperAdminLocale()

    if (!provider) {
        return (
            <div className="rounded-2xl border border-dashed border-slate-300 bg-slate-50 px-4 py-6 text-sm text-slate-600">
                {tr(
                    'Select a provider to view its configuration foundation.',
                    'اختر مزودًا لعرض أساس إعداده.',
                )}
            </div>
        )
    }

    return (
        <div className="rounded-2xl border border-slate-200 bg-white p-4 shadow-sm">
            <div className="flex flex-wrap items-start justify-between gap-3">
                <div className="min-w-0">
                    <p className="text-[11px] font-semibold uppercase tracking-[0.18em] text-emerald-700">
                        {tr('Provider configuration', 'إعداد المزود')}
                    </p>
                    <h2 className="mt-1 text-lg font-semibold text-slate-950">{provider.displayName}</h2>
                    <p className="mt-1 max-w-2xl text-sm leading-6 text-slate-600" dir="auto">
                        {provider.description}
                    </p>
                </div>
                <ProviderBadge enabled={provider.enabled} status={provider.status} />
            </div>

            <dl className="mt-3 grid gap-2 sm:grid-cols-3">
                <div className="rounded-lg border border-slate-200 bg-slate-50 px-3 py-2">
                    <dt className="text-[10px] font-semibold uppercase tracking-[0.14em] text-slate-500">
                        {tr('Code', 'الرمز')}
                    </dt>
                    <dd className="mt-0.5 font-mono text-xs font-semibold text-slate-950" dir="ltr">
                        {provider.providerCode}
                    </dd>
                </div>
                <div className="rounded-lg border border-slate-200 bg-slate-50 px-3 py-2">
                    <dt className="text-[10px] font-semibold uppercase tracking-[0.14em] text-slate-500">
                        {tr('Environment', 'البيئة')}
                    </dt>
                    <dd className="mt-0.5 text-sm font-semibold text-slate-950" dir="auto">
                        {provider.environment}
                    </dd>
                </div>
                <div className="rounded-lg border border-slate-200 bg-slate-50 px-3 py-2">
                    <dt className="text-[10px] font-semibold uppercase tracking-[0.14em] text-slate-500">
                        {tr('State', 'الحالة')}
                    </dt>
                    <dd className="mt-1">
                        <StatusBadge tone={provider.enabled ? 'emerald' : 'slate'}>
                            {provider.enabled ? tr('Enabled', 'مفعّل') : tr('Disabled', 'معطّل')}
                        </StatusBadge>
                    </dd>
                </div>
            </dl>

            <div className="mt-3">
                <p className="text-[11px] font-semibold uppercase tracking-[0.18em] text-slate-500">
                    {tr('Supported capabilities', 'القدرات المدعومة')}
                </p>
                <div className="mt-2 grid gap-2 sm:grid-cols-2 xl:grid-cols-3">
                    {provider.capabilities.map((capability) => {
                        const Icon = capability.supported ? CheckCircle2 : CircleOff
                        return (
                            <div key={capability.code} className="rounded-lg border border-slate-200 bg-white px-3 py-2.5">
                                <div className="flex items-center gap-1.5">
                                    <Icon
                                        className={
                                            capability.supported
                                                ? 'h-3.5 w-3.5 text-emerald-600'
                                                : 'h-3.5 w-3.5 text-slate-400'
                                        }
                                    />
                                    <p className="truncate text-sm font-semibold text-slate-950" dir="auto">
                                        {capability.displayName}
                                    </p>
                                </div>
                                <p className="mt-1 line-clamp-2 text-xs leading-5 text-slate-600" dir="auto">
                                    {capability.description}
                                </p>
                            </div>
                        )
                    })}
                </div>
            </div>
        </div>
    )
}
