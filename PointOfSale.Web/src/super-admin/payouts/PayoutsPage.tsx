import { useEffect, useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { Loader2, Send, ShieldCheck } from 'lucide-react'
import { useToast } from '../../components/Toast'
import { useSuperAdminLocale } from '../i18n/useSuperAdminLocale'
import {
    GuidePanel,
    InlineNotice,
    KpiCard,
    PageHeader,
    StatusBadge,
    SuperAdminPage,
    saButton,
    saInput,
    saTable,
} from '../ui/SuperAdminUI'
import {
    completeSuperAdminVendorPayout,
    createVendorPayoutIdempotencyKey,
    getSuperAdminPayoutQueue,
    prepareSuperAdminVendorPayout,
    type SuperAdminVendorPayoutQueueCurrencySummary,
    type SuperAdminVendorPayoutMethodChoice,
    type SuperAdminVendorPayoutQueueItem,
} from './payoutsService'
import { requireCurrentPayoutExecutionAmount, requireCurrentPayoutMethodSelection } from './payoutFinancialAuthority'
import { DevelopmentSettlementPanel } from './DevelopmentSettlementPanel'
import { P520FTerminalPanel } from './P520FTerminalPanel'

const QUEUE_PAGE_SIZE = 20
const COMMERCEHUB_AUTHORITY_CURRENCY = 'EGP'
const UNSUPPORTED_COMMERCEHUB_CURRENCY = 'CommerceHub currency blocked'

function formatCurrency(amount: number, currency = 'EGP') {
    if (String(currency || '').trim().toUpperCase() !== COMMERCEHUB_AUTHORITY_CURRENCY) {
        return UNSUPPORTED_COMMERCEHUB_CURRENCY
    }

    return new Intl.NumberFormat('en-US', {
        style: 'currency',
        currency: COMMERCEHUB_AUTHORITY_CURRENCY,
        minimumFractionDigits: 2,
        maximumFractionDigits: 2,
    }).format(Number(amount || 0))
}

function formatDateTime(value?: string | null) {
    if (!value) {
        return 'No recent orders'
    }

    const date = new Date(value)
    if (Number.isNaN(date.getTime())) {
        return 'No recent orders'
    }

    return date.toLocaleString()
}

function formatSummaryCurrencyLabel(value: string): string {
    return String(value || '').trim().toUpperCase() === COMMERCEHUB_AUTHORITY_CURRENCY
        ? COMMERCEHUB_AUTHORITY_CURRENCY
        : UNSUPPORTED_COMMERCEHUB_CURRENCY
}

function formatCurrencySummaryAmount(amount: number, currency: string): string {
    return formatCurrency(amount, currency)
}

function getErrorMessage(error: unknown, fallback: string) {
    return error instanceof Error && error.message ? error.message : fallback
}

interface ExecuteModalState {
    item: SuperAdminVendorPayoutQueueItem
    amount: string
    payoutMethodId: string
    externalReferenceId: string
    bankTransactionId: string
    idempotencyKey: string
    confirmTransferExecuted: boolean
}

export default function PayoutsPage() {
    const queryClient = useQueryClient()
    const { showError, showSuccess } = useToast()
    const { tr } = useSuperAdminLocale()
    const [executeModal, setExecuteModal] = useState<ExecuteModalState | null>(null)
    const [page, setPage] = useState(1)

    const queueQuery = useQuery({
        queryKey: ['super-admin-payout-queue', page, QUEUE_PAGE_SIZE],
        queryFn: () => getSuperAdminPayoutQueue(page, QUEUE_PAGE_SIZE),
        staleTime: 30_000,
    })

    const queueResult = queueQuery.data
    const queue = queueResult?.payouts ?? []
    const currentExecuteItem = executeModal
        ? queue.find((item) => item.vendorId === executeModal.item.vendorId) ?? null
        : null
    const currentActivePayout = currentExecuteItem?.activePayout ?? null
    const currentCommittedPayout = currentActivePayout?.status === 'Pending' ? null : currentActivePayout
    const currentExecuteAmount = executeModal && currentExecuteItem
        ? Number(executeModal.amount)
        : null
    const queueMeta = queueResult?.meta
    const currencySummaries = queueResult?.currencySummaries ?? []
    const hasCurrencyMix = queueResult?.hasCurrencyMix ?? false
    const aggregateCurrency = queueResult?.aggregateCurrency ?? null
    const totalEligible = queueResult?.totalEligibleAmount ?? 0
    const totalHeld = queueResult?.totalHeldAmount ?? 0
    const totalPayable = queueResult?.totalPayableAmount ?? 0
    const totalCount = queueMeta?.totalCount ?? queue.length
    const pageStart = totalCount === 0 ? 0 : ((page - 1) * QUEUE_PAGE_SIZE) + 1
    const pageEnd = totalCount === 0 ? 0 : Math.min(page * QUEUE_PAGE_SIZE, totalCount)

    const executeMutation = useMutation({
        mutationFn: () => {
            if (!executeModal || !currentExecuteItem) {
                throw new Error('Current backend payout authority is no longer available for this vendor.')
            }

            const activePayout = currentExecuteItem.activePayout
            if (activePayout && activePayout.status !== 'Pending') {
                if (!currentExecuteItem.canCompletePayout) {
                    throw new Error('Current backend payout completion authority is no longer available for this vendor.')
                }

                if (!activePayout.externalTransferHandoffToken?.trim()) {
                    throw new Error('Current backend payout handoff authority is unavailable. Refresh before completing the payout.')
                }

                return completeSuperAdminVendorPayout(
                    currentExecuteItem.vendorId,
                    activePayout.payoutId,
                    activePayout.amount,
                    {
                        externalReferenceId: executeModal.externalReferenceId.trim() || undefined,
                        bankTransactionId: executeModal.bankTransactionId.trim() || undefined,
                        externalTransferHandoffToken: activePayout.externalTransferHandoffToken,
                        externalTransferConfirmedExecuted: executeModal.confirmTransferExecuted,
                    },
                )
            }

            if (!currentExecuteItem.canPreparePayout) {
                throw new Error('Current backend payout preparation authority is no longer available for this vendor.')
            }

            return prepareSuperAdminVendorPayout(currentExecuteItem.vendorId, {
                amount: activePayout
                    ? activePayout.amount
                    : requireCurrentPayoutExecutionAmount(Number(executeModal.amount), currentExecuteItem),
                payoutMethodId: activePayout?.payoutMethodId ?? requireCurrentPayoutMethodSelection(executeModal.payoutMethodId, currentExecuteItem).id,
                idempotencyKey: activePayout?.idempotencyKey ?? executeModal.idempotencyKey,
            })
        },
        onSuccess: (payout) => {
            showSuccess(payout.status === 'Processing'
                ? 'Payout handoff committed. External transfer may now be executed using the displayed payout intent.'
                : 'Completed payout evidence recorded and posted to the CommerceHub ledger.')
            setExecuteModal(null)
            queryClient.invalidateQueries({ queryKey: ['super-admin-payout-queue'] })
        },
        onError: (error) => showError(getErrorMessage(error, 'Unable to record payout evidence.')),
    })

    useEffect(() => {
        if (!queueMeta?.totalPages) {
            return
        }

        if (page > queueMeta.totalPages && queueMeta.totalPages > 0) {
            setPage((currentPage) => Math.min(currentPage, queueMeta.totalPages!))
        }
    }, [page, queueMeta?.totalPages])

    useEffect(() => {
        if (!executeModal || queueQuery.isFetching) {
            return
        }

        if (!currentExecuteItem || (!currentExecuteItem.canPreparePayout && !currentExecuteItem.canCompletePayout)) {
            setExecuteModal(null)
        }
    }, [currentExecuteItem, executeModal, queueQuery.isFetching])

    function openExecuteModal(item: SuperAdminVendorPayoutQueueItem) {
        const defaultMethod = item.payoutMethods.find((method) => method.isDefault) ?? item.payoutMethods[0]
        setExecuteModal({
            item,
            amount: item.activePayout
                ? item.activePayout.amount.toFixed(2)
                : item.eligibleAmount > 0 ? item.eligibleAmount.toFixed(2) : item.totalPayable.toFixed(2),
            payoutMethodId: item.activePayout?.payoutMethodId ?? defaultMethod?.id ?? '',
            externalReferenceId: '',
            bankTransactionId: '',
            idempotencyKey: item.activePayout?.idempotencyKey ?? createVendorPayoutIdempotencyKey(),
            confirmTransferExecuted: false,
        })
    }

    function resolveSelectedMethod(): SuperAdminVendorPayoutMethodChoice | null {
        if (!executeModal || !currentExecuteItem) {
            return null
        }

        return currentExecuteItem.payoutMethods.find((method) => method.id === executeModal.payoutMethodId) ?? null
    }

    const executeModalHasExternalEvidence = !!(
        executeModal?.externalReferenceId.trim() ||
        executeModal?.bankTransactionId.trim()
    )

    const methodLabels: Record<string, string> = {
        VodafoneCash: tr('Vodafone Cash', 'فودافون كاش'),
        InstaPay: tr('InstaPay', 'إنستاباي'),
        BankTransfer: tr('Bank transfer', 'تحويل بنكي'),
    }
    const methodLabel = (value: string) => methodLabels[value] ?? value

    const riskLabels: Record<string, string> = {
        VendorSuspended: tr('Vendor suspended', 'البائع معلّق'),
        GovernanceSuspended: tr('Governance suspended', 'معلّق بالحوكمة'),
        GovernanceBanned: tr('Governance banned', 'محظور بالحوكمة'),
        GovernanceFinancialLock: tr('Governance financial lock', 'قفل مالي بالحوكمة'),
        DeliveryHold: tr('Undelivered orders still holding balance', 'طلبات غير مسلّمة تحتجز الرصيد'),
        RefundWindowOpen: tr('Refund window still open', 'نافذة الاسترداد مفتوحة'),
        SettlementDelayActive: tr('Payout delay still active', 'تأخير التحويل ما زال فعالًا'),
        ActiveRefundRequest: tr('Active refund request', 'طلب استرداد نشط'),
        ExternalTransferReconciliationRequired: tr('Prior payout needs reconciliation', 'تحويل سابق يحتاج مطابقة'),
        StaleExternalTransferProcessing: tr('Prior payout stuck in processing', 'تحويل سابق عالق قيد المعالجة'),
        PayoutBlockedByAuthorityConflict: tr('Authority conflict', 'تعارض صلاحيات'),
        NoDefaultPayoutMethod: tr('Default payout method missing', 'وسيلة التحويل الافتراضية مفقودة'),
    }
    const riskLabel = (value: string) => riskLabels[value] ?? value

    const guide = {
        en: {
            title: 'Payout operations guide',
            purpose:
                'Prepares, commits, and reconciles vendor payouts. Preparation reserves earnings and issues a handoff token; completion records immutable external transfer evidence and posts cash and payable movements.',
            when:
                'Use it when vendors become eligible for payout, when a committed transfer has finished and needs reconciliation, or when investigating blocked payouts and risk flags.',
            steps: [
                'Review payable, eligible, held, and default method for each vendor.',
                'Read every risk flag and blocked reason before opening the payout action.',
                'Prepare the payout to commit the reservation, transfer intent, and handoff token.',
                'Execute the external transfer, then return to record its reference and bank transaction evidence.',
                'Only confirm completion after verifying the transfer actually executed.',
            ],
            statuses: [
                { label: 'Eligible', description: 'Earnings that passed delivery, refund-window, and readiness checks.' },
                { label: 'Held', description: 'Money still protected by delivery, refund, or readiness blockers.' },
                { label: 'Pending payout', description: 'A committed handoff is waiting for external transfer and reconciliation.' },
                { label: 'Clear', description: 'No blocking risk flags; normal payout review can proceed.' },
                { label: 'Reconciliation required', description: 'A prior transfer must be matched before another payout is allowed.' },
            ],
            safety: [
                'Preparation reserves vendor earnings; treat it as a financial commitment, not a draft.',
                'Never mark a transfer complete without real bank or provider evidence.',
                'Currency totals are never combined across currencies. Do not create a manual combined total.',
                'The overpayment and override controls are intentionally absent here; follow the displayed backend authority.',
            ],
        },
        ar: {
            title: 'دليل عمليات التحويل',
            purpose:
                'يجهّز ويُرحّل ويطابق تحويلات البائعين. التجهيز يحجز الأرباح ويصدر رمز تسليم، والإكمال يسجل إثبات التحويل الخارجي غير القابل للتغيير ويرحّل حركات النقد والمستحقات.',
            when:
                'استخدمه عند أهلية بائع للتحويل أو انتهاء تحويل مُرحّل وحاجته للمطابقة أو التحقيق في تحويلات محظورة وعلامات خطر.',
            steps: [
                'راجع المستحق والمؤهل والمحتجز والوسيلة الافتراضية لكل بائع.',
                'اقرأ كل علامة خطر وسبب حظر قبل فتح إجراء التحويل.',
                'جهّز التحويل لحجز المبلغ وتثبيت نية التحويل ورمز التسليم.',
                'نفّذ التحويل الخارجي ثم ارجع وسجّل مرجعه وإثبات العملية البنكية.',
                'لا تؤكد الإكمال إلا بعد التحقق من تنفيذ التحويل فعليًا.',
            ],
            statuses: [
                { label: 'مؤهل', description: 'أرباح اجتازت فحوص التسليم ونافذة الاسترداد والجاهزية.' },
                { label: 'محتجز', description: 'أموال ما زالت محمية بقيود التسليم أو الاسترداد أو الجاهزية.' },
                { label: 'تحويل معلّق', description: 'تسليم مُرحّل ينتظر التحويل الخارجي والمطابقة.' },
                { label: 'سليم', description: 'لا توجد علامات خطر حاجبة؛ يمكن متابعة المراجعة الطبيعية.' },
                { label: 'يتطلب مطابقة', description: 'يجب مطابقة تحويل سابق قبل السماح بتحويل آخر.' },
            ],
            safety: [
                'التجهيز يحجز أرباح البائع؛ تعامل معه كالتزام مالي لا كمسودة.',
                'لا تعتبر التحويل مكتملًا دون إثبات بنكي أو من المزود حقيقي.',
                'لا تُجمع الإجماليات عبر العملات المختلفة. لا تنشئ إجماليًا يدويًا.',
                'أدوات تجاوز الدفع غير موجودة هنا عمدًا؛ اتبع صلاحية الخادم المعروضة.',
            ],
        },
    }

    const renderCurrencySummaryCard = (summary: SuperAdminVendorPayoutQueueCurrencySummary) => (
        <article key={summary.currency} className="rounded-xl border border-emerald-200 bg-emerald-50 px-3.5 py-3 shadow-sm">
            <div className="flex items-center justify-between gap-2">
                <div>
                    <p className="text-[10px] font-semibold uppercase tracking-[0.14em] text-emerald-700">{tr('Queue currency', 'عملة القائمة')}</p>
                    <p className="mt-0.5 text-lg font-semibold text-emerald-950">{formatSummaryCurrencyLabel(summary.currency)}</p>
                </div>
                <p className="text-xs font-semibold text-emerald-800">{tr('Currency-scoped authority', 'صلاحية بعملة واحدة')}</p>
            </div>
            <div className="mt-2.5 grid gap-1.5 text-sm text-emerald-950/90">
                <div className="flex items-center justify-between gap-3">
                    <span>{tr('Eligible', 'المؤهل')}</span>
                    <span className="font-semibold">{formatCurrencySummaryAmount(summary.totalEligibleAmount, summary.currency)}</span>
                </div>
                <div className="flex items-center justify-between gap-3">
                    <span>{tr('Held', 'المحتجز')}</span>
                    <span className="font-semibold">{formatCurrencySummaryAmount(summary.totalHeldAmount, summary.currency)}</span>
                </div>
                <div className="flex items-center justify-between gap-3">
                    <span>{tr('Payable', 'المستحق')}</span>
                    <span className="font-semibold">{formatCurrencySummaryAmount(summary.totalPayableAmount, summary.currency)}</span>
                </div>
            </div>
        </article>
    )

    return (
        <SuperAdminPage>
            {import.meta.env.DEV && <><DevelopmentSettlementPanel /><P520FTerminalPanel /></>}
            <PageHeader
                eyebrow={tr('Super Admin · Money', 'الإدارة العليا · المال')}
                title={tr('Vendor payouts', 'تحويلات البائعين')}
                description={tr(
                    'Prepare a payout to reserve earnings and issue a handoff token, then reconcile the external transfer with immutable evidence.',
                    'جهّز التحويل لحجز الأرباح وإصدار رمز التسليم، ثم طابق التحويل الخارجي بإثبات غير قابل للتغيير.',
                )}
                aside={
                    <StatusBadge tone={hasCurrencyMix ? 'amber' : 'emerald'}>
                        {hasCurrencyMix ? tr('Mixed currencies', 'عملات مختلطة') : tr('Currency-scoped authority', 'صلاحية بعملة واحدة')}
                    </StatusBadge>
                }
            />

            <div className="grid gap-3 sm:grid-cols-2 xl:grid-cols-4">
                <KpiCard label={tr('Total payable', 'إجمالي المستحق')} value={aggregateCurrency ? formatCurrency(totalPayable, aggregateCurrency) : tr('Split by currency', 'مقسّم حسب العملة')} tone="slate" />
                <KpiCard label={tr('Eligible batch', 'الدفعة المؤهلة')} value={aggregateCurrency ? formatCurrency(totalEligible, aggregateCurrency) : tr('Split by currency', 'مقسّم حسب العملة')} tone="emerald" />
                <KpiCard label={tr('Held back', 'المحتجز')} value={aggregateCurrency ? formatCurrency(totalHeld, aggregateCurrency) : tr('Split by currency', 'مقسّم حسب العملة')} tone="amber" />
                <KpiCard label={tr('Vendors in queue', 'بائعون في القائمة')} value={totalCount} tone="sky" />
            </div>

            <GuidePanel guide={guide} />

            {hasCurrencyMix ? (
                <InlineNotice tone="amber" title={tr('Currency totals are split', 'الإجماليات مقسّمة حسب العملة')}>
                    {tr(
                        'Queue-wide totals are split by authoritative currency. Cross-currency payout aggregation is intentionally disabled so operators do not act on a fabricated combined money total.',
                        'إجماليات القائمة مقسّمة حسب العملة المعتمدة. تجميع التحويلات عبر العملات معطّل عمدًا حتى لا يعتمد المشغّلون على إجمالي مالي مُلفّق.',
                    )}
                </InlineNotice>
            ) : null}

            {currencySummaries.length > 0 ? (
                <section className="grid gap-3 sm:grid-cols-2 xl:grid-cols-3">
                    {currencySummaries.map(renderCurrencySummaryCard)}
                </section>
            ) : null}

            <section className="overflow-hidden rounded-2xl border border-slate-200 bg-white shadow-sm">
                <div className="flex items-center gap-3 border-b border-slate-100 px-4 py-4 sm:px-5">
                    <div className="flex h-9 w-9 items-center justify-center rounded-xl bg-slate-100 text-slate-700">
                        <ShieldCheck className="h-4 w-4" />
                    </div>
                    <div className="min-w-0">
                        <p className="text-[10px] font-semibold uppercase tracking-[0.16em] text-slate-500">{tr('Queue', 'القائمة')}</p>
                        <h2 className="mt-0.5 text-lg font-semibold text-slate-950">{tr('Vendor payout execution board', 'لوحة تنفيذ تحويلات البائعين')}</h2>
                        <p className="mt-1 text-sm text-slate-500">
                            {totalCount === 0
                                ? tr('No vendors currently require payout review.', 'لا يوجد بائعون يحتاجون مراجعة تحويل حاليًا.')
                                : hasCurrencyMix || aggregateCurrency == null
                                    ? tr(
                                          `Showing vendors ${pageStart}-${pageEnd} of ${totalCount}. Queue totals are split by currency.`,
                                          `عرض البائعين ${pageStart}-${pageEnd} من ${totalCount}. الإجماليات مقسّمة حسب العملة.`,
                                      )
                                    : tr(
                                          `Showing vendors ${pageStart}-${pageEnd} of ${totalCount}. Total queue payable: ${formatCurrency(totalPayable, aggregateCurrency)}.`,
                                          `عرض البائعين ${pageStart}-${pageEnd} من ${totalCount}. إجمالي المستحق: ${formatCurrency(totalPayable, aggregateCurrency)}.`,
                                      )}
                        </p>
                    </div>
                </div>

                <div className="overflow-x-auto">
                    <table className="min-w-full divide-y divide-slate-100">
                        <thead className="bg-slate-50/90">
                            <tr className={saTable.head}>
                                <th className={saTable.headerCell}>{tr('Vendor', 'البائع')}</th>
                                <th className={saTable.headerCell}>{tr('Payable', 'المستحق')}</th>
                                <th className={saTable.headerCell}>{tr('Eligible', 'المؤهل')}</th>
                                <th className={saTable.headerCell}>{tr('Default method', 'الوسيلة الافتراضية')}</th>
                                <th className={saTable.headerCell}>{tr('Risk flags', 'علامات الخطر')}</th>
                                <th className={saTable.headerCell}>{tr('Last order', 'آخر طلب')}</th>
                                <th className={`${saTable.headerCell} text-end`}>{tr('Action', 'إجراء')}</th>
                            </tr>
                        </thead>
                        <tbody className="divide-y divide-slate-100 bg-white">
                            {queue.length === 0 ? (
                                <tr>
                                    <td colSpan={7} className="px-4 py-12 text-center text-sm text-slate-500">
                                        {queueQuery.isFetching ? tr('Loading payouts…', 'جارٍ تحميل التحويلات…') : tr('No vendor payouts are pending right now.', 'لا توجد تحويلات بائعين معلّقة حاليًا.')}
                                    </td>
                                </tr>
                            ) : (
                                queue.map((item) => (
                                    <tr key={item.vendorId} className="align-top">
                                        <td className={saTable.cell}>
                                            <p className="font-semibold text-slate-950">{item.vendorName}</p>
                                            <p className="mt-0.5 text-sm text-slate-500">
                                                {tr('Paid out:', 'تم تحويله:')} {formatCurrency(item.completedPayoutsTotal, item.currency)}
                                            </p>
                                        </td>
                                        <td className={`${saTable.cell} text-sm text-slate-700`}>
                                            <p className="font-medium text-slate-900">{formatCurrency(item.totalPayable, item.currency)}</p>
                                            <p className="mt-0.5 text-slate-500">{tr('Held:', 'محتجز:')} {formatCurrency(item.holdbackAmount, item.currency)}</p>
                                        </td>
                                        <td className={`${saTable.cell} text-sm text-slate-700`}>
                                            <p className="font-medium text-emerald-700">{formatCurrency(item.eligibleAmount, item.currency)}</p>
                                            <p className="mt-0.5 text-slate-500">{tr('Pending ops:', 'عمليات معلّقة:')} {formatCurrency(item.pendingPayoutsTotal, item.currency)}</p>
                                        </td>
                                        <td className={`${saTable.cell} text-sm text-slate-700`}>
                                            {item.defaultPayoutMethodType ? (
                                                <>
                                                    <p className="font-medium text-slate-900">{methodLabel(item.defaultPayoutMethodType)}</p>
                                                    <p className="mt-0.5 break-words text-slate-500">{item.defaultPayoutDestinationValue}</p>
                                                </>
                                            ) : (
                                                <span className="text-slate-400">{tr('Not configured', 'غير مهيأة')}</span>
                                            )}
                                        </td>
                                        <td className={saTable.cell}>
                                            <div className="flex max-w-xs flex-wrap gap-1.5">
                                                {(item.riskFlags.length === 0 ? ['Clear'] : item.riskFlags).map((flag) => (
                                                    <StatusBadge key={flag} tone={flag === 'Clear' ? 'emerald' : 'amber'}>
                                                        {flag === 'Clear' ? tr('Clear', 'سليم') : riskLabel(flag)}
                                                    </StatusBadge>
                                                ))}
                                            </div>
                                            {item.executionBlockedReason ? (
                                                <p className="mt-2 max-w-xs text-xs leading-5 text-amber-900/80">
                                                    {item.executionBlockedReason}
                                                </p>
                                            ) : null}
                                        </td>
                                        <td className={`${saTable.cell} text-sm text-slate-600`}>{formatDateTime(item.lastOrderAt)}</td>
                                        <td className={`${saTable.cell} text-end`}>
                                            <button
                                                type="button"
                                                disabled={!item.canPreparePayout && !item.canCompletePayout}
                                                onClick={() => openExecuteModal(item)}
                                                className={`${saButton.primary} whitespace-nowrap px-3 py-2 text-xs`}
                                                title={item.executionBlockedReason ?? undefined}
                                            >
                                                <Send className="h-3.5 w-3.5" />
                                                {item.activePayout?.status === 'Pending'
                                                    ? tr('Resume preparation', 'استئناف التجهيز')
                                                    : item.activePayout ? tr('Record outcome', 'تسجيل النتيجة') : tr('Prepare payout', 'تجهيز التحويل')}
                                            </button>
                                        </td>
                                    </tr>
                                ))
                            )}
                        </tbody>
                    </table>
                </div>

                {queueMeta?.totalPages && queueMeta.totalPages > 1 ? (
                    <div className="flex flex-col gap-2 border-t border-slate-100 px-4 py-3 text-sm text-slate-600 md:flex-row md:items-center md:justify-between">
                        <p>
                            {tr(`Showing ${pageStart}-${pageEnd} of ${totalCount} vendors`, `عرض ${pageStart}-${pageEnd} من ${totalCount} بائع`)}
                        </p>
                        <div className="flex items-center gap-2">
                            <button
                                type="button"
                                onClick={() => setPage((currentPage) => Math.max(1, currentPage - 1))}
                                disabled={page <= 1 || queueQuery.isFetching}
                                className={saButton.secondary}
                            >
                                {tr('Previous', 'السابق')}
                            </button>
                            <span className="font-medium text-slate-900">
                                {tr(`Page ${page} of ${queueMeta.totalPages}`, `صفحة ${page} من ${queueMeta.totalPages}`)}
                            </span>
                            <button
                                type="button"
                                onClick={() => setPage((currentPage) => Math.min(queueMeta.totalPages!, currentPage + 1))}
                                disabled={page >= queueMeta.totalPages || queueQuery.isFetching}
                                className={saButton.secondary}
                            >
                                {tr('Next', 'التالي')}
                            </button>
                        </div>
                    </div>
                ) : null}
            </section>

            {executeModal && currentExecuteItem ? (
                <div className="fixed inset-0 z-50 flex items-center justify-center bg-slate-950/55 px-4">
                    <div className="w-full max-w-2xl rounded-[32px] border border-slate-200 bg-white p-6 shadow-2xl">
                        <div className="flex items-start justify-between gap-4">
                            <div>
                                <p className="text-[10px] font-semibold uppercase tracking-[0.16em] text-slate-500">
                                    {currentCommittedPayout ? tr('Reconcile external transfer', 'مطابقة التحويل الخارجي') : tr('Prepare payout handoff', 'تجهيز تسليم التحويل')}
                                </p>
                                <h3 className="mt-1 text-xl font-semibold text-slate-950">{currentExecuteItem.vendorName}</h3>
                                <p className="mt-1 text-sm text-slate-600">
                                    {currentActivePayout
                                        ? tr(`Prepared amount: ${formatCurrency(currentActivePayout.amount, currentActivePayout.currency)}`, `المبلغ المجهّز: ${formatCurrency(currentActivePayout.amount, currentActivePayout.currency)}`)
                                        : tr(`Eligible now: ${formatCurrency(currentExecuteItem.eligibleAmount, currentExecuteItem.currency)}`, `المؤهل الآن: ${formatCurrency(currentExecuteItem.eligibleAmount, currentExecuteItem.currency)}`)}
                                </p>
                                <p className="mt-1.5 max-w-xl text-sm leading-6 text-slate-500">
                                    {currentCommittedPayout
                                        ? tr('The payout handoff is durably committed. Record completion only after verifying the external transfer executed and capturing immutable bank or provider evidence.', 'تسليم التحويل مُرحّل بشكل دائم. لا تسجّل الإكمال إلا بعد التحقق من تنفيذ التحويل الخارجي والتقاط إثبات بنكي أو من المزود غير قابل للتغيير.')
                                        : tr('Commit this payout before sending money. POS will reserve the earnings and issue the transfer intent and handoff token required for later reconciliation.', 'رحّل هذا التحويل قبل إرسال المال. سيحجز POS الأرباح ويصدر نية التحويل ورمز التسليم اللازمين للمطابقة لاحقًا.')}
                                </p>
                            </div>
                            <button type="button" onClick={() => setExecuteModal(null)} className={saButton.secondary}>
                                {tr('Close', 'إغلاق')}
                            </button>
                        </div>

                        <div className="mt-4 grid gap-3">
                            <label className="block space-y-1.5">
                                <span className="text-sm font-medium text-slate-700">{tr('Amount', 'المبلغ')}</span>
                                <input
                                    type="number"
                                    min="0.01"
                                    step="0.01"
                                    value={executeModal.amount}
                                    onChange={(event) => setExecuteModal((current) => current ? { ...current, amount: event.target.value } : current)}
                                    disabled={!!currentActivePayout}
                                    className={saInput}
                                />
                            </label>

                            {!currentActivePayout ? <label className="block space-y-1.5">
                                <span className="text-sm font-medium text-slate-700">{tr('Payout method', 'وسيلة التحويل')}</span>
                                <select
                                    value={executeModal.payoutMethodId}
                                    onChange={(event) => setExecuteModal((current) => current ? { ...current, payoutMethodId: event.target.value } : current)}
                                    className={saInput}
                                >
                                    {currentExecuteItem.payoutMethods.map((method) => (
                                        <option key={method.id} value={method.id}>
                                            {methodLabel(method.methodType)} - {method.destinationValue}
                                        </option>
                                    ))}
                                </select>
                            </label> : null}

                            {currentCommittedPayout ? (
                                <div className="rounded-xl border border-sky-200 bg-sky-50 px-3.5 py-3 text-sm text-sky-950">
                                    <p className="font-semibold">{tr('Committed payout', 'تحويل مُرحّل')} {currentCommittedPayout.payoutId}</p>
                                    <p className="mt-1 break-all">{tr('Transfer intent:', 'نية التحويل:')} {currentCommittedPayout.externalTransferIdempotencyKey}</p>
                                    <p className="mt-1">{tr('Committed:', 'وقت الترحيل:')} {formatDateTime(currentCommittedPayout.externalTransferHandoffCommittedAtUtc)}</p>
                                    <p className="mt-1">{tr('Destination:', 'الوجهة:')} {currentCommittedPayout.destinationValueSnapshot}</p>
                                </div>
                            ) : null}

                            {currentActivePayout?.status === 'Pending' ? (
                                <div className="rounded-xl border border-sky-200 bg-sky-50 px-3.5 py-3 text-sm text-sky-950">
                                    {tr(
                                        `Resuming pending payout ${currentActivePayout.payoutId} for ${currentActivePayout.destinationValueSnapshot}. No external transfer is authorized until this handoff commits.`,
                                        `استئناف التحويل المعلّق ${currentActivePayout.payoutId} إلى ${currentActivePayout.destinationValueSnapshot}. لا يُصرح بأي تحويل خارجي حتى يُرحّل هذا التسليم.`,
                                    )}
                                </div>
                            ) : null}

                            {currentCommittedPayout ? <div className="grid gap-3 md:grid-cols-2">
                                <label className="block space-y-1.5">
                                    <span className="text-sm font-medium text-slate-700">{tr('External reference ID', 'مرجع خارجي')}</span>
                                    <input
                                        type="text"
                                        value={executeModal.externalReferenceId}
                                        onChange={(event) => setExecuteModal((current) => current ? { ...current, externalReferenceId: event.target.value } : current)}
                                        className={saInput}
                                        placeholder={tr('Provider transfer or settlement reference', 'مرجع التحويل أو التسوية من المزود')}
                                    />
                                </label>

                                <label className="block space-y-1.5">
                                    <span className="text-sm font-medium text-slate-700">{tr('Bank transaction ID', 'رقم العملية البنكية')}</span>
                                    <input
                                        type="text"
                                        value={executeModal.bankTransactionId}
                                        onChange={(event) => setExecuteModal((current) => current ? { ...current, bankTransactionId: event.target.value } : current)}
                                        className={saInput}
                                        placeholder={tr('Bank confirmation or ledger transaction ID', 'تأكيد البنك أو رقم العملية في الدفتر')}
                                    />
                                </label>
                            </div> : null}

                            {currentCommittedPayout ? <div className={`rounded-xl border px-3.5 py-3 text-sm ${executeModalHasExternalEvidence ? 'border-emerald-200 bg-emerald-50 text-emerald-950' : 'border-amber-200 bg-amber-50 text-amber-950'}`}>
                                {executeModalHasExternalEvidence
                                    ? tr('Execution evidence captured. POS can use this to recognize the already completed transfer.', 'تم التقاط إثبات التنفيذ. يمكن لـ POS استخدامه للاعتراف بالتحويل المكتمل.')
                                    : tr('Provide at least one immutable evidence field: external reference ID or bank transaction ID.', 'قدّم حقل إثبات غير قابل للتغيير واحدًا على الأقل: المرجع الخارجي أو رقم العملية البنكية.')}
                            </div> : null}

                            {currentCommittedPayout ? (
                                <label className="flex items-start gap-2.5 rounded-xl border border-amber-200 bg-amber-50 px-3.5 py-3 text-sm text-amber-950">
                                    <input
                                        type="checkbox"
                                        checked={executeModal.confirmTransferExecuted}
                                        onChange={(event) => setExecuteModal((current) => current ? { ...current, confirmTransferExecuted: event.target.checked } : current)}
                                        className="mt-0.5 h-4 w-4"
                                    />
                                    <span>{tr('I verified that this external transfer executed. Completing the payout will post immutable cash and vendor-payable movements.', 'تحققت من تنفيذ هذا التحويل الخارجي. إكمال التحويل سيرحّل حركات نقد ومستحقات بائع غير قابلة للتغيير.')}</span>
                                </label>
                            ) : null}

                            {!currentActivePayout && currentExecuteAmount != null && currentExecuteAmount - currentExecuteItem.eligibleAmount > 0.005 ? (
                                <div className="rounded-xl border border-rose-200 bg-rose-50 px-3.5 py-3 text-sm text-rose-950">
                                    {tr('Amount exceeds current backend eligible payout authority.', 'المبلغ يتجاوز صلاحية التحويل المؤهل الحالية من الخادم.')}
                                </div>
                            ) : null}

                            {!currentActivePayout && resolveSelectedMethod() ? (
                                <div className="rounded-xl border border-sky-200 bg-sky-50 px-3.5 py-3 text-sm text-sky-950">
                                    {tr(
                                        `Recording payout to ${methodLabel(resolveSelectedMethod()!.methodType)} at ${resolveSelectedMethod()!.destinationValue}`,
                                        `تسجيل التحويل إلى ${methodLabel(resolveSelectedMethod()!.methodType)} على ${resolveSelectedMethod()!.destinationValue}`,
                                    )}
                                </div>
                            ) : null}
                        </div>

                        <div className="mt-4 flex justify-end">
                            <button
                                type="button"
                                disabled={
                                    executeMutation.isPending ||
                                    (!currentExecuteItem.canPreparePayout && !currentExecuteItem.canCompletePayout) ||
                                    (!currentActivePayout && (!executeModal.payoutMethodId || !resolveSelectedMethod())) ||
                                    currentExecuteAmount == null ||
                                    !Number.isFinite(currentExecuteAmount) ||
                                    currentExecuteAmount <= 0 ||
                                    (!currentActivePayout && currentExecuteAmount - currentExecuteItem.eligibleAmount > 0.005) ||
                                    (!!currentCommittedPayout && (!executeModalHasExternalEvidence || !executeModal.confirmTransferExecuted))
                                }
                                onClick={() => executeMutation.mutate()}
                                className={saButton.primary}
                            >
                                {executeMutation.isPending ? <Loader2 className="h-4 w-4 animate-spin" /> : <Send className="h-4 w-4" />}
                                {currentCommittedPayout ? tr('Record completed transfer', 'تسجيل التحويل المكتمل') : tr('Commit payout handoff', 'ترحيل تسليم التحويل')}
                            </button>
                        </div>
                    </div>
                </div>
            ) : null}
        </SuperAdminPage>
    )
}
