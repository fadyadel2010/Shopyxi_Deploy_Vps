import { ActivitySquare, AlertTriangle, ShieldCheck, Workflow } from 'lucide-react'
import { useSuperAdminLocale } from '../i18n/useSuperAdminLocale'
import { EmptyState, ErrorState, LoadingState, SectionCard, StatusBadge, saTable } from '../ui/SuperAdminUI'
import type { SuperAdminFinancialIntegrityCheck } from './financeService'
import { financeIntegrityLabel, financeIssueLabel } from './financePresentation'

interface FinanceReconciliationPanelProps {
    data?: SuperAdminFinancialIntegrityCheck
    isLoading?: boolean
    errorMessage?: string | null
}

const COMMERCEHUB_AUTHORITY_CURRENCY = 'EGP'
const UNSUPPORTED_COMMERCEHUB_CURRENCY = 'CommerceHub currency blocked'

const counters = [
    { key: 'orderFlowMismatchCount', label: 'Order flow', labelAr: 'تدفق الطلبات' },
    { key: 'refundFlowMismatchCount', label: 'Refund flow', labelAr: 'تدفق المرتجعات' },
    { key: 'promoUsageWithoutLedgerCount', label: 'Usage w/o ledger', labelAr: 'استخدام دون قيد' },
    { key: 'promoLedgerWithoutUsageCount', label: 'Ledger w/o usage', labelAr: 'قيد دون استخدام' },
    { key: 'duplicatePromoUsageCount', label: 'Duplicate usage', labelAr: 'استخدام مكرر' },
    { key: 'missingRefundReversalCount', label: 'Missing reversal', labelAr: 'عكس مفقود' },
    { key: 'orphanTransactionsCount', label: 'Orphan refs', labelAr: 'مراجع يتيمة' },
    { key: 'duplicateEntryCount', label: 'Duplicates', labelAr: 'قيود مكررة' },
    { key: 'payableMismatchCount', label: 'Payables', labelAr: 'مستحقات' },
    { key: 'receivableMismatchCount', label: 'Receivables', labelAr: 'مبالغ مستحقة لنا' },
] as const

