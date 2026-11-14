import { ChevronDown, ChevronUp, ShieldCheck } from 'lucide-react'
import { useSuperAdminLocale } from '../../i18n/useSuperAdminLocale'
import type { PaymentProviderDashboardProvider } from '../paymentProviderTypes'
import { HealthBadge } from './HealthBadge'
import { ProviderBadge } from './ProviderBadge'

export function ProviderSummaryCard({
    provider,
    expanded,
    onToggle,
}: {
    provider: PaymentProviderDashboardProvider
    expanded: boolean
    onToggle: () => void
}) {
    const { tr, formatDateTime, formatNumber } = useSuperAdminLocale()

    return (
        <article className="rounded-2xl border border-slate-200 bg-white p-4 shadow-sm">
            <div className="flex flex-wrap items-start justify-between gap-3">
                <div className="min-w-0">
                    <div className="flex flex-wrap items-center gap-2">
                        <h3 className="text-base font-semibold text-slate-950">{provider.displayName}</h3>
                        <ProviderBadge enabled={provider.enabled} status={provider.status} />
                        <HealthBadge status={provider.health.status} />
                    </div>
                    <p className="mt-1.5 max-w-3xl text-sm leading-6 text-slate-600" dir="auto">
                        {provider.description}
                    </p>
                </div>
                <button
                    type="button"
                    onClick={onToggle}
                    aria-expanded={expanded}
                    className="inline-flex h-9 items-center gap-2 rounded-xl border border-slate-200 bg-white px-3 text-sm font-semibold text-slate-700 transition hover:bg-slate-50"
                >
                    {expanded ? <ChevronUp className="h-4 w-4" /> : <ChevronDown className="h-4 w-4" />}
                    {expanded ? tr('Hide details', 'إخفاء التفاصيل') : tr('Details', 'التفاصيل')}
                </button>
            </div>

            <dl className="mt-3 grid gap-2 sm:grid-cols-2 xl:grid-cols-3">
                <Metric label={tr('Provider', 'المزود')} value={provider.providerCode} mono />
                <Metric label={tr('Environment', 'البيئة')} value={provider.environment} />
                <Metric label={tr('Enabled', 'مفعّل')} value={provider.enabled ? tr('Yes', 'نعم') : tr('No', 'لا')} />
                <Metric
                    label={tr('Last check', 'آخر فحص')}
                    value={formatDateTime(provider.lastCheckUtc, tr('Never', 'لم يحدث'))}
                />
                <Metric label={tr('Secrets', 'الأسرار')} value={formatNumber(provider.secrets.length)} />
                <Metric
                    label={tr('Version', 'الإصدار')}
                    value={provider.version || tr('Unavailable', 'غير متاح')}
                    mono
                />
            </dl>

            {expanded ? (
                <div className="mt-4 space-y-3 border-t border-slate-100 pt-4">
                    <section className="rounded-xl border border-slate-200 bg-slate-50 p-3.5">
                        <div className="flex items-center gap-2">
                            <ShieldCheck className="h-4 w-4 text-slate-600" />
                            <h4 className="text-sm font-semibold text-slate-950">
                                {tr('Administrative permissions', 'الصلاحيات الإدارية')}
                            </h4>
                        </div>
                        <div className="mt-2.5 flex flex-wrap gap-1.5">
                            {provider.permissions.map((permission) => (
                                <span
                                    key={permission}
                                    className="rounded-md border border-slate-200 bg-white px-2.5 py-1 text-xs font-semibold text-slate-700"
                                    dir="auto"
                                >
                                    {permission}
                                </span>
                            ))}
                        </div>
                    </section>
                    <section className="rounded-xl border border-slate-200 bg-slate-50 p-3.5">
                        <h4 className="text-sm font-semibold text-slate-950">
                            {tr('Provider metadata', 'بيانات وصفية للمزود')}
                        </h4>
                        <div className="mt-2.5 grid gap-2 sm:grid-cols-2 xl:grid-cols-4">
                            {Object.entries(provider.metadata)
                                .slice(0, 8)
                                .map(([key, value]) => (
                                    <Metric
                                        key={key}
                                        label={key}
                                        value={String(value ?? tr('Unavailable', 'غير متاح'))}
                                    />
                                ))}
                        </div>
                    </section>
                </div>
            ) : null}
        </article>
    )
}

function Metric({ label, value, mono = false }: { label: string; value: string; mono?: boolean }) {
    return (
        <div className="rounded-lg border border-slate-200 bg-slate-50 px-3 py-2">
            <dt className="truncate text-[10px] font-semibold uppercase tracking-[0.14em] text-slate-500">{label}</dt>
            <dd className={`mt-0.5 truncate text-sm font-semibold text-slate-950 ${mono ? 'font-mono text-xs' : ''}`} dir="auto">
                {value}
            </dd>
        </div>
    )
}
