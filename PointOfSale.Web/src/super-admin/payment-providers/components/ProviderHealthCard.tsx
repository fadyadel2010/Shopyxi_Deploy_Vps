import { Activity, Clock3, Gauge } from 'lucide-react'
import { useSuperAdminLocale } from '../../i18n/useSuperAdminLocale'
import type { PaymentProviderHealth } from '../paymentProviderTypes'
import { HealthBadge } from './HealthBadge'

export function ProviderHealthCard({ health }: { health?: PaymentProviderHealth | null }) {
    const { tr, formatDateTime, formatNumber } = useSuperAdminLocale()
    const currentHealth = health ?? {
        status: 'Unknown',
        environment: '',
        connectionQuality: 'Unknown',
        operationalMessages: [],
    }

    const duration =
        typeof currentHealth.lastCheckDurationMs === 'number'
            ? tr(
                  `${formatNumber(currentHealth.lastCheckDurationMs)} ms`,
                  `${formatNumber(currentHealth.lastCheckDurationMs)} مللي ثانية`,
              )
            : tr('Unavailable', 'غير متاح')

    return (
        <section className="rounded-2xl border border-slate-200 bg-white p-4 shadow-sm">
            <div className="flex flex-wrap items-start justify-between gap-2">
                <div>
                    <p className="text-[11px] font-semibold uppercase tracking-[0.18em] text-slate-500">
                        {tr('Health', 'الصحة')}
                    </p>
                    <h3 className="mt-1 text-base font-semibold text-slate-950">
                        {tr('Provider health', 'صحة المزود')}
                    </h3>
                </div>
                <HealthBadge status={currentHealth.status} />
            </div>

            <dl className="mt-3 grid gap-2 sm:grid-cols-2 lg:grid-cols-3">
                <div className="rounded-lg border border-slate-200 bg-slate-50 px-3 py-2">
                    <dt className="flex items-center gap-1.5 text-[10px] font-semibold uppercase tracking-[0.14em] text-slate-500">
                        <Activity className="h-3 w-3" />
                        {tr('Environment', 'البيئة')}
                    </dt>
                    <dd className="mt-0.5 text-sm font-semibold text-slate-950" dir="auto">
                        {currentHealth.environment || tr('Unknown', 'غير معروف')}
                    </dd>
                </div>
                <div className="rounded-lg border border-slate-200 bg-slate-50 px-3 py-2">
                    <dt className="flex items-center gap-1.5 text-[10px] font-semibold uppercase tracking-[0.14em] text-slate-500">
                        <Clock3 className="h-3 w-3" />
                        {tr('Last success', 'آخر نجاح')}
                    </dt>
                    <dd className="mt-0.5 text-sm font-semibold text-slate-950" dir="auto">
                        {formatDateTime(currentHealth.lastSuccessfulCheckUtc, tr('Never', 'لم يحدث'))}
                    </dd>
                </div>
                <div className="rounded-lg border border-slate-200 bg-slate-50 px-3 py-2">
                    <dt className="flex items-center gap-1.5 text-[10px] font-semibold uppercase tracking-[0.14em] text-slate-500">
                        <Clock3 className="h-3 w-3" />
                        {tr('Last failure', 'آخر فشل')}
                    </dt>
                    <dd className="mt-0.5 text-sm font-semibold text-slate-950" dir="auto">
                        {formatDateTime(currentHealth.lastFailedCheckUtc, tr('Never', 'لم يحدث'))}
                    </dd>
                </div>
                <div className="rounded-lg border border-slate-200 bg-slate-50 px-3 py-2">
                    <dt className="text-[10px] font-semibold uppercase tracking-[0.14em] text-slate-500">
                        {tr('Duration', 'المدة')}
                    </dt>
                    <dd className="mt-0.5 text-sm font-semibold text-slate-950" dir="auto">
                        {duration}
                    </dd>
                </div>
                <div className="rounded-lg border border-slate-200 bg-slate-50 px-3 py-2">
                    <dt className="text-[10px] font-semibold uppercase tracking-[0.14em] text-slate-500">
                        {tr('Provider version', 'إصدار المزود')}
                    </dt>
                    <dd className="mt-0.5 truncate text-sm font-semibold text-slate-950" dir="auto">
                        {currentHealth.providerVersion || tr('Unavailable', 'غير متاح')}
                    </dd>
                </div>
                <div className="rounded-lg border border-slate-200 bg-slate-50 px-3 py-2">
                    <dt className="flex items-center gap-1.5 text-[10px] font-semibold uppercase tracking-[0.14em] text-slate-500">
                        <Gauge className="h-3 w-3" />
                        {tr('Quality', 'الجودة')}
                    </dt>
                    <dd className="mt-0.5 text-sm font-semibold text-slate-950" dir="auto">
                        {currentHealth.connectionQuality || tr('Unknown', 'غير معروف')}
                    </dd>
                </div>
            </dl>
        </section>
    )
}
