import { CalendarDays, PencilLine, Power, ShieldAlert, ShieldCheck } from 'lucide-react'
import { useSuperAdminLocale } from '../i18n/useSuperAdminLocale'
import { EmptyState, StatusBadge, saButton, saTable } from '../ui/SuperAdminUI'
import type { CommissionRule } from './commissionService'

interface CommissionTableProps {
    rules: CommissionRule[]
    isLoading: boolean
    activeActionRuleId?: string | null
    getAppliedRuleLabel: (rule: CommissionRule) => string
    getDeactivationState: (rule: CommissionRule) => { disabled: boolean; reason?: string }
    onEdit: (rule: CommissionRule) => void
    onDeactivate: (rule: CommissionRule) => void
    onCreateFirstRule: () => void
    resolveScopeName: (rule: CommissionRule) => string
}

function TableSkeleton() {
    return (
        <div className="space-y-2 p-4">
            {Array.from({ length: 5 }).map((_, index) => (
                <div
                    key={`commission-skeleton-row-${index}`}
                    className="grid animate-pulse gap-3 rounded-xl border border-slate-100 bg-slate-50/70 px-4 py-3 md:grid-cols-[0.8fr_1.4fr_0.6fr_0.5fr_0.7fr_0.9fr_0.9fr]"
                >
                    {Array.from({ length: 7 }).map((__, cellIndex) => (
                        <div key={`commission-skeleton-cell-${index}-${cellIndex}`} className="h-4 rounded-full bg-slate-200" />
                    ))}
                </div>
            ))}
        </div>
    )
}

