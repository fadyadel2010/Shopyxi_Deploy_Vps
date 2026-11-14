import { Clock3 } from 'lucide-react'
import { useSuperAdminLocale } from '../../i18n/useSuperAdminLocale'
import type { PaymentProviderAdministrationActivity } from '../paymentProviderTypes'

export function AdministrationTimeline({ activity }: { activity: PaymentProviderAdministrationActivity[] }) {
    const { tr, formatDateTime } = useSuperAdminLocale()

    return (
        <section className="rounded-lg border border-slate-200 bg-white px-4 py-4 shadow-sm">
            <div className="flex items-center justify-between gap-3">
                <div>
                    <p className="text-[11px] font-semibold uppercase tracking-[0.18em] text-slate-500">
                        {tr('Recent administrative activity', 'النشاط الإداري الأخير')}
                    </p>
                    <h3 className="mt-1 text-base font-semibold text-slate-950">
                        {tr('Audit timeline', 'الخط الزمني للتدقيق')}
                    </h3>
                </div>
                <Clock3 className="h-4 w-4 text-slate-500" />
            </div>
            <div className="mt-3 space-y-2">
                {activity.length === 0 ? (
                    <div className="rounded-lg border border-dashed border-slate-300 bg-slate-50 px-3.5 py-3 text-sm text-slate-600">
                        {tr('No administrative events recorded.', 'لا توجد أحداث إدارية مسجلة.')}
                    </div>
                ) : activity.map((item) => (
                    <article key={item.activityId} className="rounded-lg border border-slate-200 bg-slate-50 px-3.5 py-3">
                        <div className="flex flex-wrap items-center justify-between gap-2">
                            <p className="font-semibold text-slate-950" dir="auto">{item.displayName}</p>
                            <span className={item.success ? 'text-xs font-semibold text-emerald-700' : 'text-xs font-semibold text-rose-700'}>
                                {item.success ? tr('Success', 'نجح') : tr('Failed', 'فشل')}
                            </span>
                        </div>
                        <p className="mt-1 text-sm leading-6 text-slate-600" dir="auto">{item.description}</p>
                        <p className="mt-1.5 text-xs font-medium text-slate-500">
                            {[
                                item.providerCode || tr('All providers', 'كل المزودين'),
                                item.actor || tr('System', 'النظام'),
                                formatDateTime(item.occurredAtUtc, tr('Unavailable', 'غير متاح')),
                            ].join(' / ')}
                        </p>
                    </article>
                ))}
            </div>
        </section>
    )
}
