import { Timer } from 'lucide-react'
import { useSuperAdminLocale } from '../../i18n/useSuperAdminLocale'
import type { PaymentProviderActionResult } from '../paymentProviderTypes'
import { HealthBadge } from './HealthBadge'

function messageList(result: PaymentProviderActionResult) {
    return result.errors.length > 0 ? result.errors : result.warnings
}

export function ConnectionResultPanel({ result }: { result: PaymentProviderActionResult | null }) {
    const { tr, formatNumber } = useSuperAdminLocale()

    if (!result) {
        return null
    }

    const messages = messageList(result)

    return (
        <section className="rounded-2xl border border-slate-200 bg-white px-4 py-4 shadow-sm">
            <div className="flex flex-wrap items-start justify-between gap-3">
                <div className="min-w-0">
                    <p className="text-[11px] font-semibold uppercase tracking-[0.18em] text-slate-500">
                        {tr('Latest result', 'أحدث نتيجة')}
                    </p>
                    <h3 className="mt-1 text-base font-semibold text-slate-950" dir="auto">
                        {result.message}
                    </h3>
                </div>
                <HealthBadge status={result.status} />
            </div>
            <div className="mt-3 flex flex-wrap gap-2 text-sm text-slate-700">
                <span className="inline-flex items-center gap-2 rounded-full border border-slate-200 bg-slate-50 px-3 py-1">
                    <Timer className="h-4 w-4" />
                    {tr(`${formatNumber(result.latencyMs)} ms`, `${formatNumber(result.latencyMs)} مللي ثانية`)}
                </span>
                <span className="rounded-full border border-slate-200 bg-slate-50 px-3 py-1">{result.environment}</span>
                {result.providerVersion ? (
                    <span className="rounded-full border border-slate-200 bg-slate-50 px-3 py-1">{result.providerVersion}</span>
                ) : null}
                {result.errorCategory ? (
                    <span className="rounded-full border border-rose-200 bg-rose-50 px-3 py-1 text-rose-700" dir="auto">
                        {result.errorCategory}
                    </span>
                ) : null}
            </div>
            {messages.length > 0 ? (
                <ul className="mt-3 list-disc space-y-1 ps-5 text-sm text-slate-700">
                    {messages.map((message) => (
                        <li key={message} dir="auto">
                            {message}
                        </li>
                    ))}
                </ul>
            ) : null}
        </section>
    )
}
