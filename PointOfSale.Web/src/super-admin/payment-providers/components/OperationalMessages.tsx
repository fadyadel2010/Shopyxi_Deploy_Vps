import { AlertCircle, CheckCircle2 } from 'lucide-react'
import { useSuperAdminLocale } from '../../i18n/useSuperAdminLocale'
import type { PaymentProviderHealth } from '../paymentProviderTypes'

export function OperationalMessages({ health }: { health?: PaymentProviderHealth | null }) {
    const { tr } = useSuperAdminLocale()
    const messages = health?.operationalMessages ?? []

    return (
        <section className="rounded-2xl border border-slate-200 bg-white px-4 py-4 shadow-sm">
            <div className="flex items-center gap-2">
                <AlertCircle className="h-4 w-4 text-slate-600" />
                <h3 className="text-base font-semibold text-slate-950">
                    {tr('Operational messages', 'الرسائل التشغيلية')}
                </h3>
            </div>
            {messages.length > 0 ? (
                <ul className="mt-3 space-y-2 text-sm text-slate-700">
                    {messages.map((message) => (
                        <li key={message} className="rounded-xl border border-slate-200 bg-slate-50 px-3.5 py-2.5" dir="auto">
                            {message}
                        </li>
                    ))}
                </ul>
            ) : (
                <div className="mt-3 flex items-center gap-2 rounded-xl border border-emerald-200 bg-emerald-50 px-3.5 py-2.5 text-sm font-medium text-emerald-800">
                    <CheckCircle2 className="h-4 w-4" />
                    {tr('No operational messages.', 'لا توجد رسائل تشغيلية.')}
                </div>
            )}
        </section>
    )
}
