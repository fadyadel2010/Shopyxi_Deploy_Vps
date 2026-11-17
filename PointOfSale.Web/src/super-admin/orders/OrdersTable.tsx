import {
    AlertCircle,
    ArrowDown,
    ArrowUp,
    ArrowUpDown,
    ChevronRight,
    Clock3,
    Package2,
} from 'lucide-react'
import { useSuperAdminLocale } from '../i18n/useSuperAdminLocale'
import { EmptyState, StatusBadge, saTable } from '../ui/SuperAdminUI'
import {
    isFailedPaymentStatus,
    isPendingOrderStatus,
    type SuperAdminOrderListItem,
    type SuperAdminOrdersSortField,
    type SuperAdminSortDirection,
} from './ordersService'

interface OrdersTableProps {
    orders: SuperAdminOrderListItem[]
    isLoading: boolean
    isFetching: boolean
    isUnavailable?: boolean
    selectedOrderId?: string | null
    sortBy: SuperAdminOrdersSortField
    sortDirection: SuperAdminSortDirection
    onSortChange: (field: SuperAdminOrdersSortField) => void
    onSelectOrder: (orderId: string) => void
}

const STUCK_THRESHOLD_MINUTES = 120

const tableColumns: Array<{ key: SuperAdminOrdersSortField; label: string; labelAr: string }> = [
    { key: 'OrderNumber', label: 'Order ID', labelAr: 'الطلب' },
    { key: 'Customer', label: 'Customer', labelAr: 'العميل' },
    { key: 'Tenant', label: 'Tenant', labelAr: 'المتجر' },
    { key: 'VendorCount', label: 'Vendors', labelAr: 'البائعون' },
    { key: 'TotalAmount', label: 'Total Amount', labelAr: 'الإجمالي' },
    { key: 'Status', label: 'Status', labelAr: 'الحالة' },
    { key: 'PaymentStatus', label: 'Payment Status', labelAr: 'حالة الدفع' },
    { key: 'CreatedAt', label: 'Created At', labelAr: 'تاريخ الإنشاء' },
]

const ORDER_STATUS_LABELS: Record<string, string> = {
    Pending: 'معلّق',
    Authorized: 'مفوّض',
    PartiallyPaid: 'مدفوع جزئيًا',
    AwaitingVerification: 'بانتظار التحقق',
    CashOnDelivery: 'الدفع عند الاستلام',
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
}

type Tone = 'slate' | 'emerald' | 'amber' | 'rose' | 'sky'

function getOrderStatusTone(status: string): Tone {
    switch (status) {
        case 'Completed':
        case 'Delivered':
            return 'emerald'
        case 'Pending':
        case 'OnHold':
        case 'Awaiting Payment':
        case 'Payment Pending':
            return 'amber'
        case 'Cancelled':
        case 'Refunded':
        case 'Returned':
        case 'Failed':
            return 'rose'
        default:
            return 'sky'
    }
}

function getPaymentStatusTone(status: string): Tone {
    switch (status) {
        case 'Failed':
        case 'Rejected':
        case 'Expired':
        case 'AmountMismatch':
            return 'rose'
        case 'Collected':
        case 'Paid':
        case 'Settled':
            return 'emerald'
        case 'Refunded':
        case 'PartiallyRefunded':
            return 'slate'
        default:
            return 'amber'
    }
}

function truncateId(value: string): string {
    return value.length <= 12 ? value : `${value.slice(0, 8)}...${value.slice(-4)}`
}

function TableSkeleton() {
    return (
        <tbody className="divide-y divide-slate-100">
            {Array.from({ length: 8 }).map((_, index) => (
                <tr key={`orders-skeleton-${index}`} className="animate-pulse">
                    <td className="px-4 py-3">
                        <div className="h-4 w-28 rounded-full bg-slate-200" />
                        <div className="mt-2 h-3 w-36 rounded-full bg-slate-100" />
                    </td>
                    {Array.from({ length: 7 }).map((__, cellIndex) => (
                        <td key={`orders-skeleton-${index}-${cellIndex}`} className="px-4 py-3">
                            <div className="h-4 w-24 rounded-full bg-slate-100" />
                        </td>
                    ))}
                    <td className="px-4 py-3">
                        <div className="h-4 w-20 rounded-full bg-slate-100" />
                    </td>
                </tr>
            ))}
        </tbody>
    )
}

