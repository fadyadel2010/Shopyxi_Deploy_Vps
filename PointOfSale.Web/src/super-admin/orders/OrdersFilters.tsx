import { RotateCcw, Search, SlidersHorizontal } from 'lucide-react'
import { useSuperAdminLocale } from '../i18n/useSuperAdminLocale'
import { saButton, saInput } from '../ui/SuperAdminUI'
import {
    SUPER_ADMIN_ORDER_STATUS_OPTIONS,
    SUPER_ADMIN_PAYMENT_FILTER_OPTIONS,
    type SuperAdminOrderPaymentFilter,
    type SuperAdminOrderStatus,
    type SuperAdminOrdersFilters,
    type SuperAdminTenantOption,
} from './ordersService'

interface OrdersFiltersProps {
    filters: SuperAdminOrdersFilters
    searchValue: string
    tenants: SuperAdminTenantOption[]
    isLoadingTenants: boolean
    isRefreshing: boolean
    onSearchChange: (value: string) => void
    onStatusChange: (value: SuperAdminOrderStatus | '') => void
    onPaymentStatusChange: (value: SuperAdminOrderPaymentFilter | '') => void
    onTenantChange: (value: string) => void
    onFromDateChange: (value: string) => void
    onToDateChange: (value: string) => void
    onReset: () => void
}

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

const PAYMENT_FILTER_LABELS: Record<string, string> = {
    Paid: 'مدفوع',
    Unpaid: 'غير مدفوع',
    Failed: 'فاشل',
}

