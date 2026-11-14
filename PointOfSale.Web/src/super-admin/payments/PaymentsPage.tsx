import { useEffect, useMemo, useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { AlertTriangle, CheckCircle2, CreditCard, Loader2, Search, Upload } from 'lucide-react'
import { useToast } from '../../components/Toast'
import { useSuperAdminLocale } from '../i18n/useSuperAdminLocale'
import { getSuperAdminOrderTenants } from '../orders/ordersService'
import {
    GuidePanel,
    InlineNotice,
    KpiCard,
    PageHeader,
    SectionCard,
    StatusBadge,
    SuperAdminPage,
    saButton,
    saInput,
    saTable,
} from '../ui/SuperAdminUI'
import {
    SUPER_ADMIN_VENDOR_PAYMENT_STATUSES,
    approveSuperAdminVendorPayment,
    createSuperAdminVendorPaymentMethod,
    getSuperAdminVendorPaymentMethods,
    getSuperAdminVendorPaymentProofUrl,
    getSuperAdminVendorPayments,
    rejectSuperAdminVendorPayment,
    type SuperAdminVendorPaymentFilters,
    type SuperAdminVendorPaymentMethod,
    type SuperAdminVendorPaymentRequestItem,
} from './paymentsService'
import { requireCurrentSuperAdminPaymentSelection } from './paymentOwnershipAuthority'

const PAGE_SIZE_OPTIONS = [20, 50, 100]

function getErrorMessage(error: unknown, fallback: string) {
    return error instanceof Error && error.message ? error.message : fallback
}

interface ActionModalState {
    mode: 'approve' | 'reject'
    paymentRequestId: string
}

export default function PaymentsPage() {
    const queryClient = useQueryClient()
    const { showError, showSuccess } = useToast()
    const { tr, formatCurrency, formatDateTime, formatNumber } = useSuperAdminLocale()
    const [filters, setFilters] = useState<SuperAdminVendorPaymentFilters>({
        page: 1,
        pageSize: 20,
        status: 'PendingVerification',
        vendorId: '',
        search: '',
    })
    const [methodForm, setMethodForm] = useState({
        name: '',
        destinationValue: '',
        instructions: '',
        isActive: true,
    })
    const [actionModal, setActionModal] = useState<ActionModalState | null>(null)
    const [actionReason, setActionReason] = useState('')
    const [allowOverpayment, setAllowOverpayment] = useState(false)

    const methodsQuery = useQuery({
        queryKey: ['super-admin-vendor-payment-methods'],
        queryFn: getSuperAdminVendorPaymentMethods,
    })

    const tenantsQuery = useQuery({
        queryKey: ['super-admin-vendor-payment-tenants'],
        queryFn: getSuperAdminOrderTenants,
        staleTime: 60_000,
    })

    const paymentsQuery = useQuery({
        queryKey: ['super-admin-vendor-payments', filters],
        queryFn: () => getSuperAdminVendorPayments(filters),
    })

    const createMethodMutation = useMutation({
        mutationFn: () => createSuperAdminVendorPaymentMethod(methodForm),
        onSuccess: () => {
            showSuccess(tr('Payment method created.', 'تم إنشاء وسيلة الدفع.'))
            setMethodForm({ name: '', destinationValue: '', instructions: '', isActive: true })
            queryClient.invalidateQueries({ queryKey: ['super-admin-vendor-payment-methods'] })
        },
        onError: (error) => showError(getErrorMessage(error, tr('Unable to create payment method.', 'تعذّر إنشاء وسيلة الدفع.'))),
    })

    const approveMutation = useMutation({
        mutationFn: () =>
            approveSuperAdminVendorPayment(actionModal!.paymentRequestId, {
                reason: actionReason,
                allowOverpayment,
            }),
        onSuccess: () => {
            showSuccess(tr('Payment request approved and posted to the ledger.', 'تمت الموافقة على طلب الدفع وترحيله إلى دفتر الأستاذ.'))
            closeActionModal()
            queryClient.invalidateQueries({ queryKey: ['super-admin-vendor-payments'] })
        },
        onError: (error) => showError(getErrorMessage(error, tr('Unable to approve the payment request.', 'تعذّرت الموافقة على طلب الدفع.'))),
    })

    const rejectMutation = useMutation({
        mutationFn: () =>
            rejectSuperAdminVendorPayment(actionModal!.paymentRequestId, {
                reason: actionReason,
            }),
        onSuccess: () => {
            showSuccess(tr('Payment request rejected.', 'تم رفض طلب الدفع.'))
            closeActionModal()
            queryClient.invalidateQueries({ queryKey: ['super-admin-vendor-payments'] })
        },
        onError: (error) => showError(getErrorMessage(error, tr('Unable to reject the payment request.', 'تعذّر رفض طلب الدفع.'))),
    })

    const requests = paymentsQuery.isFetching || paymentsQuery.isError ? [] : paymentsQuery.data?.requests ?? []
    const currentActionRequest = requireCurrentSuperAdminPaymentSelection(actionModal?.paymentRequestId ?? null, requests)
    const meta = paymentsQuery.data?.meta
    const totalPages = Math.max(1, Number((meta as { totalPages?: number })?.totalPages || 1))
    const pendingCount = requests.filter((request) => request.status === 'PendingVerification').length
    const approvedCount = requests.filter((request) => request.status === 'Approved').length

    const isActionSubmitting = approveMutation.isPending || rejectMutation.isPending

    useEffect(() => {
        if (actionModal && !paymentsQuery.isFetching && !currentActionRequest) {
            closeActionModal()
        }
    }, [actionModal, currentActionRequest, paymentsQuery.isFetching])

    const methodCards = useMemo(() => methodsQuery.data ?? [], [methodsQuery.data])

    function updateFilters(next: Partial<SuperAdminVendorPaymentFilters>) {
        setFilters((current) => ({
            ...current,
            ...next,
            page: next.page ?? (next.pageSize || next.status !== undefined || next.vendorId !== undefined || next.search !== undefined ? 1 : current.page),
        }))
    }

    function openActionModal(mode: 'approve' | 'reject', request: SuperAdminVendorPaymentRequestItem) {
        setActionModal({ mode, paymentRequestId: request.paymentRequestId })
        setActionReason('')
        setAllowOverpayment(false)
    }

    function closeActionModal() {
        setActionModal(null)
        setActionReason('')
        setAllowOverpayment(false)
    }

    async function handleOpenProof(paymentRequestId: string) {
        try {
            const proof = await getSuperAdminVendorPaymentProofUrl(paymentRequestId)
            window.open(proof.fileUrl, '_blank', 'noopener,noreferrer')
        } catch (error) {
            showError(getErrorMessage(error, tr('Unable to open payment proof.', 'تعذّر فتح إثبات الدفع.')))
        }
    }

    function handleActionConfirm() {
        if (!actionModal || !currentActionRequest || !actionReason.trim()) {
            showError(tr('A reason is required before continuing.', 'السبب مطلوب قبل المتابعة.'))
            return
        }

        if (actionModal.mode === 'approve') {
            approveMutation.mutate()
            return
        }

        rejectMutation.mutate()
    }

    const statusLabels: Record<string, string> = {
        PendingVerification: tr('Pending verification', 'قيد التحقق'),
        Approved: tr('Approved', 'معتمد'),
        Rejected: tr('Rejected', 'مرفوض'),
    }

    const statusTone = (status: string): 'emerald' | 'rose' | 'amber' =>
        status === 'Approved' ? 'emerald' : status === 'Rejected' ? 'rose' : 'amber'

    const guide = {
        en: {
            title: 'Vendor payments guide',
            purpose:
                'Reviews proof-backed vendor debt repayments. Approval posts paired CommerceHub settlement entries; rejection leaves the ledger untouched and preserves the audit trail.',
            when:
                'Use it when vendors submit transfer proof, when pending requests need verification, or when an amount may exceed the current debt and needs a deliberate overpayment decision.',
            steps: [
                'Set the status and vendor filters, then search by method or transfer reference.',
                'Open the proof in a new tab and compare it with the vendor, amount, and destination.',
                'Choose Approve or Reject and enter a clear audit reason.',
                'Approve only when the proof is valid; the request is then posted to the ledger.',
                'Refresh and confirm the request moved out of the pending queue.',
            ],
            statuses: [
                { label: 'Pending verification', description: 'Proof has been submitted and no ledger posting exists yet.' },
                { label: 'Approved', description: 'The request was verified and posted to the ledger.' },
                { label: 'Rejected', description: 'The request was declined; no money movement was posted.' },
                { label: 'Overpayment allowed', description: 'An explicit override that lets approval proceed even if the amount exceeds current debt.' },
            ],
            safety: [
                'Approval is a ledger posting and cannot be casually undone; verify the proof and amount first.',
                'The overpayment override is dangerous. Use it only with documented evidence and a clear reason.',
                'Never approve based on a screenshot alone; confirm the destination belongs to the platform.',
                'Rejections remain auditable; do not delete or hide rejected requests.',
            ],
        },
        ar: {
            title: 'دليل مدفوعات البائعين',
            purpose:
                'يراجع سداد مديونية البائعين المدعوم بالإثبات. الموافقة ترحّل قيود تسوية CommerceHub المزدوجة، والرفض يترك الدفتر دون تغيير مع حفظ سجل التدقيق.',
            when:
                'استخدمه عند إرسال البائعين لإثبات التحويل أو الحاجة للتحقق من طلبات معلّقة أو عندما يتجاوز المبلغ المديونية الحالية ويحتاج قرارًا صريحًا.',
            steps: [
                'حدد الحالة والبائع ثم ابحث بوسيلة الدفع أو مرجع التحويل.',
                'افتح الإثبات في تبويب جديد وقارنه بالبائع والمبلغ والوجهة.',
                'اختر الموافقة أو الرفض وأدخل سببًا واضحًا للتدقيق.',
                'وافق فقط عند صحة الإثبات؛ عندها يُرحّل الطلب إلى دفتر الأستاذ.',
                'حدّث الصفحة وتأكد من خروج الطلب من قائمة الانتظار.',
            ],
            statuses: [
                { label: 'قيد التحقق', description: 'تم إرسال الإثبات ولا يوجد ترحيل في الدفتر بعد.' },
                { label: 'معتمد', description: 'تم التحقق من الطلب وترحيله إلى دفتر الأستاذ.' },
                { label: 'مرفوض', description: 'تم رفض الطلب دون ترحيل أي حركة مالية.' },
                { label: 'السماح بالدفع الزائد', description: 'تجاوز صريح يسمح بالموافقة حتى لو تجاوز المبلغ المديونية الحالية.' },
            ],
            safety: [
                'الموافقة ترحيل في الدفتر ولا يمكن التراجع عنها بسهولة؛ تحقق من الإثبات والمبلغ أولًا.',
                'تجاوز الدفع الزائد خطير. استخدمه فقط مع أدلة موثقة وسبب واضح.',
                'لا توافق اعتمادًا على لقطة شاشة فقط؛ تأكد أن الوجهة تابعة للمنصة.',
                'تبقى الطلبات المرفوضة قابلة للتدقيق؛ لا تحذفها أو تخفها.',
            ],
        },
    }

    return (
        <SuperAdminPage>
            <PageHeader
                eyebrow={tr('Super Admin · Money', 'الإدارة العليا · المال')}
                title={tr('Vendor payments', 'مدفوعات البائعين')}
                description={tr(
                    'Proof-backed debt repayment queue. Approval posts to the ledger; rejection stays outside it and remains auditable.',
                    'قائمة سداد مديونية مدعومة بالإثبات. الموافقة تُرحّل للدفتر والرفض يبقى خارجه وقابلًا للتدقيق.',
                )}
                aside={
                    <StatusBadge tone="amber">
                        {tr('Default view', 'العرض الافتراضي')}: {tr('Pending', 'معلّق')}
                    </StatusBadge>
                }
            />

            <div className="grid gap-3 sm:grid-cols-3">
                <KpiCard label={tr('Pending review', 'قيد المراجعة')} value={formatNumber(pendingCount)} hint={tr('Requests waiting for manual verification.', 'طلبات تنتظر التحقق اليدوي.')} tone="amber" />
                <KpiCard label={tr('Approved in view', 'معتمد في العرض')} value={formatNumber(approvedCount)} hint={tr('Row count only; ledger money stays backend-owned.', 'عدد الصفوف فقط؛ الأموال في الدفتر مملوكة للخادم.')} tone="emerald" />
                <KpiCard label={tr('Payment methods', 'وسائل الدفع')} value={formatNumber(methodCards.length)} hint={tr('Platform destinations shown to vendors.', 'وجهات المنصة المعروضة للبائعين.')} tone="slate" />
            </div>

            <GuidePanel guide={guide} />

            <div className="grid grid-cols-1 gap-4 xl:grid-cols-[minmax(0,0.9fr)_minmax(0,1.1fr)] [&>article]:min-w-0">
                <SectionCard
                    title={tr('Payment methods', 'وسائل الدفع')}
                    description={tr('Platform destinations shown to vendors.', 'وجهات المنصة التي يراها البائعون.')}
                    headerAside={<CreditCard className="h-4 w-4 text-slate-500" />}
                >
                    <div className="space-y-2.5">
                        {methodCards.map((method: SuperAdminVendorPaymentMethod) => (
                            <div key={method.id} className="rounded-xl border border-slate-200 bg-slate-50 px-3.5 py-3">
                                <div className="flex items-start justify-between gap-3">
                                    <div className="min-w-0">
                                        <p className="font-semibold text-slate-950">{method.name}</p>
                                        <p className="mt-0.5 break-words text-sm text-slate-600">{method.destinationValue}</p>
                                        <p className="mt-1 text-sm leading-5 text-slate-500">{method.instructions}</p>
                                    </div>
                                    <StatusBadge tone={method.isActive ? 'emerald' : 'slate'}>
                                        {method.isActive ? tr('Active', 'نشطة') : tr('Inactive', 'غير نشطة')}
                                    </StatusBadge>
                                </div>
                            </div>
                        ))}
                        {methodCards.length === 0 ? (
                            <p className="rounded-xl border border-dashed border-slate-300 bg-slate-50 px-3.5 py-4 text-sm text-slate-500">
                                {tr('No payment methods configured yet.', 'لا توجد وسائل دفع مضافة بعد.')}
                            </p>
                        ) : null}
                    </div>

                    <div className="mt-4 space-y-2.5 border-t border-slate-100 pt-4">
                        <input value={methodForm.name} onChange={(event) => setMethodForm((current) => ({ ...current, name: event.target.value }))} placeholder={tr('Method name', 'اسم الوسيلة')} className={saInput} />
                        <input value={methodForm.destinationValue} onChange={(event) => setMethodForm((current) => ({ ...current, destinationValue: event.target.value }))} placeholder={tr('Destination value', 'قيمة الوجهة')} className={saInput} />
                        <textarea value={methodForm.instructions} onChange={(event) => setMethodForm((current) => ({ ...current, instructions: event.target.value }))} placeholder={tr('Instructions shown to vendors', 'التعليمات المعروضة للبائعين')} rows={3} className={saInput} />
                        <button type="button" onClick={() => createMethodMutation.mutate()} disabled={createMethodMutation.isPending} className={saButton.primary}>
                            {createMethodMutation.isPending ? <Loader2 className="h-4 w-4 animate-spin" /> : <Upload className="h-4 w-4" />}
                            {tr('Add payment method', 'إضافة وسيلة دفع')}
                        </button>
                    </div>
                </SectionCard>

                <SectionCard
                    title={tr('Repayment requests', 'طلبات السداد')}
                    description={tr(
                        'Verify proof, amount, and destination before approving.',
                        'تحقق من الإثبات والمبلغ والوجهة قبل الموافقة.',
                    )}
                >
                    <div className="grid gap-2 md:grid-cols-2 xl:grid-cols-4">
                        <select aria-label={tr('Repayment status', 'حالة السداد')} value={filters.status ?? ''} onChange={(event) => updateFilters({ status: event.target.value as SuperAdminVendorPaymentFilters['status'] })} className={saInput}>
                            <option value="">{tr('All statuses', 'كل الحالات')}</option>
                            {SUPER_ADMIN_VENDOR_PAYMENT_STATUSES.map((status) => <option key={status} value={status}>{statusLabels[status] ?? status}</option>)}
                        </select>
                        <select aria-label={tr('Repayment vendor', 'بائع السداد')} value={filters.vendorId ?? ''} onChange={(event) => updateFilters({ vendorId: event.target.value })} className={saInput}>
                            <option value="">{tenantsQuery.isLoading ? tr('Loading vendors…', 'جارٍ تحميل البائعين…') : tr('All vendors', 'كل البائعين')}</option>
                            {(tenantsQuery.data || []).map((tenant) => <option key={tenant.id} value={tenant.id}>{tenant.name}</option>)}
                        </select>
                        <div className="relative md:col-span-2">
                            <Search className="pointer-events-none absolute start-3.5 top-1/2 h-4 w-4 -translate-y-1/2 text-slate-400" />
                            <input value={filters.search ?? ''} onChange={(event) => updateFilters({ search: event.target.value })} placeholder={tr('Search vendor, method, or transfer reference', 'ابحث بالبائع أو الوسيلة أو مرجع التحويل')} className={`${saInput} ps-10`} />
                        </div>
                    </div>

                    {paymentsQuery.isError ? (
                        <div className="mt-3">
                            <InlineNotice tone="rose" title={tr('Payments unavailable', 'المدفوعات غير متاحة')}>
                                {getErrorMessage(paymentsQuery.error, tr('Refresh to retry.', 'حدّث لإعادة المحاولة.'))}
                            </InlineNotice>
                        </div>
                    ) : null}

                    <div className={`mt-4 ${saTable.shell}`}>
                        <table className="min-w-full divide-y divide-slate-100">
                            <thead className="bg-slate-50/90">
                                <tr className={saTable.head}>
                                    <th className={saTable.headerCell}>{tr('Vendor', 'البائع')}</th>
                                    <th className={saTable.headerCell}>{tr('Amount', 'المبلغ')}</th>
                                    <th className={saTable.headerCell}>{tr('Method', 'الوسيلة')}</th>
                                    <th className={saTable.headerCell}>{tr('Status', 'الحالة')}</th>
                                    <th className={saTable.headerCell}>{tr('Created', 'تاريخ الإنشاء')}</th>
                                    <th className={`${saTable.headerCell} text-end`}>{tr('Actions', 'إجراءات')}</th>
                                </tr>
                            </thead>
                            <tbody className="divide-y divide-slate-100">
                                {requests.map((request) => (
                                    <tr key={request.paymentRequestId}>
                                        <td className={saTable.cell}>
                                            <p className="font-semibold text-slate-950">{request.vendorName}</p>
                                            <p className="mt-0.5 text-sm text-slate-500">{request.transferReference || tr('No transfer reference', 'لا يوجد مرجع تحويل')}</p>
                                        </td>
                                        <td className={`${saTable.cell} font-semibold text-slate-950`}>{formatCurrency(request.amount, request.currency)}</td>
                                        <td className={`${saTable.cell} text-sm text-slate-600`}>
                                            <p className="font-medium text-slate-900">{request.methodName}</p>
                                            <p className="mt-0.5">{request.destinationValue}</p>
                                        </td>
                                        <td className={saTable.cell}>
                                            <StatusBadge tone={statusTone(request.status)}>{statusLabels[request.status] ?? request.status}</StatusBadge>
                                        </td>
                                        <td className={`${saTable.cell} text-sm text-slate-600`}>
                                            <p>{formatDateTime(request.createdAtUtc)}</p>
                                            <p className="mt-0.5 text-xs text-slate-500">{request.reviewReason || tr('Awaiting review', 'بانتظار المراجعة')}</p>
                                        </td>
                                        <td className={saTable.cell}>
                                            <div className="flex flex-wrap justify-end gap-1.5">
                                                <button type="button" onClick={() => handleOpenProof(request.paymentRequestId)} className={`${saButton.secondary} px-2.5 py-1.5 text-xs`}>{tr('Proof', 'الإثبات')}</button>
                                                {request.status === 'PendingVerification' ? (
                                                    <>
                                                        <button type="button" onClick={() => openActionModal('approve', request)} className={`${saButton.success} px-2.5 py-1.5 text-xs`}>{tr('Approve', 'موافقة')}</button>
                                                        <button type="button" onClick={() => openActionModal('reject', request)} className={`${saButton.danger} px-2.5 py-1.5 text-xs`}>{tr('Reject', 'رفض')}</button>
                                                    </>
                                                ) : null}
                                            </div>
                                        </td>
                                    </tr>
                                ))}
                                {!paymentsQuery.isError && requests.length === 0 ? (
                                    <tr>
                                        <td colSpan={6} className="px-4 py-8 text-center text-sm text-slate-500">
                                            {paymentsQuery.isFetching ? tr('Loading requests…', 'جارٍ تحميل الطلبات…') : tr('No repayment requests match these filters.', 'لا توجد طلبات سداد تطابق هذه المرشحات.')}
                                        </td>
                                    </tr>
                                ) : null}
                            </tbody>
                        </table>
                    </div>

                    <div className="mt-3 flex flex-col gap-2 sm:flex-row sm:items-center sm:justify-between">
                        <label className="flex items-center gap-2 text-sm text-slate-600">
                            <span>{tr('Page size', 'حجم الصفحة')}</span>
                            <select aria-label={tr('Repayments per page', 'طلبات السداد لكل صفحة')} value={filters.pageSize} onChange={(event) => updateFilters({ pageSize: Number(event.target.value) })} className={`${saInput} w-auto py-1.5`}>
                                {PAGE_SIZE_OPTIONS.map((size) => <option key={size} value={size}>{size}</option>)}
                            </select>
                        </label>
                        <div className="flex items-center gap-2">
                            <button type="button" disabled={filters.page <= 1} onClick={() => updateFilters({ page: filters.page - 1 })} className={saButton.secondary}>{tr('Previous', 'السابق')}</button>
                            <div className="rounded-lg border border-slate-200 bg-slate-50 px-3 py-1.5 text-sm font-medium text-slate-700">
                                {tr(`Page ${filters.page} of ${totalPages}`, `صفحة ${filters.page} من ${totalPages}`)}
                            </div>
                            <button type="button" disabled={filters.page >= totalPages} onClick={() => updateFilters({ page: filters.page + 1 })} className={saButton.primary}>{tr('Next', 'التالي')}</button>
                        </div>
                    </div>
                </SectionCard>
            </div>

            {actionModal ? (
                <div className="fixed inset-0 z-[70] flex items-center justify-center bg-slate-950/55 px-4">
                    <div className="w-full max-w-xl rounded-2xl border border-slate-200 bg-white p-5 shadow-2xl">
                        <div className="flex items-start gap-3">
                            <div className={`flex h-10 w-10 items-center justify-center rounded-xl ${actionModal.mode === 'approve' ? 'bg-emerald-50 text-emerald-700' : 'bg-rose-50 text-rose-700'}`}>
                                {actionModal.mode === 'approve' ? <CheckCircle2 className="h-5 w-5" /> : <AlertTriangle className="h-5 w-5" />}
                            </div>
                            <div>
                                <p className="text-[10px] font-semibold uppercase tracking-[0.16em] text-slate-500">
                                    {actionModal.mode === 'approve' ? tr('Approve payment', 'اعتماد الدفع') : tr('Reject payment', 'رفض الدفع')}
                                </p>
                                <h2 className="mt-1 text-xl font-semibold text-slate-950">{currentActionRequest?.vendorName}</h2>
                                <p className="mt-1 text-sm leading-6 text-slate-600">
                                    {currentActionRequest
                                        ? `${formatCurrency(currentActionRequest.amount, currentActionRequest.currency)} · ${currentActionRequest.methodName}`
                                        : tr('Refreshing current payment authority…', 'جارٍ تحديث صلاحية الدفع الحالية…')}
                                </p>
                            </div>
                        </div>
                        <textarea
                            value={actionReason}
                            onChange={(event) => setActionReason(event.target.value)}
                            rows={3}
                            placeholder={actionModal.mode === 'approve'
                                ? tr('Why is this proof valid and ready for ledger posting?', 'لماذا هذا الإثبات صالح وجاهز للترحيل إلى الدفتر؟')
                                : tr('Why is this payment request being rejected?', 'لماذا يُرفض طلب الدفع هذا؟')}
                            className={`${saInput} mt-4`}
                        />
                        {actionModal.mode === 'approve' ? (
                            <label className="mt-3 flex items-start gap-2.5 rounded-xl border border-amber-200 bg-amber-50 px-3.5 py-3 text-sm text-amber-950">
                                <input type="checkbox" checked={allowOverpayment} onChange={(event) => setAllowOverpayment(event.target.checked)} className="mt-0.5 h-4 w-4 rounded border-amber-300" />
                                <span>{tr('Allow approval even if the submitted amount now exceeds current vendor debt.', 'السماح بالموافقة حتى لو تجاوز المبلغ المرسل مديونية البائع الحالية.')}</span>
                            </label>
                        ) : null}
                        <div className="mt-4 flex justify-end gap-2">
                            <button type="button" onClick={closeActionModal} disabled={isActionSubmitting} className={saButton.secondary}>{tr('Cancel', 'إلغاء')}</button>
                            <button type="button" onClick={handleActionConfirm} disabled={isActionSubmitting || !currentActionRequest} className={actionModal.mode === 'approve' ? saButton.success : saButton.danger}>
                                {isActionSubmitting ? <Loader2 className="h-4 w-4 animate-spin" /> : null}
                                {actionModal.mode === 'approve' ? tr('Approve request', 'اعتماد الطلب') : tr('Reject request', 'رفض الطلب')}
                            </button>
                        </div>
                    </div>
                </div>
            ) : null}
        </SuperAdminPage>
    )
}