function SortButton({
    label,
    active,
    direction,
    onClick,
}: {
    label: string
    active: boolean
    direction: SuperAdminSortDirection
    onClick: () => void
}) {
    return (
        <button
            type="button"
            className="inline-flex items-center gap-1.5 text-start text-xs font-semibold uppercase tracking-wide text-slate-500 transition hover:text-slate-900"
            onClick={onClick}
        >
            <span>{label}</span>
            {active ? (
                direction === 'Asc' ? <ArrowUp className="h-3.5 w-3.5" /> : <ArrowDown className="h-3.5 w-3.5" />
            ) : (
                <ArrowUpDown className="h-3.5 w-3.5" />
            )}
        </button>
    )
}

export default function OrdersTable({
    orders,
    isLoading,
    isFetching,
    isUnavailable,
    selectedOrderId,
    sortBy,
    sortDirection,
    onSortChange,
    onSelectOrder,
}: OrdersTableProps) {
    const { tr, formatNumber, formatCurrency, formatDateTime, locale } = useSuperAdminLocale()

    function getRelativeAge(value: string): { label: string; minutesOld: number | null; isStuck: boolean } {
        const parsed = new Date(value)
        if (Number.isNaN(parsed.getTime())) {
            return { label: tr('Awaiting sync', 'بانتظار المزامنة'), minutesOld: null, isStuck: false }
        }

        const diffMs = Math.max(0, Date.now() - parsed.getTime())
        const minutesOld = Math.floor(diffMs / 60_000)

        if (minutesOld < 1) {
            return { label: tr('Created just now', 'أُنشئ الآن'), minutesOld, isStuck: false }
        }

        const relative = new Intl.RelativeTimeFormat(locale, { numeric: 'auto' })

        if (minutesOld < 60) {
            return {
                label: tr(`Created ${minutesOld} minutes ago`, `أُنشئ منذ ${relative.format(-minutesOld, 'minute')}`),
                minutesOld,
                isStuck: minutesOld >= STUCK_THRESHOLD_MINUTES,
            }
        }

        const hoursOld = Math.floor(minutesOld / 60)
        if (hoursOld < 24) {
            return {
                label: tr(`Created ${hoursOld} hours ago`, `أُنشئ منذ ${relative.format(-hoursOld, 'hour')}`),
                minutesOld,
                isStuck: minutesOld >= STUCK_THRESHOLD_MINUTES,
            }
        }

        const daysOld = Math.floor(hoursOld / 24)
        return {
            label: tr(`Created ${daysOld} days ago`, `أُنشئ منذ ${relative.format(-daysOld, 'day')}`),
            minutesOld,
            isStuck: minutesOld >= STUCK_THRESHOLD_MINUTES,
        }
    }

    return (
        <section className="overflow-hidden rounded-2xl border border-slate-200 bg-white shadow-sm">
            <div className="flex flex-col gap-2 border-b border-slate-100 px-4 py-3.5 sm:flex-row sm:items-center sm:justify-between sm:px-5">
                <div>
                    <h2 className="text-base font-semibold text-slate-950">
                        {tr('Platform-wide orders', 'طلبات المنصة')}
                    </h2>
                    <p className="mt-0.5 text-sm text-slate-500">
                        {tr(
                            'Select a row to inspect vendor splits, items, payments, and commission snapshots.',
                            'اختر صفًا لفحص تقسيم البائعين والأصناف والمدفوعات ولقطات العمولة.',
                        )}
                    </p>
                </div>

                <span className="inline-flex items-center gap-1.5 self-start rounded-full border border-slate-200 bg-slate-50 px-2.5 py-1 text-xs font-medium text-slate-700 sm:self-auto">
                    <Package2 className="h-3.5 w-3.5" />
                    {isFetching && !isLoading
                        ? tr('Refreshing results', 'جارٍ تحديث النتائج')
                        : tr(`${formatNumber(orders.length)} rows loaded`, `${formatNumber(orders.length)} صفًا محمّلًا`)}
                </span>
            </div>

            <div className="overflow-x-auto">
                <table className="w-full min-w-[1180px] divide-y divide-slate-100">
                    <thead className="bg-slate-50/90">
                        <tr>
                            {tableColumns.map((column) => (
                                <th key={column.key} className={saTable.headerCell}
                                    aria-sort={sortBy === column.key ? (sortDirection === 'Asc' ? 'ascending' : 'descending') : 'none'}>
                                    <SortButton
                                        label={tr(column.label, column.labelAr)}
                                        active={sortBy === column.key}
                                        direction={sortDirection}
                                        onClick={() => onSortChange(column.key)}
                                    />
                                </th>
                            ))}
                            <th className={`${saTable.headerCell} text-end`}>{tr('Inspect', 'فحص')}</th>
                        </tr>
                    </thead>

                    {isLoading ? <TableSkeleton /> : null}

                    {!isLoading ? (
                        <tbody className="divide-y divide-slate-100">
                            {orders.length === 0 ? (
                                <tr>
                                    <td colSpan={tableColumns.length + 1} className="px-4 py-6">
                                        <EmptyState
                                            title={isUnavailable ? tr('Orders unavailable', 'الطلبات غير متاحة') : tr('No orders matched these filters', 'لا توجد طلبات تطابق هذه المرشحات')}
                                            description={isUnavailable ? tr('Resolve the request or filter error before interpreting counts.', 'عالج خطأ الطلب أو المرشحات قبل تفسير الأعداد.') : tr(
                                                'Adjust the search, payment filter, or date range to broaden the operational view.',
                                                'عدّل البحث أو مرشح الدفع أو الفترة لتوسيع العرض التشغيلي.',
                                            )}
                                        />
                                    </td>
                                </tr>
                            ) : (
                                orders.map((order) => {
                                    const hasFailedPayment = isFailedPaymentStatus(order.paymentStatus)
                                    const isPending = isPendingOrderStatus(order.status)
                                    const age = getRelativeAge(order.createdAt)
                                    const isStuckPending = isPending && age.isStuck

                                    const rowTone = hasFailedPayment
                                        ? 'bg-rose-50/65 hover:bg-rose-50'
                                        : isStuckPending
                                          ? 'bg-amber-100/70 hover:bg-amber-100/80'
                                          : isPending
                                            ? 'bg-amber-50/65 hover:bg-amber-50'
                                            : 'bg-white hover:bg-slate-50/80'

                                    const isSelected = selectedOrderId === order.orderId

                                    return (
                                        <tr
                                            key={order.orderId}
                                            tabIndex={0}
                                            className={[
                                                'cursor-pointer transition focus:outline-none focus:ring-2 focus:ring-inset focus:ring-emerald-200',
                                                rowTone,
                                                isSelected ? 'ring-2 ring-inset ring-emerald-200' : '',
                                            ].join(' ')}
                                            onClick={() => onSelectOrder(order.orderId)}
                                            onKeyDown={(event) => {
                                                if (event.key === 'Enter' || event.key === ' ') {
                                                    event.preventDefault()
                                                    onSelectOrder(order.orderId)
                                                }
                                            }}
                                        >
                                            <td className="px-4 py-3 align-top">
                                                <div className="space-y-1.5">
                                                    <div className="text-sm font-semibold text-slate-950" dir="ltr">
                                                        {order.orderNumber}
                                                    </div>
                                                    <div className="font-mono text-xs text-slate-500" dir="ltr">
                                                        {truncateId(order.orderId)}
                                                    </div>

                                                    <div className="flex flex-wrap gap-1.5">
                                                        {hasFailedPayment ? (
                                                            <StatusBadge tone="rose">
                                                                <AlertCircle className="h-3.5 w-3.5" />
                                                                {tr('Failed payment', 'دفع فاشل')}
                                                            </StatusBadge>
                                                        ) : null}

                                                        {isStuckPending ? (
                                                            <StatusBadge tone="amber">
                                                                <Clock3 className="h-3.5 w-3.5" />
                                                                {tr('Stuck 2h+', 'عالق +ساعتين')}
                                                            </StatusBadge>
                                                        ) : null}

                                                        {isPending && !isStuckPending ? (
                                                            <StatusBadge tone="amber">
                                                                <Clock3 className="h-3.5 w-3.5" />
                                                                {tr('Pending review', 'بانتظار المراجعة')}
                                                            </StatusBadge>
                                                        ) : null}
                                                    </div>
                                                </div>
                                            </td>

                                            <td className="px-4 py-3 align-top">
                                                <div className="text-sm font-medium text-slate-900">
                                                    {order.customerName || tr('Unknown customer', 'عميل غير معروف')}
                                                </div>
                                            </td>

                                            <td className="px-4 py-3 align-top">
                                                <div className="text-sm font-medium text-slate-900">
                                                    {order.vendorCount > 1
                                                        ? tr(
                                                              `${order.subOrders[0]?.vendorName} +${order.vendorCount - 1} more`,
                                                              `${order.subOrders[0]?.vendorName} و${formatNumber(order.vendorCount - 1)} آخرون`,
                                                          )
                                                        : order.subOrders[0]?.vendorName || tr('No vendors', 'لا يوجد بائعون')}
                                                </div>
                                                <div className="mt-0.5 text-xs text-slate-500">
                                                    {order.subOrders
                                                        .slice(0, 2)
                                                        .map((subOrder) => subOrder.vendorName)
                                                        .join(', ') || tr('No vendor summary', 'لا يوجد ملخص بائعين')}
                                                </div>
                                            </td>

                                            <td className="px-4 py-3 align-top">
                                                <div className="text-sm font-semibold text-slate-900">
                                                    {tr(
                                                        `${formatNumber(order.vendorCount)} ${
                                                            order.vendorCount === 1 ? 'vendor' : 'vendors'
                                                        }`,
                                                        `${formatNumber(order.vendorCount)} بائع`,
                                                    )}
                                                </div>
                                                <div className="mt-0.5 text-xs text-slate-500">
                                                    {order.vendorCount > 1
                                                        ? tr('Multi-vendor split order', 'طلب مقسّم بين بائعين')
                                                        : tr('Single vendor', 'بائع واحد')}
                                                </div>
                                            </td>

                                            <td className="px-4 py-3 align-top">
                                                <div className="text-sm font-semibold text-slate-950" dir="auto">
                                                    {formatCurrency(order.totalAmount, order.currency)}
                                                </div>
                                            </td>

                                            <td className="px-4 py-3 align-top">
                                                <StatusBadge tone={getOrderStatusTone(order.status)}>
                                                    {tr(order.status, ORDER_STATUS_LABELS[order.status] ?? order.status)}
                                                </StatusBadge>
                                            </td>

                                            <td className="px-4 py-3 align-top">
                                                <StatusBadge tone={getPaymentStatusTone(order.paymentStatus)}>
                                                    {tr(
                                                        order.paymentStatus,
                                                        PAYMENT_STATUS_LABELS[order.paymentStatus] ?? order.paymentStatus,
                                                    )}
                                                </StatusBadge>
                                            </td>

                                            <td className="px-4 py-3 align-top">
                                                <div className="text-sm font-medium text-slate-900">{age.label}</div>
                                                <div className="mt-0.5 text-xs text-slate-500" dir="auto">
                                                    {formatDateTime(order.createdAt)}
                                                </div>
                                            </td>

                                            <td className="px-4 py-3 align-top text-end">
                                                <span className="inline-flex items-center gap-1 text-sm font-semibold text-emerald-700">
                                                    {tr('Details', 'التفاصيل')}
                                                    <ChevronRight className="h-4 w-4 rtl:rotate-180" />
                                                </span>
                                            </td>
                                        </tr>
                                    )
                                })
                            )}
                        </tbody>
                    ) : null}
                </table>
            </div>
        </section>
    )
}