export default function OrdersFilters({
    filters,
    searchValue,
    tenants,
    isLoadingTenants,
    isRefreshing,
    onSearchChange,
    onStatusChange,
    onPaymentStatusChange,
    onTenantChange,
    onFromDateChange,
    onToDateChange,
    onReset,
}: OrdersFiltersProps) {
    const { tr, locale } = useSuperAdminLocale()

    return (
        <section className="overflow-hidden rounded-2xl border border-slate-200 bg-white shadow-sm">
            <div className="flex flex-col gap-3 border-b border-slate-100 px-4 py-3.5 sm:flex-row sm:items-center sm:justify-between sm:px-5">
                <div className="flex items-start gap-2.5">
                    <span className="flex h-8 w-8 shrink-0 items-center justify-center rounded-lg bg-slate-100 text-slate-700">
                        <SlidersHorizontal className="h-4 w-4" />
                    </span>
                    <div>
                        <h2 className="text-sm font-semibold text-slate-950">{tr('Order filters', 'مرشحات الطلبات')}</h2>
                        <p className="mt-0.5 text-xs leading-5 text-slate-500">
                            {tr(
                                'Filters stay in the URL so the current view can be shared and reloaded.',
                                'تبقى المرشحات في الرابط حتى يمكن مشاركة العرض الحالي وإعادة تحميله.',
                            )}
                        </p>
                    </div>
                </div>

                <button type="button" className={saButton.secondary} onClick={onReset}>
                    <RotateCcw className="h-4 w-4" />
                    {tr('Reset filters', 'إعادة ضبط المرشحات')}
                </button>
            </div>

            <div className="grid gap-3 px-4 py-4 sm:grid-cols-2 lg:grid-cols-3 xl:grid-cols-6 sm:px-5">
                <div className="space-y-1.5 sm:col-span-2">
                    <span className="text-[10px] font-semibold uppercase tracking-[0.16em] text-slate-500">
                        {tr('Search', 'بحث')}
                    </span>
                    <div className="relative">
                        <Search className="pointer-events-none absolute start-3.5 top-1/2 h-4 w-4 -translate-y-1/2 text-slate-400" />
                        <input
                            aria-label={tr('Search orders', 'بحث الطلبات')}
                            value={searchValue}
                            onChange={(event) => onSearchChange(event.target.value)}
                            placeholder={tr(
                                'Order ID, order number, or customer',
                                'معرّف الطلب أو رقمه أو العميل',
                            )}
                            className={`${saInput} ps-10`}
                        />
                    </div>
                </div>

                <label className="space-y-1.5">
                    <span className="text-[10px] font-semibold uppercase tracking-[0.16em] text-slate-500">
                        {tr('Status', 'الحالة')}
                    </span>
                    <select
                        aria-label={tr('Order status', 'حالة الطلب')}
                        value={filters.status ?? ''}
                        onChange={(event) => onStatusChange(event.target.value as SuperAdminOrderStatus | '')}
                        className={saInput}
                    >
                        <option value="">{tr('All statuses', 'كل الحالات')}</option>
                        {SUPER_ADMIN_ORDER_STATUS_OPTIONS.map((status) => (
                            <option key={status} value={status}>
                                {tr(status, ORDER_STATUS_LABELS[status] ?? status)}
                            </option>
                        ))}
                    </select>
                </label>

                <label className="space-y-1.5">
                    <span className="text-[10px] font-semibold uppercase tracking-[0.16em] text-slate-500">
                        {tr('Payment', 'الدفع')}
                    </span>
                    <select
                        aria-label={tr('Payment status', 'حالة الدفع')}
                        value={filters.paymentStatus ?? ''}
                        onChange={(event) =>
                            onPaymentStatusChange(event.target.value as SuperAdminOrderPaymentFilter | '')
                        }
                        className={saInput}
                    >
                        <option value="">{tr('All payments', 'كل المدفوعات')}</option>
                        {SUPER_ADMIN_PAYMENT_FILTER_OPTIONS.map((status) => (
                            <option key={status} value={status}>
                                {tr(status, PAYMENT_FILTER_LABELS[status] ?? status)}
                            </option>
                        ))}
                    </select>
                </label>

                <label className="space-y-1.5">
                    <span className="text-[10px] font-semibold uppercase tracking-[0.16em] text-slate-500">
                        {tr('Tenant', 'المتجر')}
                    </span>
                    <select
                        aria-label={tr('Order tenant', 'متجر الطلب')}
                        value={filters.tenantId ?? ''}
                        disabled={isLoadingTenants}
                        onChange={(event) => onTenantChange(event.target.value)}
                        className={saInput}
                    >
                        <option value="">
                            {isLoadingTenants
                                ? tr('Loading tenants…', 'جارٍ تحميل المتاجر…')
                                : tr('All tenants', 'كل المتاجر')}
                        </option>
                        {filters.tenantId && !tenants.some(tenant => tenant.id === filters.tenantId) ? (
                            <option value={filters.tenantId}>{tr('Selected store (label unavailable)', 'المتجر المحدد (الاسم غير متاح)')}</option>
                        ) : null}
                        {tenants.map((tenant) => (
                            <option key={tenant.id} value={tenant.id}>
                                {tenant.name}
                            </option>
                        ))}
                    </select>
                </label>

                <label className="space-y-1.5">
                    <span className="text-[10px] font-semibold uppercase tracking-[0.16em] text-slate-500">
                        {tr('From', 'من')}
                    </span>
                    <input
                        type="date"
                        lang={locale}
                        aria-label={tr('Orders created from date', 'طلبات أُنشئت من تاريخ')}
                        value={filters.fromDate ?? ''}
                        onChange={(event) => onFromDateChange(event.target.value)}
                        className={saInput}
                    />
                </label>

                <label className="space-y-1.5">
                    <span className="text-[10px] font-semibold uppercase tracking-[0.16em] text-slate-500">
                        {tr('To', 'إلى')}
                    </span>
                    <input
                        type="date"
                        lang={locale}
                        aria-label={tr('Orders created through date', 'طلبات أُنشئت حتى تاريخ')}
                        value={filters.toDate ?? ''}
                        onChange={(event) => onToDateChange(event.target.value)}
                        className={saInput}
                    />
                </label>
            </div>

            <div className="border-t border-slate-100 bg-slate-50/80 px-4 py-2.5 text-xs font-medium text-slate-500 sm:px-5">
                {isRefreshing
                    ? tr('Refreshing orders snapshot…', 'جارٍ تحديث لقطة الطلبات…')
                    : tr(
                          'Orders and stats refresh every 30 seconds while this tab is active. Date filters use inclusive UTC calendar days; the browser controls the native date format.',
                          'تُحدَّث الطلبات والإحصاءات كل ٣٠ ثانية ما دام التبويب نشطًا. تشمل مرشحات التاريخ أيام UTC كاملة؛ ويتحكم المتصفح بتنسيق حقل التاريخ الأصلي.',
                      )}
            </div>
        </section>
    )
}
