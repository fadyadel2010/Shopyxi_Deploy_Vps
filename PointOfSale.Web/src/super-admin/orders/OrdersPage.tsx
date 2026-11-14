import { useCallback, useEffect, useMemo, useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import {
    Activity,
    Download,
    PackageSearch,
    RefreshCw,
} from 'lucide-react'
import { useSearchParams } from 'react-router-dom'
import { useDebounce } from '../../hooks/useDebounce'
import { useSuperAdminLocale } from '../i18n/useSuperAdminLocale'
import {
    GuidePanel,
    InlineNotice,
    KpiCard,
    PageHeader,
    SuperAdminPage,
    saButton,
    saInput,
} from '../ui/SuperAdminUI'
import OrderDetailsDrawer from './OrderDetailsDrawer'
import OrdersFilters from './OrdersFilters'
import OrdersTable from './OrdersTable'
import {
    SUPER_ADMIN_ORDER_STATUS_OPTIONS,
    SUPER_ADMIN_PAYMENT_FILTER_OPTIONS,
    downloadSuperAdminOrdersCsv,
    getSuperAdminOrderDetails,
    getSuperAdminOrderStats,
    getSuperAdminOrderTenants,
    getSuperAdminOrders,
    isFailedPaymentStatus,
    isPendingOrderStatus,
    type SuperAdminOrderPaymentFilter,
    type SuperAdminOrderStatus,
    type SuperAdminOrdersFilters,
    type SuperAdminOrdersSortField,
    type SuperAdminSortDirection,
} from './ordersService'

const DEFAULT_PAGE = 1
const DEFAULT_PAGE_SIZE = 20
const DEFAULT_SORT_BY: SuperAdminOrdersSortField = 'CreatedAt'
const DEFAULT_SORT_DIRECTION: SuperAdminSortDirection = 'Desc'
const PENDING_ALERT_THRESHOLD = 10
const pageSizeOptions = [20, 50, 100]
const validSortFields: SuperAdminOrdersSortField[] = [
    'CreatedAt',
    'OrderNumber',
    'Customer',
    'Tenant',
    'TotalAmount',
    'Status',
    'PaymentStatus',
    'VendorCount',
]
const validSortDirections: SuperAdminSortDirection[] = ['Asc', 'Desc']

function parsePositiveInt(value: string | null, fallback: number): number {
    const parsed = Number.parseInt(value ?? '', 10)
    return Number.isFinite(parsed) && parsed > 0 ? parsed : fallback
}

function parseSortField(value: string | null): SuperAdminOrdersSortField {
    return validSortFields.includes(value as SuperAdminOrdersSortField)
        ? (value as SuperAdminOrdersSortField)
        : DEFAULT_SORT_BY
}

function parseSortDirection(value: string | null): SuperAdminSortDirection {
    return validSortDirections.includes(value as SuperAdminSortDirection)
        ? (value as SuperAdminSortDirection)
        : DEFAULT_SORT_DIRECTION
}

function parseStatus(value: string | null): SuperAdminOrderStatus | '' {
    return SUPER_ADMIN_ORDER_STATUS_OPTIONS.includes(value as SuperAdminOrderStatus)
        ? (value as SuperAdminOrderStatus)
        : ''
}

function parsePaymentStatus(value: string | null): SuperAdminOrderPaymentFilter | '' {
    return SUPER_ADMIN_PAYMENT_FILTER_OPTIONS.includes(value as SuperAdminOrderPaymentFilter)
        ? (value as SuperAdminOrderPaymentFilter)
        : ''
}

function buildFilters(searchParams: URLSearchParams): SuperAdminOrdersFilters {
    return {
        page: parsePositiveInt(searchParams.get('page'), DEFAULT_PAGE),
        pageSize: parsePositiveInt(searchParams.get('pageSize'), DEFAULT_PAGE_SIZE),
        search: searchParams.get('search') ?? '',
        status: parseStatus(searchParams.get('status')),
        paymentStatus: parsePaymentStatus(searchParams.get('paymentStatus')),
        tenantId: searchParams.get('tenantId') ?? '',
        fromDate: searchParams.get('fromDate') ?? '',
        toDate: searchParams.get('toDate') ?? '',
        sortBy: parseSortField(searchParams.get('sortBy')),
        sortDirection: parseSortDirection(searchParams.get('sortDirection')),
    }
}

function getErrorMessage(error: unknown, fallback: string): string {
    if (error instanceof Error && error.message) {
        return error.message
    }

    return fallback
}

export default function OrdersPage() {
    const [searchParams, setSearchParams] = useSearchParams()
    const [selectedOrderId, setSelectedOrderId] = useState<string | null>(null)
    const [isExporting, setIsExporting] = useState(false)
    const [exportError, setExportError] = useState<string | null>(null)
    const filters = useMemo(() => buildFilters(searchParams), [searchParams])
    const [searchInput, setSearchInput] = useState(filters.search ?? '')
    const debouncedSearch = useDebounce(searchInput, 400)
    const { tr, formatNumber, formatCurrency, formatDateTime } = useSuperAdminLocale()

    useEffect(() => {
        setSearchInput(filters.search ?? '')
    }, [filters.search])

    const updateParams = useCallback(
        (updates: Record<string, string | null | undefined>) => {
            setSearchParams((previous) => {
                const next = new URLSearchParams(previous)

                Object.entries(updates).forEach(([key, value]) => {
                    if (!value) {
                        next.delete(key)
                        return
                    }

                    next.set(key, value)
                })

                return next
            })
        },
        [setSearchParams],
    )

    useEffect(() => {
        if (debouncedSearch !== searchInput) {
            return
        }

        const currentSearch = filters.search ?? ''
        if (debouncedSearch === currentSearch) {
            return
        }

        updateParams({
            search: debouncedSearch || null,
            page: String(DEFAULT_PAGE),
        })
    }, [debouncedSearch, filters.search, searchInput, updateParams])

    const ordersQuery = useQuery({
        queryKey: ['super-admin', 'orders', filters],
        queryFn: () => getSuperAdminOrders(filters),
        staleTime: 30_000,
        refetchOnWindowFocus: true,
    })

    const statsQuery = useQuery({
        queryKey: ['super-admin', 'orders', 'stats', filters.tenantId || 'all'],
        queryFn: () => getSuperAdminOrderStats(filters.tenantId || undefined),
        staleTime: 15_000,
        refetchInterval: 30_000,
        refetchIntervalInBackground: false,
    })

    const tenantsQuery = useQuery({
        queryKey: ['super-admin', 'orders', 'tenants'],
        queryFn: getSuperAdminOrderTenants,
        staleTime: 5 * 60_000,
    })

    const detailsQuery = useQuery({
        queryKey: ['super-admin', 'orders', 'details', selectedOrderId],
        queryFn: () => getSuperAdminOrderDetails(selectedOrderId as string),
        enabled: Boolean(selectedOrderId),
        staleTime: 30_000,
    })

    const orders = useMemo(
        () => (ordersQuery.isFetching || ordersQuery.isError ? [] : (ordersQuery.data?.orders ?? [])),
        [ordersQuery.isFetching, ordersQuery.isError, ordersQuery.data?.orders],
    )
    const meta = ordersQuery.data?.meta
    const totalCount = meta?.totalCount ?? 0
    const totalPages = meta?.totalPages ?? 0
    const currentPageFailedPayments = orders.filter((order) => isFailedPaymentStatus(order.paymentStatus)).length
    const currentPagePendingOrders = orders.filter((order) => isPendingOrderStatus(order.status)).length
    const currentPageMultiVendorOrders = orders.filter((order) => order.vendorCount > 1).length
    const activeFilterCount = [
        filters.search,
        filters.status,
        filters.paymentStatus,
        filters.tenantId,
        filters.fromDate,
        filters.toDate,
    ].filter(Boolean).length

    const stats = statsQuery.data
    const showIssueAlert =
        (stats?.failedPayments ?? 0) > 0 || (stats?.pendingOrders ?? 0) >= PENDING_ALERT_THRESHOLD

    const issueAlertMessage = useMemo(() => {
        if (!stats) {
            return null
        }

        const parts: string[] = []

        if (stats.failedPayments > 0) {
            parts.push(
                tr(
                    `${formatNumber(stats.failedPayments)} failed payment${
                        stats.failedPayments === 1 ? '' : 's'
                    } require attention`,
                    `${formatNumber(stats.failedPayments)} عملية دفع فاشلة تحتاج إلى مراجعة`,
                ),
            )
        }

        if (stats.pendingOrders >= PENDING_ALERT_THRESHOLD) {
            parts.push(
                tr(
                    `${formatNumber(stats.pendingOrders)} pending orders are building up`,
                    `${formatNumber(stats.pendingOrders)} طلبًا معلّقًا يتراكم`,
                ),
            )
        }

        return parts.join(' · ')
    }, [formatNumber, stats, tr])

    function handleResetFilters() {
        setSearchInput('')
        setExportError(null)
        setSearchParams({
            page: String(DEFAULT_PAGE),
            pageSize: String(DEFAULT_PAGE_SIZE),
            sortBy: DEFAULT_SORT_BY,
            sortDirection: DEFAULT_SORT_DIRECTION,
        })
    }

    function setFilterParam(
        key: 'status' | 'paymentStatus' | 'tenantId' | 'fromDate' | 'toDate',
        value: string,
    ) {
        setExportError(null)
        updateParams({
            [key]: value || null,
            page: String(DEFAULT_PAGE),
        })
    }

    function handleSortChange(field: SuperAdminOrdersSortField) {
        const nextDirection: SuperAdminSortDirection =
            filters.sortBy === field && filters.sortDirection === 'Desc' ? 'Asc' : 'Desc'

        updateParams({
            sortBy: field,
            sortDirection: nextDirection,
            page: String(DEFAULT_PAGE),
        })
    }

    function handlePageChange(page: number) {
        updateParams({
            page: String(page),
        })
    }

    function handlePageSizeChange(pageSize: number) {
        updateParams({
            pageSize: String(pageSize),
            page: String(DEFAULT_PAGE),
        })
    }

    useEffect(() => {
        if (!selectedOrderId || ordersQuery.isFetching) {
            return
        }

        if (!orders.some((order) => order.orderId === selectedOrderId)) {
            setSelectedOrderId(null)
        }
    }, [orders, ordersQuery.isFetching, selectedOrderId])

    async function refreshAll() {
        await Promise.all([
            ordersQuery.refetch(),
            statsQuery.refetch(),
            tenantsQuery.refetch(),
            selectedOrderId ? detailsQuery.refetch() : Promise.resolve(),
        ])
    }

    async function handleExport() {
        setIsExporting(true)
        setExportError(null)

        try {
            const blob = await downloadSuperAdminOrdersCsv(filters)
            const downloadUrl = window.URL.createObjectURL(blob)
            const link = document.createElement('a')
            link.href = downloadUrl
            link.download = `commercehub-orders-${new Date().toISOString().slice(0, 10)}.csv`
            document.body.appendChild(link)
            link.click()
            link.remove()
            window.URL.revokeObjectURL(downloadUrl)
        } catch (error) {
            setExportError(
                getErrorMessage(
                    error,
                    tr(
                        'Unable to export the filtered orders right now.',
                        'تعذّر تصدير الطلبات المفلترة حاليًا.',
                    ),
                ),
            )
        } finally {
            setIsExporting(false)
        }
    }

    const guide = {
        en: {
            title: 'Orders monitoring guide',
            purpose:
                'Gives a read-only, platform-wide view of CommerceHub orders, payment health, vendor splits, and per-order financial snapshots so the Super Admin can investigate issues without re-running checkout or payment logic.',
            when:
                'Use it during daily operations, when a customer or vendor reports a missing or stuck order, when failed payments or pending backlogs need triage, or before any refund or payout decision.',
            steps: [
                'Read the daily KPIs and the operational attention notice for failed payments or pending backlog.',
                'Filter by order status, payment status, tenant, or date range, and search by order number or customer.',
                'Sort the table and open a row to inspect vendor splits, items, payment records, and commission snapshots.',
                'Use Export CSV when you need the current filtered set for offline review; it does not change any data.',
                'Refresh after resolving an issue and confirm the order and payment state changed.',
            ],
            statuses: [
                { label: 'Pending', description: 'The order is not complete yet and may need operational follow-up.' },
                { label: 'Stuck 2h+', description: 'A pending order older than the stuck threshold; treat as an active incident.' },
                { label: 'Failed payment', description: 'Payment status is failed or rejected; recovery may be required.' },
                { label: 'Completed / Delivered', description: 'The order reached its normal terminal state.' },
                { label: 'Cancelled / Refunded', description: 'The order was cancelled or refunded; financial entries remain auditable.' },
            ],
            safety: [
                'This screen is read-only; it does not capture, refund, or retry payments.',
                'Cached rows are withheld when a refresh fails so stale order authority is never shown as current.',
                'Financial snapshots are backend-computed. Do not use them to recalculate or override commission.',
                'CSV exports contain customer and order data; store and share them responsibly.',
            ],
        },
        ar: {
            title: 'دليل متابعة الطلبات',
            purpose:
                'يوفر رؤية للقراءة فقط لكل طلبات CommerceHub على مستوى المنصة، مع صحة الدفع وتقسيم البائعين ولقطة مالية لكل طلب، لتمكين الإدارة العليا من التحقيق دون إعادة تنفيذ منطق الدفع أو الطلب.',
            when:
                'استخدمه في العمليات اليومية، وعند إبلاغ عميل أو بائع عن طلب مفقود أو عالق، وعند الحاجة لفرز المدفوعات الفاشلة أو الطلبات المعلّقة المتراكمة، وقبل أي قرار استرداد أو تحويل.',
            steps: [
                'اقرأ مؤشرات اليوم وتنبيه الاهتمام التشغيلي للمدفوعات الفاشلة أو الطلبات المعلّقة.',
                'رشّح حسب حالة الطلب أو الدفع أو المتجر أو الفترة، وابحث برقم الطلب أو اسم العميل.',
                'رتّب الجدول وافتح صفًا لفحص تقسيم البائعين والأصناف وسجلات الدفع ولقطات العمولة.',
                'استخدم تصدير CSV عند الحاجة للنسخة المفلترة للمراجعة دون اتصال؛ لا يغيّر أي بيانات.',
                'حدّث الصفحة بعد حل المشكلة وتأكد من تغيّر حالة الطلب والدفع.',
            ],
            statuses: [
                { label: 'معلّق', description: 'الطلب غير مكتمل وقد يحتاج متابعة تشغيلية.' },
                { label: 'عالق أكثر من ساعتين', description: 'طلب معلّق تجاوز حد التعليق؛ تعامل معه كحادث نشط.' },
                { label: 'دفع فاشل', description: 'حالة الدفع فاشلة أو مرفوضة؛ قد يلزم استرداد أو إعادة محاولة.' },
                { label: 'مكتمل / تم التسليم', description: 'وصل الطلب إلى حالته النهائية الطبيعية.' },
                { label: 'ملغى / مرتجع', description: 'أُلغي الطلب أو أُرجع؛ تبقى القيود المالية قابلة للتدقيق.' },
            ],
            safety: [
                'هذه الشاشة للقراءة فقط؛ لا تلتقط أو تسترد أو تعيد محاولة الدفع.',
                'تُحجب الصفوف المخزنة عند فشل التحديث حتى لا تُعرض صلاحية طلبات قديمة كأنها حالية.',
                'اللقطات المالية محسوبة في الخادم. لا تستخدمها لإعادة حساب العمولة أو تجاوزها.',
                'ملفات CSV تحتوي بيانات عملاء وطلبات؛ احفظها وشاركها بمسؤولية.',
            ],
        },
    }

    if (ordersQuery.isError && !ordersQuery.data) {
        return (
            <SuperAdminPage>
                <PageHeader
                    eyebrow={tr('Super Admin · Orders', 'الإدارة العليا · الطلبات')}
                    title={tr('Orders monitoring', 'متابعة الطلبات')}
                    description={tr(
                        'Platform-wide order visibility, payment health, and order inspection.',
                        'رؤية شاملة للطلبات وصحة الدفع وفحص تفاصيل الطلب.',
                    )}
                />
                <InlineNotice tone="rose" title={tr('Orders monitoring unavailable', 'متابعة الطلبات غير متاحة')}>
                    <p>
                        {getErrorMessage(
                            ordersQuery.error,
                            tr(
                                'Unable to load the Super Admin orders dashboard.',
                                'تعذّر تحميل لوحة طلبات الإدارة العليا.',
                            ),
                        )}
                    </p>
                    <button type="button" className={`${saButton.secondary} mt-2`} onClick={() => refreshAll()}>
                        <RefreshCw className="h-4 w-4" />
                        {tr('Retry request', 'إعادة المحاولة')}
                    </button>
                </InlineNotice>
            </SuperAdminPage>
        )
    }

    return (
        <>
            <SuperAdminPage>
                <PageHeader
                    eyebrow={tr('Super Admin · Orders', 'الإدارة العليا · الطلبات')}
                    title={tr('Orders monitoring', 'متابعة الطلبات')}
                    description={tr(
                        'Every CommerceHub order with payment health, vendor splits, and on-demand inspection.',
                        'كل طلب في CommerceHub مع صحة الدفع وتقسيم البائعين وفحص التفاصيل عند الطلب.',
                    )}
                    actions={
                        <>
                            <button
                                type="button"
                                className={saButton.primary}
                                disabled={ordersQuery.isFetching || statsQuery.isFetching}
                                onClick={() => refreshAll()}
                            >
                                <RefreshCw
                                    className={`h-4 w-4 ${
                                        ordersQuery.isFetching || statsQuery.isFetching ? 'animate-spin' : ''
                                    }`}
                                />
                                {ordersQuery.isFetching || statsQuery.isFetching
                                    ? tr('Refreshing', 'جارٍ التحديث')
                                    : tr('Refresh', 'تحديث')}
                            </button>
                            <button
                                type="button"
                                className={saButton.secondary}
                                disabled={isExporting}
                                onClick={() => handleExport()}
                            >
                                <Download className={`h-4 w-4 ${isExporting ? 'animate-bounce' : ''}`} />
                                {isExporting ? tr('Exporting CSV', 'جارٍ التصدير') : tr('Export CSV', 'تصدير CSV')}
                            </button>
                        </>
                    }
                    aside={
                        <span className="inline-flex items-center gap-1.5 rounded-full border border-emerald-200 bg-emerald-50 px-2.5 py-1 text-xs font-semibold text-emerald-800">
                            <Activity className="h-3.5 w-3.5" />
                            {tr('Live monitoring', 'متابعة مباشرة')}
                        </span>
                    }
                />

                <div className="grid gap-3 sm:grid-cols-2 xl:grid-cols-4">
                    <KpiCard
                        label={tr('Total orders today', 'إجمالي طلبات اليوم')}
                        value={formatNumber(stats?.totalOrdersToday ?? 0)}
                        hint={tr('UTC day window', 'نافذة يوم UTC')}
                        tone="slate"
                        isLoading={statsQuery.isLoading && !stats}
                    />
                    <KpiCard
                        label={tr('Pending orders', 'طلبات معلّقة')}
                        value={formatNumber(stats?.pendingOrders ?? 0)}
                        hint={tr('Current pending backlog', 'التراكم المعلّق الحالي')}
                        tone="amber"
                        isLoading={statsQuery.isLoading && !stats}
                    />
                    <KpiCard
                        label={tr('Failed payments', 'مدفوعات فاشلة')}
                        value={formatNumber(stats?.failedPayments ?? 0)}
                        hint={tr('Requires payment follow-up', 'تتطلب متابعة الدفع')}
                        tone="rose"
                        isLoading={statsQuery.isLoading && !stats}
                    />
                    <KpiCard
                        label={tr('Revenue today', 'إيراد اليوم')}
                        value={formatCurrency(stats?.revenueToday ?? 0, stats?.currency ?? 'EGP')}
                        hint={tr('UTC day window', 'نافذة يوم UTC')}
                        tone="emerald"
                        isLoading={statsQuery.isLoading && !stats}
                    />
                </div>

                <div className="grid gap-3 sm:grid-cols-2 xl:grid-cols-4">
                    <div className="rounded-xl border border-slate-200 bg-white px-3.5 py-3 shadow-sm">
                        <p className="text-[10px] font-semibold uppercase tracking-[0.16em] text-slate-500">
                            {tr('API timestamp', 'وقت واجهة البرمجة')}
                        </p>
                        <p className="mt-1 text-sm font-semibold text-slate-900" dir="auto">
                            {formatDateTime(meta?.timestamp, tr('Awaiting sync', 'بانتظار المزامنة'))}
                        </p>
                    </div>
                    <div className="rounded-xl border border-slate-200 bg-white px-3.5 py-3 shadow-sm">
                        <p className="text-[10px] font-semibold uppercase tracking-[0.16em] text-slate-500">
                            {tr('Live stats as of', 'إحصاءات مباشرة حتى')}
                        </p>
                        <p className="mt-1 text-sm font-semibold text-slate-900" dir="auto">
                            {formatDateTime(stats?.asOfUtc, tr('Awaiting sync', 'بانتظار المزامنة'))}
                        </p>
                    </div>
                    <div className="rounded-xl border border-slate-200 bg-white px-3.5 py-3 shadow-sm">
                        <p className="text-[10px] font-semibold uppercase tracking-[0.16em] text-slate-500">
                            {tr('Active filters', 'المرشحات النشطة')}
                        </p>
                        <p className="mt-1 text-sm font-semibold text-slate-900" dir="auto">
                            {formatNumber(activeFilterCount)}
                        </p>
                    </div>
                    <div className="rounded-xl border border-slate-200 bg-white px-3.5 py-3 shadow-sm">
                        <p className="text-[10px] font-semibold uppercase tracking-[0.16em] text-slate-500">
                            {tr('Matching orders', 'طلبات مطابقة')}
                        </p>
                        <p className="mt-1 text-sm font-semibold text-slate-900" dir="auto">
                            {formatNumber(totalCount)}
                        </p>
                    </div>
                </div>

                <GuidePanel guide={guide} />

                {showIssueAlert && issueAlertMessage ? (
                    <InlineNotice tone="amber" title={tr('Operational attention needed', 'يلزم انتباه تشغيلي')}>
                        {issueAlertMessage}
                    </InlineNotice>
                ) : null}

                {statsQuery.isError ? (
                    <InlineNotice tone="amber" title={tr('Summary temporarily unavailable', 'الملخص غير متاح مؤقتًا')}>
                        {tr(
                            'Operational summary cards are temporarily unavailable. The detailed orders table is still live.',
                            'بطاقات الملخص التشغيلي غير متاحة مؤقتًا. جدول الطلبات التفصيلي ما زال مباشرًا.',
                        )}
                    </InlineNotice>
                ) : null}

                {exportError ? (
                    <InlineNotice tone="rose" title={tr('Export failed', 'فشل التصدير')}>
                        {exportError}
                    </InlineNotice>
                ) : null}

                <OrdersFilters
                    filters={filters}
                    searchValue={searchInput}
                    tenants={tenantsQuery.data ?? []}
                    isLoadingTenants={tenantsQuery.isLoading}
                    isRefreshing={ordersQuery.isFetching}
                    onSearchChange={setSearchInput}
                    onStatusChange={(value) => setFilterParam('status', value)}
                    onPaymentStatusChange={(value) => setFilterParam('paymentStatus', value)}
                    onTenantChange={(value) => setFilterParam('tenantId', value)}
                    onFromDateChange={(value) => setFilterParam('fromDate', value)}
                    onToDateChange={(value) => setFilterParam('toDate', value)}
                    onReset={handleResetFilters}
                />

                {ordersQuery.isError && ordersQuery.data ? (
                    <InlineNotice tone="amber" title={tr('Cached rows withheld', 'تم حجب الصفوف المخزنة')}>
                        {tr(
                            'Current order authority refresh failed, so cached order rows are withheld until the next accepted backend response.',
                            'فشل تحديث صلاحية الطلبات الحالية، لذا تُحجب الصفوف المخزنة حتى أول استجابة خادم مقبولة.',
                        )}
                    </InlineNotice>
                ) : null}

                {tenantsQuery.isError ? (
                    <InlineNotice tone="amber" title={tr('Tenant lookup unavailable', 'بحث المتاجر غير متاح')}>
                        {tr(
                            'Tenant lookup is temporarily unavailable, so the tenant filter falls back to "All tenants" until the next successful refresh.',
                            'بحث المتاجر غير متاح مؤقتًا، لذا يعود مرشح المتجر إلى "كل المتاجر" حتى التحديث الناجح التالي.',
                        )}
                    </InlineNotice>
                ) : null}

                <OrdersTable
                    orders={orders}
                    isLoading={ordersQuery.isLoading || ordersQuery.isFetching}
                    isFetching={ordersQuery.isFetching}
                    selectedOrderId={selectedOrderId}
                    sortBy={filters.sortBy}
                    sortDirection={filters.sortDirection}
                    onSortChange={handleSortChange}
                    onSelectOrder={setSelectedOrderId}
                />

                <section className="flex flex-col gap-3 rounded-2xl border border-slate-200 bg-white px-4 py-4 shadow-sm lg:flex-row lg:items-center lg:justify-between">
                    <div className="flex items-start gap-3">
                        <div className="flex h-9 w-9 shrink-0 items-center justify-center rounded-xl bg-slate-100 text-slate-600">
                            <PackageSearch className="h-4 w-4" />
                        </div>
                        <div className="min-w-0">
                            <p className="text-[10px] font-semibold uppercase tracking-[0.16em] text-slate-500">
                                {tr('Pagination', 'ترقيم الصفحات')}
                            </p>
                            <p className="mt-0.5 text-sm leading-6 text-slate-700" dir="auto">
                                {tr(
                                    `Page ${filters.page} of ${totalPages || 1} with ${formatNumber(totalCount)} matching orders. This page includes ${formatNumber(currentPageFailedPayments)} failed payment issues, ${formatNumber(currentPagePendingOrders)} pending orders, and ${formatNumber(currentPageMultiVendorOrders)} multi-vendor orders.`,
                                    `صفحة ${filters.page} من ${totalPages || 1} مع ${formatNumber(totalCount)} طلبًا مطابقًا. تتضمن هذه الصفحة ${formatNumber(currentPageFailedPayments)} مشكلة دفع فاشل، و${formatNumber(currentPagePendingOrders)} طلبًا معلّقًا، و${formatNumber(currentPageMultiVendorOrders)} طلبًا متعدد البائعين.`,
                                )}
                            </p>
                        </div>
                    </div>

                    <div className="flex flex-col gap-2 sm:flex-row sm:items-center">
                        <label className="flex items-center gap-2 text-sm text-slate-600">
                            <span>{tr('Page size', 'حجم الصفحة')}</span>
                            <select
                                aria-label={tr('Orders per page', 'الطلبات لكل صفحة')}
                                value={filters.pageSize}
                                onChange={(event) => handlePageSizeChange(Number(event.target.value))}
                                className={`${saInput} w-auto py-1.5`}
                            >
                                {pageSizeOptions.map((size) => (
                                    <option key={size} value={size}>
                                        {size}
                                    </option>
                                ))}
                            </select>
                        </label>

                        <div className="flex items-center gap-2">
                            <button
                                type="button"
                                className={saButton.secondary}
                                disabled={filters.page <= 1}
                                onClick={() => handlePageChange(filters.page - 1)}
                            >
                                {tr('Previous', 'السابق')}
                            </button>
                            <button
                                type="button"
                                className={saButton.primary}
                                disabled={!totalPages || filters.page >= totalPages}
                                onClick={() => handlePageChange(filters.page + 1)}
                            >
                                {tr('Next', 'التالي')}
                            </button>
                        </div>
                    </div>
                </section>
            </SuperAdminPage>

            <OrderDetailsDrawer
                isOpen={Boolean(selectedOrderId)}
                order={detailsQuery.data}
                isLoading={detailsQuery.isLoading || detailsQuery.isFetching}
                isError={detailsQuery.isError}
                errorMessage={
                    detailsQuery.isError
                        ? getErrorMessage(
                              detailsQuery.error,
                              tr(
                                  'Unable to load the requested order details.',
                                  'تعذّر تحميل تفاصيل الطلب المطلوب.',
                              ),
                          )
                        : null
                }
                onClose={() => setSelectedOrderId(null)}
                onRetry={() => detailsQuery.refetch()}
            />
        </>
    )
}
