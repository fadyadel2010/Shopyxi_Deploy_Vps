import { useEffect, useMemo, useRef, useState } from 'react'
import {
    Bug,
    ChevronDown,
    ChevronUp,
    CircleDollarSign,
    Clock3,
    CreditCard,
    Package,
    ReceiptText,
    Store,
    UserRound,
    X,
} from 'lucide-react'
import { useSuperAdminLocale } from '../i18n/useSuperAdminLocale'
import { EmptyState, ErrorState, InlineNotice, StatusBadge, saButton } from '../ui/SuperAdminUI'
import {
    isFailedPaymentStatus,
    isPendingOrderStatus,
    type SuperAdminOrderDetails,
    type SuperAdminOrderItem,
    type SuperAdminOrderSubOrderDetails,
} from './ordersService'

interface OrderDetailsDrawerProps {
    isOpen: boolean
    order?: SuperAdminOrderDetails | null
    isLoading: boolean
    isError: boolean
    errorMessage?: string | null
    onClose: () => void
    onRetry: () => void
}

const STUCK_THRESHOLD_MINUTES = 120

const ORDER_STATUS_LABELS: Record<string, string> = {
    Pending: 'معلّق',
    'Awaiting Payment': 'بانتظار الدفع',
    'Payment Pending': 'الدفع معلّق',
    Confirmed: 'مؤكد',
    Processing: 'قيد المعالجة',
    Packed: 'تم التجهيز',
    'Ready For Shipment': 'جاهز للشحن',
    Shipped: 'تم الشحن',
    Delivered: 'تم التسليم',
    Completed: 'مكتمل',
    Cancelled: 'ملغى',
    Returned: 'مرتجع',
    Refunded: 'مسترد',
    Failed: 'فاشل',
    OnHold: 'معلّق مؤقتًا',
}

const PAYMENT_STATUS_LABELS: Record<string, string> = {
    Paid: 'مدفوع',
    Collected: 'محصّل',
    Settled: 'مسوّى',
    Refunded: 'مسترد',
    PartiallyRefunded: 'مسترد جزئيًا',
    Failed: 'فاشل',
    Rejected: 'مرفوض',
    Expired: 'منتهي',
    AmountMismatch: 'مبلغ غير مطابق',
    Unpaid: 'غير مدفوع',
    Pending: 'معلّق',
    Ignored: 'متجاهل',
    Authorized: 'مفوّض',
    PartiallyPaid: 'مدفوع جزئيًا',
    AwaitingVerification: 'بانتظار التحقق',
    CashOnDelivery: 'الدفع عند الاستلام',
    RefundRequired: 'الاسترداد مطلوب',
    RefundFailed: 'فشل الاسترداد',
}

const REFUND_STATUS_LABELS: Record<string, string> = {
    Pending: 'معلّق', Processing: 'قيد المعالجة', Completed: 'مكتمل',
    Failed: 'فاشل', ReconciliationRequired: 'تتطلب مطابقة',
    Requested: 'مطلوب', Approved: 'موافق عليه', Rejected: 'مرفوض',
    UnderReview: 'قيد المراجعة', Cancelled: 'ملغى', WaitingForReturn: 'بانتظار الإرجاع',
    AwaitingPayout: 'بانتظار الصرف', Paid: 'مدفوع',
}

function formatJson(value?: string | null): string {
    if (!value?.trim()) {
        return 'Unavailable'
    }

    try {
        return JSON.stringify(JSON.parse(value), null, 2)
    } catch {
        return value
    }
}

function DetailCard({
    label,
    value,
    tone = 'border-slate-200 bg-white text-slate-950',
}: {
    label: string
    value: string
    tone?: string
}) {
    return (
        <div className={`rounded-xl border px-3.5 py-3 shadow-sm ${tone}`}>
            <p className="text-[10px] font-semibold uppercase tracking-[0.14em] opacity-70">{label}</p>
            <p className="mt-1 text-base font-semibold" dir="auto">{value}</p>
        </div>
    )
}

function DrawerSkeleton() {
    return (
        <div className="animate-pulse space-y-4">
            <div className="grid gap-3 sm:grid-cols-2">
                {Array.from({ length: 4 }).map((_, index) => (
                    <div key={`drawer-card-${index}`} className="h-20 rounded-xl bg-slate-100" />
                ))}
            </div>

            {Array.from({ length: 4 }).map((_, index) => (
                <div key={`drawer-section-${index}`} className="h-32 rounded-xl bg-slate-100" />
            ))}
        </div>
    )
}

function deriveSubOrderItemPresentation(order?: SuperAdminOrderDetails | null) {
    if (!order) {
        return []
    }

    return order.subOrders.map((subOrder: SuperAdminOrderSubOrderDetails) => ({
        subOrder,
        items: order.orderItems.filter((item: SuperAdminOrderItem) => item.subOrderId === subOrder.subOrderId),
    }))
}