export default function CommissionTable({
    rules,
    isLoading,
    activeActionRuleId,
    getAppliedRuleLabel,
    getDeactivationState,
    onEdit,
    onDeactivate,
    onCreateFirstRule,
    resolveScopeName,
}: CommissionTableProps) {
    const { tr, formatNumber, formatDateTime } = useSuperAdminLocale()
    const activeCount = rules.filter((rule) => rule.isActive).length

    const scopeLabel = (scopeType: CommissionRule['scopeType']) => {
        if (scopeType === 'Global') return tr('Global', 'عام')
        if (scopeType === 'Category') return tr('Category', 'فئة')
        return tr('Vendor', 'بائع')
    }

    const scopeTone = (scopeType: CommissionRule['scopeType']) => {
        if (scopeType === 'Vendor') return 'sky' as const
        if (scopeType === 'Category') return 'violet' as const
        return 'slate' as const
    }

    const formatRate = (rate: number) =>
        `${formatNumber(rate * 100, { maximumFractionDigits: 2 })}%`

    return (
        <section className="overflow-hidden rounded-2xl border border-slate-200 bg-white shadow-sm">
            <div className="flex flex-col gap-3 border-b border-slate-100 px-4 py-4 sm:flex-row sm:items-start sm:justify-between sm:px-5">
                <div className="min-w-0">
                    <h2 className="text-base font-semibold text-slate-950">
                        {tr('Commission rules', 'قواعد العمولة')}
                    </h2>
                    <p className="mt-1 text-sm leading-6 text-slate-500">
                        {tr(
                            'Active rules are highlighted; inactive rules stay visible for audit purposes.',
                            'القواعد النشطة مميزة، وتبقى القواعد غير النشطة ظاهرة لأغراض التدقيق.',
                        )}
                    </p>
                </div>

                <span className="inline-flex items-center gap-1.5 self-start rounded-full border border-emerald-200 bg-emerald-50 px-2.5 py-1 text-xs font-semibold text-emerald-800 sm:self-auto">
                    <ShieldCheck className="h-3.5 w-3.5" />
                    {tr(
                        `${formatNumber(activeCount)} active ${activeCount === 1 ? 'rule' : 'rules'}`,
                        `${formatNumber(activeCount)} قاعدة نشطة`,
                    )}
                </span>
            </div>

            {isLoading ? (
                <TableSkeleton />
            ) : rules.length === 0 ? (
                <div className="px-4 py-6 sm:px-5">
                    <EmptyState
                        title={tr('No commission rules configured', 'لا توجد قواعد عمولة مُهيأة')}
                        description={tr(
                            'Create a global default first, then layer category and vendor overrides as needed.',
                            'أنشئ قاعدة عامة افتراضية أولًا، ثم أضف تجاوزات الفئات والبائعين حسب الحاجة.',
                        )}
                        action={
                            <button type="button" className={saButton.primary} onClick={onCreateFirstRule}>
                                <ShieldCheck className="h-4 w-4" />
                                {tr('Create the first rule', 'إنشاء أول قاعدة')}
                            </button>
                        }
                    />
                </div>
            ) : (
                <div className={saTable.shell}>
                    <table className={saTable.table}>
                        <thead className={saTable.head}>
                            <tr>
                                <th className={saTable.headerCell}>{tr('Scope', 'النطاق')}</th>
                                <th className={saTable.headerCell}>{tr('Target', 'الهدف')}</th>
                                <th className={`${saTable.headerCell} text-end`}>{tr('Rate', 'النسبة')}</th>
                                <th className={`${saTable.headerCell} text-end`}>{tr('Priority', 'الأولوية')}</th>
                                <th className={saTable.headerCell}>{tr('Status', 'الحالة')}</th>
                                <th className={saTable.headerCell}>{tr('Created', 'تاريخ الإنشاء')}</th>
                                <th className={`${saTable.headerCell} text-end`}>{tr('Actions', 'إجراءات')}</th>
                            </tr>
                        </thead>
                        <tbody className="divide-y divide-slate-100 bg-white">
                            {rules.map((rule) => {
                                const isBusy = activeActionRuleId === rule.id
                                const deactivationState = getDeactivationState(rule)

                                return (
                                    <tr
                                        key={rule.id}
                                        className={rule.isActive ? 'bg-emerald-50/40 align-top' : 'align-top text-slate-500'}
                                    >
                                        <td
                                            className={`${saTable.cell} border-s-4 ${
                                                rule.isActive ? 'border-s-emerald-500' : 'border-s-transparent'
                                            }`}
                                        >
                                            <div className="space-y-1.5">
                                                <StatusBadge tone={scopeTone(rule.scopeType)}>
                                                    {scopeLabel(rule.scopeType)}
                                                </StatusBadge>
                                                {rule.scopeType === 'Global' ? (
                                                    <p className="text-[10px] font-semibold uppercase tracking-[0.14em] text-sky-700">
                                                        {tr('Platform default', 'الافتراضي للمنصة')}
                                                    </p>
                                                ) : null}
                                            </div>
                                        </td>

                                        <td className={saTable.cell}>
                                            <div className="max-w-[300px]">
                                                <p className="truncate text-sm font-semibold text-slate-900">
                                                    {resolveScopeName(rule)}
                                                </p>
                                                <p className="mt-0.5 text-xs text-slate-500">{getAppliedRuleLabel(rule)}</p>
                                                <p className="mt-0.5 truncate font-mono text-[11px] text-slate-400" dir="ltr">
                                                    {rule.scopeId ?? rule.id}
                                                </p>
                                            </div>
                                        </td>

                                        <td className={`${saTable.cell} text-end text-sm font-semibold text-slate-950`} dir="auto">
                                            {formatRate(rule.rate)}
                                        </td>

                                        <td className={`${saTable.cell} text-end text-sm text-slate-700`} dir="auto">
                                            {formatNumber(rule.priority)}
                                        </td>

                                        <td className={saTable.cell}>
                                            <StatusBadge tone={rule.isActive ? 'emerald' : 'slate'}>
                                                {rule.isActive ? tr('Active', 'نشطة') : tr('Inactive', 'غير نشطة')}
                                            </StatusBadge>
                                        </td>

                                        <td className={`${saTable.cell} text-sm text-slate-600`} dir="auto">
                                            {formatDateTime(rule.createdAt, tr('Unavailable', 'غير متاح'))}
                                        </td>

                                        <td className={`${saTable.cell} text-end`}>
                                            <div className="flex flex-col items-end gap-1.5">
                                                <div className="flex flex-wrap items-center justify-end gap-1.5">
                                                    <button
                                                        type="button"
                                                        aria-label={tr(
                                                            `Edit ${rule.scopeType} commission rule`,
                                                            `تعديل قاعدة عمولة ${scopeLabel(rule.scopeType)}`,
                                                        )}
                                                        className={`${saButton.secondary} px-2.5 py-1.5 text-xs`}
                                                        onClick={() => onEdit(rule)}
                                                    >
                                                        <PencilLine className="h-3.5 w-3.5" />
                                                        {tr('Edit', 'تعديل')}
                                                    </button>
                                                    <button
                                                        type="button"
                                                        aria-label={tr(
                                                            `Deactivate ${rule.scopeType} commission rule`,
                                                            `تعطيل قاعدة عمولة ${scopeLabel(rule.scopeType)}`,
                                                        )}
                                                        title={deactivationState.reason}
                                                        className={`${saButton.danger} px-2.5 py-1.5 text-xs`}
                                                        disabled={isBusy || !rule.isActive || deactivationState.disabled}
                                                        onClick={() => onDeactivate(rule)}
                                                    >
                                                        <Power className="h-3.5 w-3.5" />
                                                        {isBusy
                                                            ? tr('Working…', 'جارٍ التنفيذ…')
                                                            : tr('Deactivate', 'تعطيل')}
                                                    </button>
                                                </div>

                                                {deactivationState.disabled ? (
                                                    <div className="flex max-w-[220px] items-start justify-end gap-1.5 text-end text-[11px] font-medium text-amber-700">
                                                        <ShieldAlert className="mt-0.5 h-3.5 w-3.5 shrink-0" />
                                                        <span>{deactivationState.reason}</span>
                                                    </div>
                                                ) : null}
                                            </div>
                                        </td>
                                    </tr>
                                )
                            })}
                        </tbody>
                    </table>
                </div>
            )}

            {!isLoading && rules.length > 0 ? (
                <div className="flex items-center gap-2 border-t border-slate-100 bg-slate-50/70 px-4 py-2.5 text-xs text-slate-500 sm:px-5">
                    <CalendarDays className="h-3.5 w-3.5" />
                    {tr(
                        'Rules are applied by scope precedence: vendor, then category, then global.',
                        'تُطبَّق القواعد حسب أسبقية النطاق: البائع ثم الفئة ثم العام.',
                    )}
                </div>
            ) : null}
        </section>
    )
}