export default function FinanceReconciliationPanel({
    data,
    isLoading,
    errorMessage,
}: FinanceReconciliationPanelProps) {
    const { tr, formatNumber, formatDateTime, formatCurrency } = useSuperAdminLocale()
    const available = !!data && !errorMessage

    const formatScopedCurrency = (value: number | null | undefined, currency = 'EGP') => {
        if (String(currency || '').trim().toUpperCase() !== COMMERCEHUB_AUTHORITY_CURRENCY) {
            return tr(UNSUPPORTED_COMMERCEHUB_CURRENCY, 'عملة CommerceHub غير مدعومة')
        }
        return formatCurrency(Number(value ?? 0), COMMERCEHUB_AUTHORITY_CURRENCY)
    }

    return (
        <SectionCard
            title={tr('Ledger integrity and reference-level consistency', 'سلامة دفتر الأستاذ والاتساق على مستوى المراجع')}
            description={tr(
                'The dashboard validates order math, refund math, promo usage backfills, promo-ledger linkage, missing reversals, and orphan ledger references. Global debit/credit parity is intentionally marked not applicable because CommerceHub uses an operational bucket ledger instead of a general ledger.',
                'تتحقق اللوحة من حسابات الطلبات والمرتجعات وتعبئة استخدام العروض وربطها بالدفتر والعكس المفقود والمراجع اليتيمة. ويُستبعد توازن المدين/الدائن العام عمدًا لأن CommerceHub يستخدم دفتر حزم تشغيليًا لا دفترًا عامًا.',
            )}
            actions={
                <StatusBadge tone={available && data.status === 'OK' ? 'emerald' : available && data.status === 'ISSUES_DETECTED' ? 'rose' : 'slate'}>
                    {available && data.status === 'OK' ? <ShieldCheck className="h-3.5 w-3.5" /> : <AlertTriangle className="h-3.5 w-3.5" />}
                    {financeIntegrityLabel(errorMessage ? 'UNAVAILABLE' : data?.status, tr)}
                </StatusBadge>
            }
        >
            {isLoading && !data ? <LoadingState label={tr('Loading integrity check…', 'جارٍ تحميل فحص السلامة…')} /> : null}

            {errorMessage ? (
                <div className="mb-3">
                    <ErrorState title={tr('Integrity check unavailable', 'فحص السلامة غير متاح')} description={errorMessage} />
                </div>
            ) : null}

            {data && available ? (
                <div className="space-y-3">
                    <p className="rounded-lg border border-amber-200 bg-amber-50 px-3.5 py-2.5 text-sm text-amber-950">
                        {data.includesGlobalLedgerChecks
                            ? tr('Selected operational-ledger checks only. This is not a complete accounting certification.', 'فحوص محددة لدفتر العمليات فقط. ليست شهادة محاسبية شاملة.')
                            : tr('Global balance and duplicate-pair checks are not included in this filtered scan. A zero flag count does not certify all financial invariants.', 'لا يشمل هذا الفحص المصفّى فحوص الأرصدة العامة وأزواج القيود المكررة. صفر علامات لا يثبت جميع الثوابت المالية.')}
                    </p>
                    <div className="grid gap-2 sm:grid-cols-2 xl:grid-cols-5">
                        {counters.map((counter) => {
                            const notChecked = !data.includesGlobalLedgerChecks &&
                                ['duplicateEntryCount', 'payableMismatchCount', 'receivableMismatchCount'].includes(counter.key)
                            const rawValue = Number(data[counter.key] ?? 0)
                            const hasIssue = rawValue > 0
                            return (
                                <div
                                    key={counter.key}
                                    className={`rounded-xl border px-3.5 py-2.5 shadow-sm ${hasIssue ? 'border-rose-200 bg-rose-50 text-rose-900' : 'border-slate-200 bg-white text-slate-950'}`}
                                >
                                    <div className="flex items-center justify-between gap-2">
                                        <p className="text-[10px] font-semibold uppercase tracking-[0.12em] opacity-70">
                                            {tr(counter.label, counter.labelAr)}
                                        </p>
                                        <Workflow className="h-3 w-3 opacity-40" />
                                    </div>
                                    <p className="mt-1 text-lg font-semibold" dir="auto">{notChecked ? tr('Not checked', 'لم يُفحص') : formatNumber(rawValue)}</p>
                                </div>
                            )
                        })}
                    </div>

                    <div className="grid gap-2 rounded-xl border border-slate-200 bg-slate-50/70 p-2.5 md:grid-cols-2 xl:grid-cols-4">
                        <div className="rounded-lg border border-slate-200 bg-white px-3 py-2.5">
                            <p className="text-[10px] font-semibold uppercase tracking-[0.12em] text-slate-500">{tr('Executed', 'وقت التنفيذ')}</p>
                            <p className="mt-1 text-sm font-semibold text-slate-950">{formatDateTime(data.executedAtUtc)}</p>
                        </div>
                        <div className="rounded-lg border border-slate-200 bg-white px-3 py-2.5">
                            <p className="text-[10px] font-semibold uppercase tracking-[0.12em] text-slate-500">{tr('Total flags', 'إجمالي العلامات')}</p>
                            <p className="mt-1 text-sm font-semibold text-slate-950">{formatNumber(data.totalIssueCount)}</p>
                        </div>
                        <div className="rounded-lg border border-slate-200 bg-white px-3 py-2.5">
                            <p className="text-[10px] font-semibold uppercase tracking-[0.12em] text-slate-500">{tr('Debit/credit parity', 'توازن المدين/الدائن')}</p>
                            <p className="mt-1 text-sm font-semibold text-slate-950">{tr('Not applicable', 'غير منطبق')}</p>
                        </div>
                        <div className="rounded-lg border border-slate-200 bg-white px-3 py-2.5">
                            <p className="text-[10px] font-semibold uppercase tracking-[0.12em] text-slate-500">{tr('Scope', 'النطاق')}</p>
                            <p className="mt-1 text-sm font-semibold text-slate-950">{data.includesGlobalLedgerChecks ? tr('Full ledger', 'دفتر الأستاذ الكامل') : tr('Selected reporting filters', 'مرشحات التقرير المحددة')}</p>
                        </div>
                    </div>

                    {data.debitCreditParityNote ? (
                        <p className="rounded-lg border border-slate-200 bg-slate-50 px-3.5 py-2.5 text-sm text-slate-600">
                            {tr('CommerceHub uses an operational bucket ledger, not a full double-entry general ledger. Global debit/credit equality is not an applicable test.', 'يستخدم CommerceHub دفتر عمليات حسب الحزم، وليس دفترًا عامًا متكاملًا بالقيد المزدوج. تساوي إجمالي المدين والدائن ليس فحصًا منطبقًا.')}
                        </p>
                    ) : null}

                    <div>
                        <div className="flex items-center gap-2 text-sm font-semibold text-slate-900">
                            <ActivitySquare className="h-4 w-4" />
                            {tr('Sample issues', 'مشاكل نموذجية')}
                        </div>

                        {data.issues.length === 0 ? (
                            <div className="mt-2.5">
                                <EmptyState
                                    title={tr('No sample inconsistencies', 'لا توجد تعارضات نموذجية')}
                                    description={tr(
                                        'No sample inconsistencies were returned for the selected scope.',
                                        'لم تُرجع أي تعارضات نموذجية في النطاق المحدد.',
                                    )}
                                />
                            </div>
                        ) : (
                            <div className={`mt-2.5 ${saTable.shell}`}>
                                <table className={saTable.table}>
                                    <thead className={saTable.head}>
                                        <tr>
                                            <th className={saTable.headerCell}>{tr('Severity', 'الخطورة')}</th>
                                            <th className={saTable.headerCell}>{tr('Type', 'النوع')}</th>
                                            <th className={saTable.headerCell}>{tr('Reference', 'المرجع')}</th>
                                            <th className={saTable.headerCell}>{tr('Summary', 'الملخص')}</th>
                                            <th className={saTable.headerCell}>{tr('Expected', 'المتوقع')}</th>
                                            <th className={saTable.headerCell}>{tr('Actual', 'الفعلي')}</th>
                                        </tr>
                                    </thead>
                                    <tbody className="divide-y divide-slate-100 bg-white">
                                        {data.issues.map((issue) => (
                                            <tr key={`${issue.issueType}-${issue.referenceId ?? 'none'}-${issue.summary}`} className="align-top">
                                                <td className={saTable.cell}>
                                                    <StatusBadge tone={issue.severity === 'ERROR' ? 'rose' : 'amber'}>
                                                        {issue.severity === 'ERROR' ? tr('Error', 'خطأ') : tr('Warning', 'تحذير')}
                                                    </StatusBadge>
                                                </td>
                                                <td className={`${saTable.cell} font-medium text-slate-900`}>{financeIssueLabel(issue.issueType, tr)}</td>
                                                <td className={`${saTable.cell} text-slate-600`}>
                                                    <div>{issue.referenceType === 'Order' ? tr('Order', 'طلب') : issue.referenceType === 'Refund' ? tr('Refund', 'استرداد') : tr('Financial record', 'سجل مالي')}</div>
                                                    <div className="mt-0.5 break-all text-xs">{issue.referenceId ?? '—'}</div>
                                                    <div className="mt-0.5 text-xs">{formatDateTime(issue.occurredAtUtc ?? undefined)}</div>
                                                </td>
                                                <td className={`${saTable.cell} text-slate-600`}>{financeIssueLabel(issue.issueType, tr)}</td>
                                                <td className={`${saTable.cell} text-slate-700`}>
                                                    {issue.expectedAmount == null ? '—' : formatScopedCurrency(issue.expectedAmount, issue.currency || 'EGP')}
                                                </td>
                                                <td className={`${saTable.cell} text-slate-700`}>
                                                    {issue.actualAmount == null ? '—' : formatScopedCurrency(issue.actualAmount, issue.currency || 'EGP')}
                                                </td>
                                            </tr>
                                        ))}
                                    </tbody>
                                </table>
                            </div>
                        )}
                    </div>
                </div>
            ) : null}
        </SectionCard>
    )
}