export default function OrderDetailsDrawer({
    isOpen,
    order,
    isLoading,
    isError,
    errorMessage,
    onClose,
    onRetry,
}: OrderDetailsDrawerProps) {
    const [showDebugInfo, setShowDebugInfo] = useState(false)
    const drawerRef = useRef<HTMLElement>(null)
    const { tr, locale, formatCurrency, formatPercent, formatDateTime } = useSuperAdminLocale()

    useEffect(() => {
        if (!isOpen) return
        const previous = document.activeElement as HTMLElement | null
        const oldOverflow = document.body.style.overflow
        document.body.style.overflow = 'hidden'
        drawerRef.current?.querySelector<HTMLButtonElement>('button')?.focus()
        const trapFocus = (event: KeyboardEvent) => {
            if (event.key !== 'Tab') return
            const controls = Array.from(drawerRef.current?.querySelectorAll<HTMLElement>(
                'button:not([disabled]),a[href],input:not([disabled]),select:not([disabled]),[tabindex="0"]',
            ) ?? [])
            const first = controls[0]
            const last = controls[controls.length - 1]
            if (event.shiftKey && document.activeElement === first) { event.preventDefault(); last?.focus() }
            else if (!event.shiftKey && document.activeElement === last) { event.preventDefault(); first?.focus() }
        }
        document.addEventListener('keydown', trapFocus)
        return () => {
            document.body.style.overflow = oldOverflow
            document.removeEventListener('keydown', trapFocus)
            previous?.focus()
        }
    }, [isOpen])

    useEffect(() => {
        if (!isOpen) {
            setShowDebugInfo(false)
            return undefined
        }

        function handleEscape(event: KeyboardEvent) {
            if (event.key === 'Escape') {
                onClose()
            }
        }

        window.addEventListener('keydown', handleEscape)
        return () => window.removeEventListener('keydown', handleEscape)
    }, [isOpen, onClose])

    useEffect(() => {
        setShowDebugInfo(false)
    }, [order?.orderId])

    const getRelativeAge = (value?: string | null): { label: string; isStuck: boolean } => {
        if (!value) {
            return { label: tr('Awaiting sync', 'بانتظار المزامنة'), isStuck: false }
        }

        const parsed = new Date(value)
        if (Number.isNaN(parsed.getTime())) {
            return { label: tr('Awaiting sync', 'بانتظار المزامنة'), isStuck: false }
        }

        const diffMs = Math.max(0, Date.now() - parsed.getTime())
        const minutesOld = Math.floor(diffMs / 60_000)

        if (minutesOld < 1) {
            return { label: tr('Just created', 'أُنشئ الآن'), isStuck: false }
        }

        const relative = new Intl.RelativeTimeFormat(locale, { numeric: 'auto' })

        if (minutesOld < 60) {
            return {
                label: relative.format(-minutesOld, 'minute'),
                isStuck: minutesOld >= STUCK_THRESHOLD_MINUTES,
            }
        }

        const hoursOld = Math.floor(minutesOld / 60)
        if (hoursOld < 24) {
            return {
                label: relative.format(-hoursOld, 'hour'),
                isStuck: minutesOld >= STUCK_THRESHOLD_MINUTES,
            }
        }

        const daysOld = Math.floor(hoursOld / 24)
        return {
            label: relative.format(-daysOld, 'day'),
            isStuck: minutesOld >= STUCK_THRESHOLD_MINUTES,
        }
    }

    const orderAge = useMemo(() => getRelativeAge(order?.createdAt), [order?.createdAt, locale])

    const splitItemPresentation = useMemo(() => deriveSubOrderItemPresentation(order), [order])

    const shouldShowIssueBanner = order
        ? isFailedPaymentStatus(order.paymentStatus) ||
          isPendingOrderStatus(order.status) ||
          (isPendingOrderStatus(order.status) && orderAge.isStuck)
        : false

    const statusLabel = (value: string) => tr(value, ORDER_STATUS_LABELS[value] ?? value)
    const paymentStatusLabel = (value: string) => tr(value, PAYMENT_STATUS_LABELS[value] ?? value)

    if (!isOpen) return null

    return (
        <>
            <div
                className={`fixed inset-0 z-40 bg-slate-950/45 transition-opacity duration-300 ${
                    isOpen ? 'pointer-events-auto opacity-100' : 'pointer-events-none opacity-0'
                }`}
                onClick={onClose}
            />

            <aside
                ref={drawerRef}
                role="dialog"
                aria-modal="true"
                aria-label={tr('Order details', 'تفاصيل الطلب')}
                className={`fixed end-0 top-0 z-50 flex h-full w-full max-w-[780px] transform flex-col border-s border-slate-200 bg-white shadow-2xl transition-transform duration-300 ${
                    isOpen ? 'translate-x-0' : 'translate-x-full rtl:-translate-x-full'
                }`}
                aria-hidden={!isOpen}
            >
                <div className="border-b border-slate-100 px-4 py-4 sm:px-5">
                    <div className="flex items-start justify-between gap-3">
                        <div className="min-w-0">
                            <p className="text-[10px] font-semibold uppercase tracking-[0.18em] text-emerald-700">
                                {tr('Order details', 'تفاصيل الطلب')}
                            </p>
                            <h2 className="mt-1 truncate text-xl font-semibold text-slate-950" dir="ltr">
                                {order?.orderNumber ?? tr('Loading order', 'جارٍ تحميل الطلب')}
                            </h2>
                            <p className="mt-0.5 truncate font-mono text-xs text-slate-500" dir="ltr">
                                {order?.orderId ?? tr('Fetching CommerceHub order record', 'جارٍ جلب سجل طلب CommerceHub')}
                            </p>
                        </div>

                        <button
                            type="button"
                            aria-label={tr('Close order details', 'إغلاق تفاصيل الطلب')}
                            className="rounded-lg border border-slate-200 bg-white p-2 text-slate-500 transition hover:bg-slate-50 hover:text-slate-900"
                            onClick={onClose}
                        >
                            <X className="h-4 w-4" />
                        </button>
                    </div>
                </div>

                <div className="flex-1 overflow-y-auto px-4 py-4 sm:px-5">
                    {isLoading ? <DrawerSkeleton /> : null}

                    {!isLoading && isError ? (
                        <ErrorState
                            title={tr('Unable to load order details', 'تعذّر تحميل تفاصيل الطلب')}
                            description={
                                errorMessage ??
                                tr(
                                    'The detail request failed before the drawer could hydrate.',
                                    'فشل طلب التفاصيل قبل اكتمال تحميل اللوحة.',
                                )
                            }
                            onRetry={onRetry}
                            retryLabel={tr('Retry detail request', 'إعادة طلب التفاصيل')}
                        />
                    ) : null}

                    {!isLoading && !isError && order ? (
                        <div className="space-y-4">
                            {shouldShowIssueBanner ? (
                                <InlineNotice
                                    tone={isFailedPaymentStatus(order.paymentStatus) ? 'rose' : 'amber'}
                                    title={
                                        isFailedPaymentStatus(order.paymentStatus)
                                            ? tr('Payment issue detected', 'تم رصد مشكلة دفع')
                                            : orderAge.isStuck
                                              ? tr(`Order appears stuck (${orderAge.label})`, `يبدو الطلب عالقًا (${orderAge.label})`)
                                              : tr('Order still pending', 'الطلب ما زال معلّقًا')
                                    }
                                >
                                    {isFailedPaymentStatus(order.paymentStatus)
                                        ? tr(
                                              'This order is flagged because the payment status is failed or rejected and may need immediate recovery.',
                                              'تم تمييز هذا الطلب لأن حالة الدفع فاشلة أو مرفوضة وقد يحتاج معالجة فورية.',
                                          )
                                        : orderAge.isStuck
                                          ? tr(
                                                'The order is still pending after more than two hours, so it likely needs operational follow-up.',
                                                'الطلب ما زال معلّقًا بعد أكثر من ساعتين، لذا يحتاج متابعة تشغيلية على الأرجح.',
                                            )
                                          : tr(
                                                'This order is still in the pending state and may need operational follow-up.',
                                                'الطلب ما زال في حالة التعليق وقد يحتاج متابعة تشغيلية.',
                                            )}
                                </InlineNotice>
                            ) : null}

                            <div className="grid gap-3 sm:grid-cols-2">
                                <DetailCard
                                    label={tr('Total', 'الإجمالي')}
                                    value={formatCurrency(order.totalAmount, order.currency)}
                                    tone="border-slate-200 bg-slate-950 text-white"
                                />
                                <DetailCard
                                    label={tr('Vendors', 'البائعون')}
                                    value={tr(
                                        `${order.vendorCount} ${order.vendorCount === 1 ? 'vendor' : 'vendors'}`,
                                        `${order.vendorCount} بائع`,
                                    )}
                                    tone="border-emerald-200 bg-emerald-50 text-emerald-950"
                                />
                                <DetailCard
                                    label={tr('Status', 'الحالة')}
                                    value={statusLabel(order.status)}
                                    tone={
                                        isPendingOrderStatus(order.status)
                                            ? 'border-amber-200 bg-amber-50 text-amber-950'
                                            : 'border-slate-200 bg-white text-slate-950'
                                    }
                                />
                                <DetailCard
                                    label={tr('Payment status', 'حالة الدفع')}
                                    value={paymentStatusLabel(order.paymentStatus)}
                                    tone={
                                        isFailedPaymentStatus(order.paymentStatus)
                                            ? 'border-rose-200 bg-rose-50 text-rose-950'
                                            : 'border-sky-200 bg-sky-50 text-sky-950'
                                    }
                                />
                            </div>

                            <section className="rounded-xl border border-slate-200 bg-white p-4 shadow-sm">
                                <div className="flex items-center gap-2 text-slate-900">
                                    <ReceiptText className="h-4 w-4 text-emerald-600" />
                                    <h3 className="text-base font-semibold">{tr('Order summary', 'ملخص الطلب')}</h3>
                                </div>
                                <div className="mt-3 grid gap-3 md:grid-cols-2">
                                    <div className="rounded-lg border border-slate-100 bg-slate-50 p-3">
                                        <div className="space-y-2 text-sm text-slate-600">
                                            <div className="flex items-center justify-between gap-3">
                                                <span>{tr('Tenant summary', 'ملخص المتجر')}</span>
                                                <span className="font-semibold text-slate-950">
                                                    {order.vendorCount > 1
                                                        ? tr(`${order.subOrders[0]?.vendorName} +${order.vendorCount - 1} more`, `${order.subOrders[0]?.vendorName} و${order.vendorCount - 1} آخرون`)
                                                        : order.subOrders[0]?.vendorName || tr('No vendors', 'لا يوجد بائعون')}
                                                </span>
                                            </div>
                                            <div className="flex items-center justify-between gap-3">
                                                <span>{tr('Created at', 'تاريخ الإنشاء')}</span>
                                                <span className="font-semibold text-slate-950" dir="auto">
                                                    {formatDateTime(order.createdAt)}
                                                </span>
                                            </div>
                                            <div className="flex items-center justify-between gap-3">
                                                <span>{tr('Age', 'العمر')}</span>
                                                <span className="font-semibold text-slate-950">{orderAge.label}</span>
                                            </div>
                                            <div className="flex items-center justify-between gap-3">
                                                <span>{tr('Paid at', 'وقت الدفع')}</span>
                                                <span className="font-semibold text-slate-950" dir="auto">
                                                    {formatDateTime(order.paidAt)}
                                                </span>
                                            </div>
                                            <div className="flex items-center justify-between gap-3">
                                                <span>{tr('Payment method', 'وسيلة الدفع')}</span>
                                                <span className="font-semibold text-slate-950">
                                                    {order.paymentMethodName ||
                                                        order.paymentMethodCode ||
                                                        tr('Unavailable', 'غير متاح')}
                                                </span>
                                            </div>
                                            <div className="flex items-center justify-between gap-3">
                                                <span>{tr('Gateway', 'بوابة الدفع')}</span>
                                                <span className="font-semibold text-slate-950">
                                                    {order.paymentGateway || tr('Unavailable', 'غير متاح')}
                                                </span>
                                            </div>
                                        </div>
                                    </div>

                                    <div className="rounded-lg border border-slate-100 bg-slate-50 p-3">
                                        <div className="space-y-2 text-sm text-slate-600">
                                            <div className="flex items-center justify-between gap-3">
                                                <span>{tr('Subtotal', 'المجموع الفرعي')}</span>
                                                <span className="font-semibold text-slate-950" dir="auto">
                                                    {formatCurrency(order.subtotal, order.currency)}
                                                </span>
                                            </div>
                                            <div className="flex items-center justify-between gap-3">
                                                <span>{tr('Shipping', 'الشحن')}</span>
                                                <span className="font-semibold text-slate-950" dir="auto">
                                                    {formatCurrency(order.shippingTotal, order.currency)}
                                                </span>
                                            </div>
                                            <div className="flex items-center justify-between gap-3 border-t border-slate-200 pt-2">
                                                <span className="font-semibold text-slate-950">{tr('Total', 'الإجمالي')}</span>
                                                <span className="text-base font-semibold text-slate-950" dir="auto">
                                                    {formatCurrency(order.totalAmount, order.currency)}
                                                </span>
                                            </div>
                                            <div className="pt-1 text-xs text-slate-500" dir="auto">
                                                {tr('Payment reference:', 'مرجع الدفع:')}{' '}
                                                {order.paymentReference ??
                                                    order.paymentReferenceId ??
                                                    tr('Unavailable', 'غير متاح')}
                                            </div>
                                        </div>
                                    </div>
                                </div>
                            </section>

                            <section className="rounded-xl border border-slate-200 bg-white p-4 shadow-sm">
                                <div className="flex items-center gap-2 text-slate-900">
                                    <CircleDollarSign className="h-4 w-4 text-emerald-600" />
                                    <h3 className="text-base font-semibold">
                                        {tr('Financial snapshot', 'اللقطة المالية')}
                                    </h3>
                                </div>

                                <div className="mt-3 grid gap-2 md:grid-cols-2 xl:grid-cols-4">
                                    <div className="rounded-lg border border-slate-100 bg-slate-50 px-3 py-2.5">
                                        <p className="text-[10px] font-semibold uppercase tracking-[0.14em] text-slate-500">
                                            {tr('Subtotal', 'المجموع الفرعي')}
                                        </p>
                                        <p className="mt-1 text-sm font-semibold text-slate-950" dir="auto">
                                            {formatCurrency(order.subtotal, order.currency)}
                                        </p>
                                    </div>
                                    <div className="rounded-lg border border-rose-100 bg-rose-50 px-3 py-2.5">
                                        <p className="text-[10px] font-semibold uppercase tracking-[0.14em] text-rose-700">
                                            {tr('Commission', 'العمولة')}
                                        </p>
                                        <p className="mt-1 text-sm font-semibold text-rose-900" dir="auto">
                                            {formatCurrency(order.platformCommissionTotal, order.currency)}
                                        </p>
                                    </div>
                                    <div className="rounded-lg border border-emerald-100 bg-emerald-50 px-3 py-2.5">
                                        <p className="text-[10px] font-semibold uppercase tracking-[0.14em] text-emerald-700">
                                            {tr('Vendor earnings', 'أرباح البائع')}
                                        </p>
                                        <p className="mt-1 text-sm font-semibold text-emerald-900" dir="auto">
                                            {formatCurrency(order.vendorEarningsTotal, order.currency)}
                                        </p>
                                    </div>
                                    <div className="rounded-lg border border-slate-100 bg-slate-50 px-3 py-2.5">
                                        <p className="text-[10px] font-semibold uppercase tracking-[0.14em] text-slate-500">
                                            {tr('Shipping', 'الشحن')}
                                        </p>
                                        <p className="mt-1 text-sm font-semibold text-slate-950" dir="auto">
                                            {formatCurrency(order.shippingTotal, order.currency)}
                                        </p>
                                    </div>
                                </div>
                            </section>

                            <section className="rounded-xl border border-slate-200 bg-white p-4 shadow-sm">
                                <div className="flex items-center gap-2 text-slate-900">
                                    <UserRound className="h-4 w-4 text-sky-600" />
                                    <h3 className="text-base font-semibold">{tr('Customer info', 'بيانات العميل')}</h3>
                                </div>
                                <div className="mt-3 grid gap-3 md:grid-cols-2">
                                    <div className="rounded-lg border border-slate-200 bg-slate-50 p-3">
                                        <p className="text-[10px] font-semibold uppercase tracking-[0.14em] text-slate-500">
                                            {tr('Name', 'الاسم')}
                                        </p>
                                        <p className="mt-1 text-sm font-semibold text-slate-950">
                                            {order.customerName || tr('Unknown customer', 'عميل غير معروف')}
                                        </p>
                                        <p className="mt-3 text-[10px] font-semibold uppercase tracking-[0.14em] text-slate-500">
                                            {tr('Phone', 'الهاتف')}
                                        </p>
                                        <p className="mt-1 text-sm font-semibold text-slate-950" dir="ltr">
                                            {order.customerPhone || tr('Unavailable', 'غير متاح')}
                                        </p>
                                    </div>

                                    <div className="rounded-lg border border-slate-200 bg-slate-50 p-3">
                                        <p className="text-[10px] font-semibold uppercase tracking-[0.14em] text-slate-500">
                                            {tr('Delivery address', 'عنوان التسليم')}
                                        </p>
                                                                 <p className="mt-1 text-sm leading-6 text-slate-700">
                                            {[order.addressLine, order.area, order.city].filter(Boolean).join(', ') ||
                                                tr('Unavailable', 'غير متاح')}
                                                                 </p>
                                        <p className="mt-3 text-[10px] font-semibold uppercase tracking-[0.14em] text-slate-500">
                                            {tr('Notes', 'ملاحظات')}
                                        </p>
                                        <p className="mt-1 text-sm leading-6 text-slate-700">
                                            {order.notes || tr('No customer notes', 'لا توجد ملاحظات للعميل')}
                                        </p>
                                    </div>
                                </div>
                            </section>

                            <section className="rounded-xl border border-slate-200 bg-white p-4 shadow-sm">
                                <div className="flex items-center gap-2 text-slate-900">
                                    <CreditCard className="h-4 w-4 text-violet-600" />
                                    <h3 className="text-base font-semibold">{tr('Payment details', 'تفاصيل الدفع')}</h3>
                                </div>

                                {order.payments.length === 0 ? (
                                    <div className="mt-3">
                                        <EmptyState
                                            title={tr('No payment records', 'لا توجد سجلات دفع')}
                                            description={tr(
                                                'No payment records were attached to this order yet.',
                                                'لم تُرفق أي سجلات دفع بهذا الطلب بعد.',
                                            )}
                                        />
                                    </div>
                                ) : (
                                    <div className="mt-3 space-y-3">
                                        {order.payments.map((payment) => (
                                            <div key={payment.paymentId} className="rounded-lg border border-slate-200 bg-slate-50 p-3">
                                                <div className="flex flex-col gap-2 sm:flex-row sm:items-start sm:justify-between">
                                                    <div className="min-w-0">
                                                        <p className="text-sm font-semibold text-slate-950" dir="auto">
                                                            {payment.provider} · {formatCurrency(payment.amount, payment.currency)}
                                                        </p>
                                                        <p className="mt-0.5 break-all font-mono text-xs text-slate-500" dir="ltr">
                                                            {payment.providerReference}
                                                        </p>
                                                    </div>
                                                    <StatusBadge
                                                        tone={
                                                            payment.status === 'Failed'
                                                                ? 'rose'
                                                                : payment.status === 'Paid'
                                                                  ? 'emerald'
                                                                  : 'amber'
                                                        }
                                                    >
                                                        {paymentStatusLabel(payment.status)}
                                                    </StatusBadge>
                                                </div>

                                                <div className="mt-3 grid gap-2 md:grid-cols-3">
                                                    <div className="rounded-lg border border-slate-100 bg-white px-3 py-2">
                                                        <p className="text-[10px] font-semibold uppercase tracking-[0.14em] text-slate-500">
                                                            {tr('Created', 'أُنشئ')}
                                                        </p>
                                                        <p className="mt-1 text-sm font-semibold text-slate-950" dir="auto">
                                                            {formatDateTime(payment.createdAt)}
                                                        </p>
                                                    </div>
                                                    <div className="rounded-lg border border-slate-100 bg-white px-3 py-2">
                                                        <p className="text-[10px] font-semibold uppercase tracking-[0.14em] text-slate-500">
                                                            {tr('Paid', 'دُفع')}
                                                        </p>
                                                        <p className="mt-1 text-sm font-semibold text-slate-950" dir="auto">
                                                            {formatDateTime(payment.paidAtUtc)}
                                                        </p>
                                                    </div>
                                                    <div className="rounded-lg border border-slate-100 bg-white px-3 py-2">
                                                        <p className="text-[10px] font-semibold uppercase tracking-[0.14em] text-slate-500">
                                                            {tr('Event processing', 'معالجة الحدث')}
                                                        </p>
                                                        <p className="mt-1 text-sm font-semibold text-slate-950">
                                                            {payment.isProcessed
                                                                ? tr('Processed', 'تمت المعالجة')
                                                                : tr('Pending', 'معلّق')}
                                                        </p>
                                                    </div>
                                                </div>

                                                {payment.failureReason ? (
                                                    <div className="mt-2 rounded-lg border border-rose-200 bg-rose-50 px-3 py-2 text-sm text-rose-900">
                                                        {tr('Failure reason:', 'سبب الفشل:')} {payment.failureReason}
                                                    </div>
                                                ) : null}

                                                {payment.attempts.length > 0 ? (
                                                    <div className="mt-3 border-t border-slate-200 pt-3">
                                                        <p className="text-[10px] font-semibold uppercase tracking-[0.14em] text-slate-500">
                                                            {tr('Payment attempts', 'محاولات الدفع')}
                                                        </p>
                                                        <div className="mt-2 space-y-2">
                                                            {payment.attempts.map((attempt) => (
                                                                <div key={attempt.attemptId} className="rounded-lg border border-slate-200 bg-white px-3 py-2 text-xs text-slate-600">
                                                                    <div className="flex flex-wrap items-center justify-between gap-2">
                                                                        <span className="font-semibold text-slate-950">
                                                                            {tr(`Attempt ${attempt.attemptNumber}`, `المحاولة ${attempt.attemptNumber}`)} · {attempt.provider}
                                                                        </span>
                                                                        <StatusBadge tone={attempt.status === 'Paid' ? 'emerald' : attempt.status === 'Failed' ? 'rose' : 'amber'}>
                                                                            {paymentStatusLabel(attempt.status)}
                                                                        </StatusBadge>
                                                                    </div>
                                                                    <p className="mt-1 break-all font-mono" dir="ltr">
                                                                        {attempt.providerReference || tr('No provider reference', 'لا يوجد مرجع مزوّد')}
                                                                    </p>
                                                                    <p className="mt-1" dir="auto">
                                                                        {tr('Completed:', 'اكتملت:')} {formatDateTime(attempt.completedAtUtc)}
                                                                    </p>
                                                                    {attempt.failureReason ? (
                                                                        <p className="mt-1 text-rose-700">
                                                                            {tr('Failure:', 'الفشل:')} {attempt.failureReason}
                                                                        </p>
                                                                    ) : null}
                                                                </div>
                                                            ))}
                                                        </div>
                                                    </div>
                                                ) : null}
                                            </div>
                                        ))}
                                    </div>
                                )}
                            </section>

                            <section className="rounded-xl border border-slate-200 bg-white p-4 shadow-sm">
                                <div className="flex items-center gap-2 text-slate-900">
                                    <Clock3 className="h-4 w-4 text-amber-600" />
                                    <h3 className="text-base font-semibold">{tr('Lifecycle history', 'سجل دورة الطلب')}</h3>
                                </div>

                                {order.statusHistory.length === 0 ? (
                                    <div className="mt-3">
                                        <EmptyState
                                            title={tr('No persisted transitions', 'لا توجد انتقالات محفوظة')}
                                            description={tr(
                                                'No sub-order status transition history is stored for this order.',
                                                'لا يوجد سجل محفوظ لانتقالات حالة الطلبات الفرعية لهذا الطلب.',
                                            )}
                                        />
                                    </div>
                                ) : (
                                    <div className="mt-3 space-y-2">
                                        {order.statusHistory.map((history) => (
                                            <div key={history.historyId} className="rounded-lg border border-slate-200 bg-slate-50 px-3 py-2.5">
                                                <div className="flex flex-wrap items-center justify-between gap-2">
                                                    <p className="text-sm font-semibold text-slate-950">
                                                        {statusLabel(history.previousStatus)} → {statusLabel(history.newStatus)}
                                                    </p>
                                                    <p className="text-xs text-slate-500" dir="auto">{formatDateTime(history.changedAtUtc)}</p>
                                                </div>
                                                {history.reason ? <p className="mt-1 text-sm text-slate-600">{history.reason}</p> : null}
                                            </div>
                                        ))}
                                    </div>
                                )}
                            </section>

                            <section className="rounded-xl border border-slate-200 bg-white p-4 shadow-sm">
                                <div className="flex items-center gap-2 text-slate-900">
                                    <ReceiptText className="h-4 w-4 text-rose-600" />
                                    <h3 className="text-base font-semibold">{tr('Refund history', 'سجل الاسترداد')}</h3>
                                </div>

                                {order.refunds.length === 0 ? (
                                    <div className="mt-3">
                                        <EmptyState
                                            title={tr('No executed refunds', 'لا توجد استردادات منفذة')}
                                            description={tr(
                                                'No persisted refund execution is linked to this order.',
                                                'لا يوجد تنفيذ استرداد محفوظ مرتبط بهذا الطلب.',
                                            )}
                                        />
                                    </div>
                                ) : (
                                    <div className="mt-3 space-y-3">
                                        {order.refunds.map((refund) => (
                                            <div key={refund.refundId} className="rounded-lg border border-slate-200 bg-slate-50 p-3">
                                                <div className="flex flex-wrap items-start justify-between gap-2">
                                                    <div>
                                                        <p className="text-sm font-semibold text-slate-950" dir="auto">
                                                            {formatCurrency(refund.amount, refund.currency)} · {refund.reason}
                                                        </p>
                                                        <p className="mt-0.5 text-xs text-slate-500" dir="auto">
                                                            {tr('Processed:', 'تمت المعالجة:')} {formatDateTime(refund.processedAtUtc)}
                                                        </p>
                                                    </div>
                                                    <StatusBadge tone={refund.status === 'Completed' ? 'emerald' : refund.status === 'Failed' ? 'rose' : 'amber'}>
                                                        {tr(refund.status, REFUND_STATUS_LABELS[refund.status] ?? refund.status)}
                                                    </StatusBadge>
                                                </div>
                                                <div className="mt-3 grid gap-2 sm:grid-cols-3">
                                                    <DetailCard label={tr('Commission reversal', 'عكس العمولة')} value={formatCurrency(refund.commissionReversalAmount, refund.currency)} />
                                                    <DetailCard label={tr('Promo reversal', 'عكس العرض')} value={formatCurrency(refund.promoDiscountReversalAmount, refund.currency)} />
                                                    <DetailCard label={tr('Vendor reversal', 'عكس البائع')} value={formatCurrency(refund.vendorEarningReversalAmount, refund.currency)} />
                                                </div>
                                                {refund.failureReason ? <p className="mt-2 text-sm text-rose-700">{refund.failureReason}</p> : null}
                                            </div>
                                        ))}
                                    </div>
                                )}
                            </section>

                            <section className="rounded-xl border border-slate-200 bg-white p-4 shadow-sm">
                                <div className="flex items-center gap-2 text-slate-900">
                                    <Package className="h-4 w-4 text-sky-600" />
                                    <h3 className="text-base font-semibold">{tr('Items', 'الأصناف')}</h3>
                                </div>

                                {splitItemPresentation.length === 0 ? (
                                    <div className="mt-3">
                                        <EmptyState
                                            title={tr('No item lines', 'لا توجد بنود أصناف')}
                                            description={tr(
                                                'No item lines were attached to this order yet.',
                                                'لم تُرفق أي بنود أصناف بهذا الطلب بعد.',
                                            )}
                                        />
                                    </div>
                                ) : (
                                    <div className="mt-3 space-y-3">
                                        {splitItemPresentation.map(({ subOrder, items }) => (
                                            <div key={subOrder.subOrderId} className="rounded-lg border border-slate-200 bg-slate-50 p-3">
                                                <div className="flex items-center justify-between gap-3">
                                                    <div>
                                                        <p className="text-sm font-semibold text-slate-950">
                                                            {subOrder.vendorName}
                                                        </p>
                                                        <p className="mt-0.5 text-xs text-slate-500">
                                                            {subOrder.orderNumber} ·{' '}
                                                            {tr(
                                                                `${items.length} ${items.length === 1 ? 'item' : 'items'}`,
                                                                `${items.length} صنف`,
                                                            )}
                                                        </p>
                                                    </div>
                                                </div>

                                                <div className="mt-3 space-y-2">
                                                    {items.map((item) => (
                                                        <div
                                                            key={item.itemId}
                                                            className="grid gap-3 rounded-lg border border-slate-100 bg-white p-3 md:grid-cols-[56px_1fr_auto]"
                                                        >
                                                            <div className="flex h-14 w-14 items-center justify-center overflow-hidden rounded-lg bg-slate-100">
                                                                {item.imageUrl ? (
                                                                    <img
                                                                        src={item.imageUrl}
                                                                        alt={item.productName}
                                                                        className="h-full w-full object-cover"
                                                                    />
                                                                ) : (
                                                                    <Package className="h-5 w-5 text-slate-400" />
                                                                )}
                                                            </div>

                                                            <div className="min-w-0">
                                                                <p className="text-sm font-semibold text-slate-950">
                                                                    {item.productName}
                                                                </p>
                                                                <p className="mt-0.5 text-sm text-slate-600">
                                                                    {item.variantName || tr('Base product', 'المنتج الأساسي')}
                                                                </p>
                                                                    <div className="mt-2 flex flex-wrap gap-3 text-xs text-slate-500">
                                                                    <span>{tr('Qty:', 'الكمية:')} {item.quantity}</span>
                                                                    <span dir="auto">
                                                                        {tr('Unit:', 'الوحدة:')}{' '}
                                                                        {formatCurrency(item.unitPrice, order.currency)}
                                                                    </span>
                                                                    </div>
                                                                    {item.refunds.map((request) => (
                                                                        <div key={request.refundId} className="mt-2 rounded border border-amber-200 bg-amber-50 p-2 text-xs text-amber-900">
                                                                            {tr('Refund request:', 'طلب استرداد:')}{' '}
                                                                            {tr(request.status, REFUND_STATUS_LABELS[request.status] ?? request.status)} ·{' '}
                                                                            {formatCurrency(request.amount, order.currency)} · {request.reason}
                                                                        </div>
                                                                    ))}
                                                            </div>

                                                            <div className="self-center text-start md:text-end">
                                                                <p className="text-[10px] font-semibold uppercase tracking-[0.14em] text-slate-500">
                                                                    {tr('Total', 'الإجمالي')}
                                                                </p>
                                                                <p className="mt-1 text-sm font-semibold text-slate-950" dir="auto">
                                                                    {formatCurrency(item.totalPrice, order.currency)}
                                                                </p>
                                                            </div>
                                                        </div>
                                                    ))}
                                                </div>
                                            </div>
                                        ))}
                                    </div>
                                )}
                            </section>

                            <section className="rounded-xl border border-slate-200 bg-white p-4 shadow-sm">
                                <div className="flex items-center gap-2 text-slate-900">
                                    <Store className="h-4 w-4 text-emerald-600" />
                                    <h3 className="text-base font-semibold">
                                        {tr('Sub-orders breakdown', 'تفصيل الطلبات الفرعية')}
                                    </h3>
                                </div>

                                <div className="mt-3 space-y-3">
                                    {order.subOrders.map((subOrder) => (
                                        <div key={subOrder.subOrderId} className="rounded-lg border border-slate-200 bg-slate-50 p-3">
                                            <div className="flex flex-col gap-2 lg:flex-row lg:items-start lg:justify-between">
                                                <div>
                                                    <p className="text-sm font-semibold text-slate-950">
                                                        {subOrder.vendorName}
                                                    </p>
                                                    <p className="mt-0.5 text-xs text-slate-500">
                                                        {subOrder.orderNumber} ·{' '}
                                                        {tr(
                                                            `${subOrder.itemCount} ${
                                                                subOrder.itemCount === 1 ? 'item' : 'items'
                                                            }`,
                                                            `${subOrder.itemCount} صنف`,
                                                        )}
                                                    </p>
                                                </div>

                                                <div className="flex flex-wrap gap-1.5">
                                                    <StatusBadge tone="slate">{statusLabel(subOrder.status)}</StatusBadge>
                                                    <StatusBadge tone="slate">
                                                        {paymentStatusLabel(subOrder.paymentStatus)}
                                                    </StatusBadge>
                                                </div>
                                            </div>

                                            <div className="mt-3 grid gap-2 md:grid-cols-2 xl:grid-cols-4">
                                                <div className="rounded-lg border border-slate-100 bg-white px-3 py-2">
                                                    <p className="text-[10px] font-semibold uppercase tracking-[0.14em] text-slate-500">
                                                        {tr('Amount', 'المبلغ')}
                                                    </p>
                                                    <p className="mt-1 text-sm font-semibold text-slate-950" dir="auto">
                                                        {formatCurrency(subOrder.amount, subOrder.currency)}
                                                    </p>
                                                </div>
                                                <div className="rounded-lg border border-rose-100 bg-rose-50 px-3 py-2">
                                                    <p className="text-[10px] font-semibold uppercase tracking-[0.14em] text-rose-700">
                                                        {tr('Commission', 'العمولة')}
                                                    </p>
                                                    <p className="mt-1 text-sm font-semibold text-rose-900" dir="auto">
                                                        {formatCurrency(subOrder.platformCommissionAmount, subOrder.currency)}
                                                    </p>
                                                    <p className="mt-0.5 text-xs text-rose-700">
                                                        {formatPercent(subOrder.platformCommissionRate)}
                                                    </p>
                                                </div>
                                                <div className="rounded-lg border border-emerald-100 bg-emerald-50 px-3 py-2">
                                                    <p className="text-[10px] font-semibold uppercase tracking-[0.14em] text-emerald-700">
                                                        {tr('Vendor earnings', 'أرباح البائع')}
                                                    </p>
                                                    <p className="mt-1 text-sm font-semibold text-emerald-900" dir="auto">
                                                        {formatCurrency(subOrder.vendorEarningAmount, subOrder.currency)}
                                                    </p>
                                                </div>
                                                <div className="rounded-lg border border-slate-100 bg-white px-3 py-2">
                                                    <p className="text-[10px] font-semibold uppercase tracking-[0.14em] text-slate-500">
                                                        {tr('Payment method', 'وسيلة الدفع')}
                                                    </p>
                                                    <p className="mt-1 text-sm font-semibold text-slate-950">
                                                        {subOrder.paymentMethodName || tr('Unavailable', 'غير متاح')}
                                                    </p>
                                                    <p className="mt-0.5 text-xs text-slate-500">
                                                        {subOrder.shippingMethodName ||
                                                            tr('No shipping method', 'لا توجد وسيلة شحن')}
                                                    </p>
                                                </div>
                                            </div>

                                            <div className="mt-3 flex flex-wrap items-center gap-2 text-xs text-slate-500">
                                                <span dir="auto">
                                                    {tr('Payment reference:', 'مرجع الدفع:')}{' '}
                                                    {subOrder.paymentReferenceId || tr('Unavailable', 'غير متاح')}
                                                </span>
                                                <span className="hidden text-slate-300 sm:inline">·</span>
                                                <span dir="auto">
                                                    {tr('Paid:', 'دُفع:')} {formatDateTime(subOrder.paidAt)}
                                                </span>
                                            </div>
                                        </div>
                                    ))}
                                </div>
                            </section>

                            <section className="rounded-xl border border-slate-200 bg-white p-4 shadow-sm">
                                <div className="flex items-center justify-between gap-3">
                                    <div className="flex items-center gap-2 text-slate-900">
                                        <Bug className="h-4 w-4 text-amber-600" />
                                        <h3 className="text-base font-semibold">{tr('Debug info', 'معلومات التصحيح')}</h3>
                                    </div>

                                    <button
                                        type="button"
                                        className={saButton.secondary}
                                        onClick={() => setShowDebugInfo((current) => !current)}
                                    >
                                        {showDebugInfo ? tr('Hide debug info', 'إخفاء معلومات التصحيح') : tr('Show debug info', 'عرض معلومات التصحيح')}
                                        {showDebugInfo ? <ChevronUp className="h-4 w-4" /> : <ChevronDown className="h-4 w-4" />}
                                    </button>
                                </div>

                                <p className="mt-2 text-sm leading-6 text-slate-500">
                                    {tr(
                                        'Raw identifiers and commission snapshots help investigate projection drift or payment-processing issues without leaving the drawer.',
                                        'تساعد المعرّفات الخام ولقطات العمولة في التحقيق في انحراف الإسقاط أو مشاكل معالجة الدفع دون مغادرة اللوحة.',
                                    )}
                                </p>

                                {showDebugInfo ? (
                                    <div className="mt-3 space-y-3">
                                        <div className="grid gap-2 md:grid-cols-3">
                                            <div className="rounded-lg border border-slate-200 bg-slate-50 p-3">
                                                <p className="text-[10px] font-semibold uppercase tracking-[0.14em] text-slate-500">
                                                    {tr('Event processing', 'معالجة الحدث')}
                                                </p>
                                                <p className="mt-1 text-sm font-semibold text-slate-950">
                                                    {order.eventStatus || tr('Unavailable', 'غير متاح')}
                                                </p>
                                            </div>
                                            <div className="rounded-lg border border-slate-200 bg-slate-50 p-3">
                                                <p className="text-[10px] font-semibold uppercase tracking-[0.14em] text-slate-500">
                                                    {tr('Payment IDs', 'معرّفات الدفع')}
                                                </p>
                                                <div className="mt-1 space-y-0.5">
                                                    {order.payments.length === 0 ? (
                                                        <p className="text-sm text-slate-500">{tr('Unavailable', 'غير متاح')}</p>
                                                    ) : (
                                                        order.payments.map((payment) => (
                                                            <p key={payment.paymentId} className="break-all font-mono text-xs text-slate-700" dir="ltr">
                                                                {payment.paymentId}
                                                            </p>
                                                        ))
                                                    )}
                                                </div>
                                            </div>
                                            <div className="rounded-lg border border-slate-200 bg-slate-50 p-3">
                                                <p className="text-[10px] font-semibold uppercase tracking-[0.14em] text-slate-500">
                                                    {tr('Sub-order IDs', 'معرّفات الطلبات الفرعية')}
                                                </p>
                                                <div className="mt-1 space-y-0.5">
                                                    {order.subOrders.length === 0 ? (
                                                        <p className="text-sm text-slate-500">{tr('Unavailable', 'غير متاح')}</p>
                                                    ) : (
                                                        order.subOrders.map((subOrder) => (
                                                            <p key={subOrder.subOrderId} className="break-all font-mono text-xs text-slate-700" dir="ltr">
                                                                {subOrder.subOrderId}
                                                            </p>
                                                        ))
                                                    )}
                                                </div>
                                            </div>
                                        </div>

                                        <div className="space-y-2">
                                            {order.subOrders
                                                .filter((subOrder) => subOrder.commissionSnapshotJson)
                                                .map((subOrder) => (
                                                    <div
                                                        key={`debug-${subOrder.subOrderId}`}
                                                        className="rounded-lg border border-slate-800 bg-slate-950 p-3 text-white"
                                                    >
                                                        <p className="text-[10px] font-semibold uppercase tracking-[0.14em] text-slate-300">
                                                            {tr('Commission snapshot', 'لقطة العمولة')} · {subOrder.vendorName}
                                                        </p>
                                                        <pre className="mt-2 overflow-x-auto whitespace-pre-wrap break-words text-xs leading-6 text-slate-100" dir="ltr">
                                                            {formatJson(subOrder.commissionSnapshotJson)}
                                                        </pre>
                                                    </div>
                                                ))}

                                            {order.subOrders.every((subOrder) => !subOrder.commissionSnapshotJson) ? (
                                                <EmptyState
                                                    title={tr('No commission snapshot', 'لا توجد لقطة عمولة')}
                                                    description={tr(
                                                        'No commission snapshot JSON is available for this order.',
                                                        'لا يتوفر JSON لقطة عمولة لهذا الطلب.',
                                                    )}
                                                />
                                            ) : null}
                                        </div>
                                    </div>
                                ) : null}
                            </section>

                            <section className="rounded-xl border border-slate-200 bg-white p-4 shadow-sm">
                                <div className="flex items-center gap-2 text-slate-900">
                                    <Clock3 className="h-4 w-4 text-emerald-600" />
                                    <h3 className="text-base font-semibold">{tr('Operational notes', 'ملاحظات تشغيلية')}</h3>
                                </div>
                                <p className="mt-2 text-sm leading-6 text-slate-600">
                                    {tr(
                                        'This drawer is loaded on demand so the main monitoring table stays light. Summary cards refresh independently on the page, while item and debug sections come directly from the current order projection for deeper inspection.',
                                        'تُحمَّل هذه اللوحة عند الطلب حتى يبقى جدول المتابعة خفيفًا. تُحدَّث بطاقات الملخص بشكل مستقل في الصفحة، بينما تأتي أقسام الأصناف والتصحيح مباشرة من إسقاط الطلب الحالي لفحص أعمق.',
                                    )}
                                </p>
                            </section>
                        </div>
                    ) : null}
                </div>
            </aside>
        </>
    )
}
