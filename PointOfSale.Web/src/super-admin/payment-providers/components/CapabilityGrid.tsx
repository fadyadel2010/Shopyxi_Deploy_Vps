import { CheckCircle2, CircleDashed } from 'lucide-react'
import { useSuperAdminLocale } from '../../i18n/useSuperAdminLocale'
import type { PaymentProviderCapability } from '../paymentProviderTypes'

export function CapabilityGrid({ capabilities }: { capabilities: PaymentProviderCapability[] }) {
    const { tr, formatNumber } = useSuperAdminLocale()

    return (
        <section className="rounded-lg border border-slate-200 bg-white px-4 py-4 shadow-sm">
            <div className="flex items-center justify-between gap-3">
                <div>
                    <p className="text-[11px] font-semibold uppercase tracking-[0.18em] text-slate-500">
                        {tr('Capabilities', 'القدرات')}
                    </p>
                    <h3 className="mt-1 text-base font-semibold text-slate-950">
                        {tr('Canonical model', 'النموذج الموحد')}
                    </h3>
                </div>
                <span className="text-sm font-semibold text-slate-600" dir="auto">
                    {formatNumber(capabilities.length)}
                </span>
            </div>
            <div className="mt-3 grid gap-2 sm:grid-cols-2 xl:grid-cols-3">
                {capabilities.map((capability) => {
                    const Icon = capability.supported ? CheckCircle2 : CircleDashed
                    return (
                        <div key={capability.code} className="rounded-lg border border-slate-200 bg-slate-50 p-3">
                            <div className="flex items-center gap-2">
                                <Icon className={capability.supported ? 'h-4 w-4 text-emerald-600' : 'h-4 w-4 text-slate-400'} />
                                <p className="font-semibold text-slate-950" dir="auto">{capability.displayName}</p>
                            </div>
                            <p className="mt-1.5 text-xs leading-5 text-slate-600" dir="auto">{capability.description}</p>
                            <p className="mt-1.5 text-[10px] font-semibold uppercase tracking-[0.14em] text-slate-500">
                                {capability.supported ? tr('Supported', 'مدعومة') : tr('Not supported', 'غير مدعومة')}
                            </p>
                        </div>
                    )
                })}
            </div>
        </section>
    )
}
