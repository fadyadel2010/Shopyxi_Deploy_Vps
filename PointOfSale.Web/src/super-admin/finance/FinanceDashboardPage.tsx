import { useDeferredValue, useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { RefreshCw, ShieldCheck, TrendingUp } from 'lucide-react'
import { useSuperAdminLocale } from '../i18n/useSuperAdminLocale'
import {
    GuidePanel,
    InlineNotice,
    PageHeader,
    SectionCard,
    StatusBadge,
    SuperAdminPage,
    saButton,
} from '../ui/SuperAdminUI'
import FinanceCharts from './FinanceCharts'
import FinanceKpiCards from './FinanceKpiCards'
import FinancePerformanceTables from './FinancePerformanceTables'
import FinanceReconciliationPanel from './FinanceReconciliationPanel'
import VendorFinanceWorkspace from './VendorFinanceWorkspace'
import { financeIntegrityLabel, financeSourceLabel } from './financePresentation'
import {
    getFinancialIntegrityCheck,
    getFinancialOverview,
    getFinancialPromos,
    getFinancialRefunds,
    getFinancialTrends,
    getFinancialVendors,
    type SuperAdminFinancialTrendGroupBy,
    type SuperAdminPromoAnalyticsSortField,
    type SuperAdminSortDirection,
    type SuperAdminVendorPerformanceSortField,
} from './financeService'

const COMMERCEHUB_AUTHORITY_CURRENCY = 'EGP'
const UNSUPPORTED_COMMERCEHUB_CURRENCY = 'CommerceHub currency blocked'

export default function FinanceDashboardPage() {
    const {
        tr,
        locale,
        formatCurrency: formatLocalizedCurrency,
        formatDateTime: formatLocalizedDateTime,
        formatNumber,
    } = useSuperAdminLocale()

    const formatCurrency = (value: number | null | undefined, currency = COMMERCEHUB_AUTHORITY_CURRENCY) => {
        if (String(currency || '').trim().toUpperCase() !== COMMERCEHUB_AUTHORITY_CURRENCY) {
            return tr(UNSUPPORTED_COMMERCEHUB_CURRENCY, 'عملة CommerceHub غير مدعومة')
        }
        if (value == null || !Number.isFinite(value)) return '—'
        return formatLocalizedCurrency(value, COMMERCEHUB_AUTHORITY_CURRENCY)
    }

    const formatDateTime = (value?: string | null) =>
        formatLocalizedDateTime(value, tr('Pending', 'قيد الانتظار'))

    const formatScopeDate = (value?: string | null) => {
        if (!value) return tr('Pending', 'قيد الانتظار')
        const datePart = value.slice(0, 10)
        if (!/^\d{4}-\d{2}-\d{2}$/.test(datePart)) {
            return formatDateTime(value)
        }
        const date = new Date(`${datePart}T00:00:00Z`)
        return new Intl.DateTimeFormat(locale, { dateStyle: 'medium', timeZone: 'UTC' }).format(date)
    }

    const getRefundAlertLabel = (alertType: string) => {
        switch (alertType) {
            case 'REFUND_RATE_SPIKE':
                return tr('Refund rate spike', 'قفزة في معدل المرتجعات')
            case 'REFUND_RATE_ELEVATED':
                return tr('Elevated refund rate', 'ارتفاع معدل المرتجعات')
            case 'REFUND_BUCKET_SPIKE':
                return tr('Refund spike in period', 'قفزة مرتجعات في الفترة')
            case 'PROMO_HEAVY_REFUNDS':
                return tr('Promo-heavy refunds', 'مرتجعات مرتبطة بالعروض')
            default:
                return tr('Refund signal', 'إشارة استرداد')
        }
    }

    const today = new Date()
    const defaultFrom = new Date(today)
    defaultFrom.setUTCDate(today.getUTCDate() - 29)

    const [filters, setFilters] = useState({
        fromDate: defaultFrom.toISOString().slice(0, 10),
        toDate: today.toISOString().slice(0, 10),
        vendorSearch: '',
        promoCode: '',
    })
    const [groupBy, setGroupBy] = useState<SuperAdminFinancialTrendGroupBy>('Day')
    const [promoTable, setPromoTable] = useState<{
        page: number
        pageSize: number
        sortBy: SuperAdminPromoAnalyticsSortField
        sortDirection: SuperAdminSortDirection
    }>({
        page: 1,
        pageSize: 10,
        sortBy: 'NetImpact',
        sortDirection: 'Desc',
    })
    const [vendorTable, setVendorTable] = useState<{
        page: number
        pageSize: number
        sortBy: SuperAdminVendorPerformanceSortField
        sortDirection: SuperAdminSortDirection
    }>({
        page: 1,
        pageSize: 10,
        sortBy: 'NetContribution',
        sortDirection: 'Desc',
    })

    const deferredVendorSearch = useDeferredValue(filters.vendorSearch.trim())
    const deferredPromoCode = useDeferredValue(filters.promoCode.trim().toUpperCase())
    const scopedFilters = {
        fromDate: filters.fromDate,
        toDate: filters.toDate,
        vendorSearch: deferredVendorSearch || undefined,
        promoCode: deferredPromoCode || undefined,
    }

    const overviewQuery = useQuery({
        queryKey: ['super-admin', 'finance', 'dashboard', 'overview', scopedFilters],
        queryFn: () => getFinancialOverview(scopedFilters),
        staleTime: 30_000,
        refetchInterval: 45_000,
        refetchOnWindowFocus: true,
    })
    const trendsQuery = useQuery({
        queryKey: ['super-admin', 'finance', 'dashboard', 'trends', scopedFilters, groupBy],
        queryFn: () => getFinancialTrends({ ...scopedFilters, groupBy }),
        staleTime: 30_000,
        refetchInterval: 45_000,
        refetchOnWindowFocus: true,
    })
    const promosQuery = useQuery({
        queryKey: ['super-admin', 'finance', 'dashboard', 'promos', scopedFilters, promoTable],
        queryFn: () => getFinancialPromos({ ...scopedFilters, ...promoTable }),
        staleTime: 30_000,
        refetchInterval: 45_000,
        refetchOnWindowFocus: true,
    })
    const vendorsQuery = useQuery({
        queryKey: ['super-admin', 'finance', 'dashboard', 'vendors', scopedFilters, vendorTable],
        queryFn: () => getFinancialVendors({ ...scopedFilters, ...vendorTable }),
        staleTime: 30_000,
        refetchInterval: 45_000,
        refetchOnWindowFocus: true,
    })
    const refundsQuery = useQuery({
        queryKey: ['super-admin', 'finance', 'dashboard', 'refunds', scopedFilters, groupBy],
        queryFn: () => getFinancialRefunds({ ...scopedFilters, groupBy }),
        staleTime: 30_000,
        refetchInterval: 45_000,
        refetchOnWindowFocus: true,
    })
    const integrityQuery = useQuery({
        queryKey: ['super-admin', 'finance', 'dashboard', 'integrity', scopedFilters],
        queryFn: () => getFinancialIntegrityCheck({ ...scopedFilters, maxIssues: 10 }),
        staleTime: 30_000,
        refetchInterval: 45_000,
        refetchOnWindowFocus: true,
    })

    const overviewError = tr('Unable to load the financial overview.', 'تعذر تحميل الملخص المالي.')
    const trendsError = trendsQuery.isError ? tr('Unable to load financial trends.', 'تعذر تحميل الاتجاهات المالية.') : null
    const promosError = promosQuery.isError ? tr('Unable to load promo analytics.', 'تعذر تحميل تحليلات العروض.') : null
    const vendorsError = vendorsQuery.isError ? tr('Unable to load vendor performance.', 'تعذر تحميل أداء البائعين.') : null
    const refundsError = refundsQuery.isError ? tr('Unable to load refund analysis.', 'تعذر تحميل تحليل المرتجعات.') : null
    const integrityError = integrityQuery.isError ? tr('Unable to load the integrity check.', 'تعذر تحميل فحص السلامة.') : null

    async function refreshAll() {
        await Promise.all([
            overviewQuery.refetch(),
            trendsQuery.refetch(),
            promosQuery.refetch(),
            vendorsQuery.refetch(),
            refundsQuery.refetch(),
            integrityQuery.refetch(),
        ])
    }

    function resetFilters() {
        const resetToday = new Date()
        const resetFrom = new Date(resetToday)
        resetFrom.setUTCDate(resetToday.getUTCDate() - 29)

        setFilters({
            fromDate: resetFrom.toISOString().slice(0, 10),
            toDate: resetToday.toISOString().slice(0, 10),
            vendorSearch: '',
            promoCode: '',
        })
        setGroupBy('Day')
        setPromoTable((current) => ({ ...current, page: 1 }))
        setVendorTable((current) => ({ ...current, page: 1 }))
    }

    if (overviewQuery.isError && !overviewQuery.data) {
        return (
            <SuperAdminPage>
                <PageHeader
                    eyebrow={tr('Super Admin · Finance', 'الإدارة العليا · المالية')}
                    title={tr('Finance', 'المالية')}
                    description={tr(
                        'Ledger-backed platform revenue, refunds, and vendor contribution.',
                        'إيرادات المنصة والمرتجعات ومساهمة البائعين المدعومة بدفتر الأستاذ.',
                    )}
                />
                <div className="rounded-2xl border border-rose-200 bg-rose-50 px-5 py-8 text-center">
                    <div className="mx-auto flex h-11 w-11 items-center justify-center rounded-xl bg-white text-rose-600 shadow-sm">
                        <TrendingUp className="h-5 w-5" />
                    </div>
                    <h2 className="mt-3 text-lg font-semibold text-rose-950">
                        {tr('Financial dashboard unavailable', 'لوحة المالية غير متاحة')}
                    </h2>
                    <p className="mx-auto mt-1.5 max-w-xl text-sm leading-6 text-rose-900/80">{overviewError}</p>
                    <button type="button" className={`${saButton.primary} mt-4`} onClick={() => refreshAll()}>
                        <RefreshCw className="h-4 w-4" />
                        {tr('Retry request', 'إعادة المحاولة')}
                    </button>
                </div>
                <VendorFinanceWorkspace />
            </SuperAdminPage>
        )
    }

    const overview = overviewQuery.data
    const refunds = refundsQuery.data
    const integrity = integrityQuery.data
    const isRefreshing =
        overviewQuery.isFetching ||
        trendsQuery.isFetching ||
        promosQuery.isFetching ||
        vendorsQuery.isFetching ||
        refundsQuery.isFetching ||
        integrityQuery.isFetching

    const guide = {
        en: {
            title: 'Finance dashboard guide',
            purpose:
                'Gives a platform-wide, ledger-backed view of commission, promo funding, refunds, reconciliation health, and vendor contribution. It is read-only visibility, not a settlement or accounting system.',
            when:
                'Use it for daily finance review, before payout or refund decisions, when reconciling CommerceHub revenue, or when investigating refund anomalies and integrity warnings.',
            steps: [
                'Set the reporting window and optionally narrow by vendor name or promo code.',
                'Read the KPI cards and trend chart for the selected scope.',
                'Review refund concentration, refund signals, promo performance, and vendor performance.',
                'Check the integrity run and read the data freshness note before relying on aggregates.',
                'Use the vendor finance workspace below for vendor-level balances and holds.',
            ],
            statuses: [
                { label: 'Selected checks passed', description: 'No issues were found by the selected checks. Filtered scans exclude global balance and duplicate-pair checks; this is not full accounting certification.' },
                { label: 'Integrity issues', description: 'Mismatch flags were found; open the reconciliation panel and investigate before acting.' },
                { label: 'CRITICAL / WARNING / INFO', description: 'Refund signal severity. CRITICAL needs immediate review; WARNING should be checked the same day.' },
                { label: 'LIVE_LEDGER', description: 'Values were aggregated from live CommerceHub ledger records, subject to the stated freshness note.' },
            ],
            safety: [
                'All figures are derived; changing filters changes the scope but never recalculates financial formulas in the browser.',
                'Aggregates are eventually consistent. Do not treat them as authoritative for a single order settlement.',
                'This screen performs no money movement. Payout, refund, and payment actions require their own permission-gated screens.',
                'Never hide or dismiss integrity warnings without recording the underlying issue.',
            ],
        },
        ar: {
            title: 'دليل لوحة المالية',
            purpose:
                'يوفر رؤية شاملة للمنصة مدعومة بدفتر الأستاذ للعمولة وتمويل العروض والمرتجعات وسلامة المطابقة ومساهمة البائعين. هذه رؤية للقراءة فقط وليست نظام تسوية أو محاسبة.',
            when:
                'استخدمه في المراجعة المالية اليومية، وقبل قرارات الدفع أو الاسترداد، وعند مطابقة إيرادات CommerceHub، أو عند التحقيق في شذوذ المرتجعات وتحذيرات السلامة.',
            steps: [
                'حدد فترة التقرير وضيّق النطاق اختياريًا باسم البائع أو كود الخصم.',
                'اقرأ بطاقات المؤشرات ومخطط الاتجاه للنطاق المحدد.',
                'راجع تركّز المرتجعات وإشاراتها وأداء العروض وأداء البائعين.',
                'تحقق من تشغيل السلامة واقرأ ملاحظة حداثة البيانات قبل الاعتماد على الإجماليات.',
                'استخدم مساحة مالية البائعين أدناه لأرصدة البائعين والحجوزات.',
            ],
            statuses: [
                { label: 'اجتازت الفحوص المحددة', description: 'لم تجد الفحوص المحددة مشاكل. لا يشمل الفحص المصفّى الأرصدة العامة وأزواج القيود المكررة، وليس شهادة محاسبية شاملة.' },
                { label: 'مشاكل السلامة', description: 'تم رصد علامات عدم تطابق؛ افتح لوحة المطابقة وحقّق قبل اتخاذ أي إجراء.' },
                { label: 'حرج / تحذير / معلومة', description: 'درجات خطورة إشارات المرتجعات. الحالة الحرجة تتطلب مراجعة فورية، والتحذير يُراجع في نفس اليوم.' },
                { label: 'دفتر الأستاذ المباشر', description: 'تم تجميع القيم من سجلات دفتر CommerceHub المباشر مع مراعاة ملاحظة الحداثة المذكورة.' },
            ],
            safety: [
                'كل الأرقام مشتقة؛ تغيير المرشحات يغيّر النطاق ولا يعيد حساب المعادلات المالية في المتصفح.',
                'الإجماليات متسقة نهائيًا بشكل تدريجي. لا تعتبرها مرجعًا نهائيًا لتسوية طلب واحد.',
                'هذه الشاشة لا تنقل أي أموال. إجراءات الدفع والاسترداد تتطلب شاشاتها المصرح بها.',
                'لا تخفِ أو تتجاهل تحذيرات السلامة دون تسجيل المشكلة الأساسية.',
            ],
        },
    }

    return (
        <SuperAdminPage>
            <PageHeader
                eyebrow={tr('Super Admin · Finance', 'الإدارة العليا · المالية')}
                title={tr('Finance', 'المالية')}
                description={tr(
                    'Ledger-backed platform revenue, promo funding, refunds, and vendor contribution.',
                    'إيرادات المنصة وتمويل العروض والمرتجعات ومساهمة البائعين، مدعومة بدفتر الأستاذ.',
                )}
                actions={
                    <button
                        type="button"
                        className={saButton.primary}
                        disabled={isRefreshing}
                        onClick={() => refreshAll()}
                    >
                        <RefreshCw className={`h-4 w-4 ${isRefreshing ? 'animate-spin' : ''}`} />
                        {isRefreshing ? tr('Refreshing', 'جارٍ التحديث') : tr('Refresh', 'تحديث')}
                    </button>
                }
                aside={
                    <div className="flex flex-wrap items-center gap-2">
                        <StatusBadge tone={integrityError ? 'slate' : integrity?.status === 'OK' ? 'emerald' : integrity?.status === 'ISSUES_DETECTED' ? 'rose' : 'slate'}>
                            <ShieldCheck className="h-3.5 w-3.5" />
                            {tr('Integrity', 'السلامة')}: {financeIntegrityLabel(integrityError ? 'UNAVAILABLE' : integrity?.status, tr)}
                        </StatusBadge>
                        <StatusBadge tone={(integrity?.totalIssueCount ?? 0) > 0 ? 'rose' : 'slate'}>
                            {integrity && !integrityError ? formatNumber(integrity.totalIssueCount) : '—'}{' '}
                            {tr('issue flags', 'علامة مشكلة')}
                        </StatusBadge>
                    </div>
                }
            />

            <div className="grid gap-3 sm:grid-cols-2 xl:grid-cols-4">
                <div className="rounded-xl border border-slate-200 bg-white px-3.5 py-3 shadow-sm">
                    <p className="text-[10px] font-semibold uppercase tracking-[0.16em] text-slate-500">
                        {tr('Reporting window (UTC)', 'فترة التقرير (UTC)')}
                    </p>
                    <p className="mt-1 text-sm font-semibold text-slate-900" dir="auto">
                        {formatScopeDate(overview?.fromDateUtc)} → {formatScopeDate(overview?.toDateUtc)}
                    </p>
                </div>
                <div className="rounded-xl border border-slate-200 bg-white px-3.5 py-3 shadow-sm">
                    <p className="text-[10px] font-semibold uppercase tracking-[0.16em] text-slate-500">
                        {tr('Net revenue', 'صافي الإيراد')}
                    </p>
                    <p className="mt-1 text-sm font-semibold text-slate-900" dir="auto">
                        {formatCurrency(overview?.netRevenue, overview?.currency)}
                    </p>
                </div>
                <div className="rounded-xl border border-slate-200 bg-white px-3.5 py-3 shadow-sm">
                    <p className="text-[10px] font-semibold uppercase tracking-[0.16em] text-slate-500">
                        {tr('Refund alerts', 'تنبيهات المرتجعات')}
                    </p>
                    <p className="mt-1 text-sm font-semibold text-slate-900" dir="auto">
                        {refunds && !refundsError ? formatNumber(refunds.alerts.length) : '—'}{' '}
                        {(refunds?.alerts.length ?? 0) === 1
                            ? tr('active signal', 'إشارة نشطة')
                            : tr('active signals', 'إشارات نشطة')}
                    </p>
                </div>
                <div className="rounded-xl border border-slate-200 bg-white px-3.5 py-3 shadow-sm">
                    <p className="text-[10px] font-semibold uppercase tracking-[0.16em] text-slate-500">
                        {tr('Integrity run', 'تشغيل السلامة')}
                    </p>
                    <p className="mt-1 text-sm font-semibold text-slate-900" dir="auto">
                        {formatDateTime(integrity?.executedAtUtc)}
                    </p>
                </div>
            </div>

            <GuidePanel guide={guide} />

            {overviewQuery.isError && overview ? (
                <InlineNotice tone="amber" title={tr('Refresh failed — showing previous results', 'فشل التحديث — تُعرض النتائج السابقة')}>
                    {tr('These values are not a successful refresh of the current financial data. Retry before relying on them.', 'هذه القيم ليست نتيجة تحديث ناجح للبيانات المالية الحالية. أعد المحاولة قبل الاعتماد عليها.')}
                </InlineNotice>
            ) : null}

            {overview && !overviewQuery.isError && overview.totalOrdersCount === 0 &&
                overview.totalRefundAmount === 0 && overview.totalGrossCommission === 0 &&
                overview.totalPromoCost === 0 ? (
                <InlineNotice tone="sky" title={tr('No recognized financial activity in this scope', 'لا يوجد نشاط مالي معترف به في هذا النطاق')}>
                    {tr('This view covers CommerceHub financial recognition, not ordinary POS or Storefront sales. Zero values do not establish that no business orders exist.', 'تغطي هذه الصفحة الاعتراف المالي في CommerceHub، وليس مبيعات نقاط البيع أو واجهة المتجر العادية. القيم الصفرية لا تثبت عدم وجود طلبات تجارية.')}
                </InlineNotice>
            ) : null}

            <InlineNotice tone="amber" title={tr('Financial data freshness', 'حداثة البيانات المالية')}>
                    <p>
                        {tr(
                            'Financial data may be slightly delayed due to processing.',
                            'قد تتأخر البيانات المالية قليلًا بسبب المعالجة.',
                        )}
                    </p>
                    <p className="mt-1">
                        {tr(
                            'Strong consistency applies to a single order flow. Aggregated dashboard metrics are eventually consistent.',
                            'ينطبق الاتساق القوي على تدفق طلب واحد. أما مؤشرات اللوحة المجمعة فهي متسقة نهائيًا بشكل تدريجي.',
                        )}
                    </p>
                <p className="mt-1">{tr('Query time is not proof that all payment, refund, or projection processing has completed.', 'وقت الاستعلام لا يثبت اكتمال معالجة جميع المدفوعات أو المرتجعات أو الإسقاطات.')}</p>
                <p className="mt-1 text-xs opacity-80" dir="auto">
                    {tr('Source', 'المصدر')} {financeSourceLabel(overview?.dataSource, tr)} ·{' '}
                    {tr('Aggregated through', 'تم التجميع حتى')}{' '}
                    {formatDateTime(overview?.aggregatedThroughUtc ?? overview?.generatedAtUtc)}
                </p>
            </InlineNotice>

            <FinanceKpiCards data={overview} isLoading={overviewQuery.isLoading} />

            <FinanceCharts
                overview={overview}
                trends={trendsQuery.data}
                refunds={refunds}
                isTrendsLoading={trendsQuery.isLoading}
                isRefundsLoading={refundsQuery.isLoading}
                trendsError={trendsError}
                refundsError={refundsError}
                fromDate={filters.fromDate}
                toDate={filters.toDate}
                vendorSearch={filters.vendorSearch}
                promoCode={filters.promoCode}
                groupBy={groupBy}
                onFromDateChange={(value) => {
                    setFilters((current) => ({ ...current, fromDate: value }))
                    setPromoTable((current) => ({ ...current, page: 1 }))
                    setVendorTable((current) => ({ ...current, page: 1 }))
                }}
                onToDateChange={(value) => {
                    setFilters((current) => ({ ...current, toDate: value }))
                    setPromoTable((current) => ({ ...current, page: 1 }))
                    setVendorTable((current) => ({ ...current, page: 1 }))
                }}
                onVendorSearchChange={(value) => {
                    setFilters((current) => ({ ...current, vendorSearch: value }))
                    setPromoTable((current) => ({ ...current, page: 1 }))
                    setVendorTable((current) => ({ ...current, page: 1 }))
                }}
                onPromoCodeChange={(value) => {
                    setFilters((current) => ({ ...current, promoCode: value.toUpperCase() }))
                    setPromoTable((current) => ({ ...current, page: 1 }))
                    setVendorTable((current) => ({ ...current, page: 1 }))
                }}
                onGroupByChange={setGroupBy}
                onResetFilters={resetFilters}
            />

            <div className="grid gap-4 xl:grid-cols-[1.4fr_1fr]">
                <SectionCard
                    title={tr('Refund concentration', 'تركّز المرتجعات')}
                    description={tr(
                        'Top vendors and items with the highest refund value inside the same dashboard scope.',
                        'أعلى البائعين والأصناف قيمةً في المرتجعات داخل نفس نطاق اللوحة.',
                    )}
                >
                    {refundsError ? (
                        <p className="rounded-lg border border-rose-200 bg-rose-50 px-4 py-3 text-sm text-rose-800">{refundsError}</p>
                    ) : (
                        <div className="grid gap-5 lg:grid-cols-2">
                            <div>
                                <h3 className="text-xs font-semibold uppercase tracking-[0.16em] text-slate-500">
                                    {tr('Top vendors', 'أعلى البائعين')}
                                </h3>
                                <div className="mt-2.5 space-y-2.5">
                                    {(refunds?.topRefundedVendors ?? []).map((vendor) => (
                                        <div key={vendor.vendorId} className="rounded-xl border border-slate-200 bg-slate-50 px-3.5 py-3">
                                            <p className="font-semibold text-slate-950">{vendor.vendorName}</p>
                                            <p className="mt-1 text-sm text-slate-600">
                                                {formatCurrency(vendor.refundAmount, vendor.currency)}{' '}
                                                {tr('across', 'عبر')} {formatNumber(vendor.refundCount)}{' '}
                                                {tr('refunds', 'مرتجع')}
                                            </p>
                                            <p className="mt-0.5 text-xs text-slate-500">
                                                {tr('Promo reversal', 'عكس العروض')}{' '}
                                                {formatCurrency(vendor.promoReversalAmount, vendor.currency)}
                                            </p>
                                        </div>
                                    ))}
                                    {refunds && refunds.topRefundedVendors.length === 0 ? (
                                        <p className="rounded-xl border border-dashed border-slate-300 bg-slate-50 px-3.5 py-4 text-sm text-slate-500">
                                            {tr('No refunded vendors in this scope.', 'لا يوجد بائعون لديهم مرتجعات في هذا النطاق.')}
                                        </p>
                                    ) : null}
                                </div>
                            </div>

                            <div>
                                <h3 className="text-xs font-semibold uppercase tracking-[0.16em] text-slate-500">
                                    {tr('Top items', 'أعلى الأصناف')}
                                </h3>
                                <div className="mt-2.5 space-y-2.5">
                                    {(refunds?.topRefundedItems ?? []).map((item) => (
                                        <div key={`${item.orderItemId ?? item.subOrderId}-${item.itemLabel}`} className="rounded-xl border border-slate-200 bg-slate-50 px-3.5 py-3">
                                            <p className="font-semibold text-slate-950">{item.itemLabel}</p>
                                            <p className="mt-0.5 text-xs text-slate-500">{item.vendorName}</p>
                                            <p className="mt-1 text-sm text-slate-600">
                                                {formatCurrency(item.refundAmount, item.currency)}{' '}
                                                {tr('across', 'عبر')} {formatNumber(item.refundCount)}{' '}
                                                {tr('refunds', 'مرتجع')}
                                            </p>
                                            <p className="mt-0.5 text-xs text-slate-500">
                                                {tr('Refunded quantity', 'الكمية المرتجعة')} {formatNumber(item.refundedQuantity)}
                                            </p>
                                        </div>
                                    ))}
                                    {refunds && refunds.topRefundedItems.length === 0 ? (
                                        <p className="rounded-xl border border-dashed border-slate-300 bg-slate-50 px-3.5 py-4 text-sm text-slate-500">
                                            {tr('No refunded items in this scope.', 'لا توجد أصناف مرتجعة في هذا النطاق.')}
                                        </p>
                                    ) : null}
                                </div>
                            </div>
                        </div>
                    )}
                </SectionCard>

                <SectionCard
                    title={tr('Refund signals', 'إشارات المرتجعات')}
                    description={tr(
                        'Fast anomaly cues based on refund rate, bucket spikes, and promo-heavy reversals.',
                        'مؤشرات سريعة للشذوذ بناءً على معدل المرتجعات وقفزات الفترات والمرتجعات المرتبطة بالعروض.',
                    )}
                >
                    {refundsError ? (
                        <p className="rounded-lg border border-rose-200 bg-rose-50 px-4 py-3 text-sm text-rose-800">{refundsError}</p>
                    ) : (
                        <div className="space-y-2.5">
                            {(refunds?.alerts ?? []).map((alert) => (
                                <div
                                    key={`${alert.alertType}-${alert.bucketStartUtc ?? 'none'}-${alert.message}`}
                                    className={`rounded-xl border px-3.5 py-3 ${alert.severity === 'CRITICAL' ? 'border-rose-200 bg-rose-50 text-rose-900' : alert.severity === 'WARNING' ? 'border-amber-200 bg-amber-50 text-amber-900' : 'border-sky-200 bg-sky-50 text-sky-900'}`}
                                >
                                    <p className="text-sm font-semibold" dir="auto">{getRefundAlertLabel(alert.alertType)}</p>
                                    <p className="mt-1 text-sm" dir="auto">{tr('Review completed refunds and their linked financial records before acting.', 'راجع المرتجعات المكتملة وسجلاتها المالية المرتبطة قبل اتخاذ إجراء.')}</p>
                                    {alert.bucketStartUtc ? (
                                        <p className="mt-1.5 text-xs opacity-80">{formatDateTime(alert.bucketStartUtc)}</p>
                                    ) : null}
                                </div>
                            ))}
                            {refunds && refunds.alerts.length === 0 ? (
                                <p className="rounded-xl border border-dashed border-emerald-200 bg-emerald-50 px-3.5 py-4 text-sm text-emerald-900">
                                    {tr(
                                        'No refund anomaly signals were generated for the selected scope.',
                                        'لم تُنشأ إشارات شذوذ للمرتجعات في النطاق المحدد.',
                                    )}
                                </p>
                            ) : null}
                        </div>
                    )}
                </SectionCard>
            </div>

            <FinancePerformanceTables
                promos={promosQuery.data}
                vendors={vendorsQuery.data}
                promosLoading={promosQuery.isLoading}
                vendorsLoading={vendorsQuery.isLoading}
                promosError={promosError}
                vendorsError={vendorsError}
                promoSortBy={promoTable.sortBy}
                promoSortDirection={promoTable.sortDirection}
                vendorSortBy={vendorTable.sortBy}
                vendorSortDirection={vendorTable.sortDirection}
                onPromoSortByChange={(value) => setPromoTable((current) => ({ ...current, page: 1, sortBy: value }))}
                onPromoSortDirectionChange={(value) => setPromoTable((current) => ({ ...current, page: 1, sortDirection: value }))}
                onVendorSortByChange={(value) => setVendorTable((current) => ({ ...current, page: 1, sortBy: value }))}
                onVendorSortDirectionChange={(value) => setVendorTable((current) => ({ ...current, page: 1, sortDirection: value }))}
                onPromoPreviousPage={() => setPromoTable((current) => ({ ...current, page: Math.max(1, current.page - 1) }))}
                onPromoNextPage={() => setPromoTable((current) => ({ ...current, page: current.page + 1 }))}
                onVendorPreviousPage={() => setVendorTable((current) => ({ ...current, page: Math.max(1, current.page - 1) }))}
                onVendorNextPage={() => setVendorTable((current) => ({ ...current, page: current.page + 1 }))}
            />

            <FinanceReconciliationPanel
                data={integrity}
                isLoading={integrityQuery.isLoading}
                errorMessage={integrityError}
            />

            <VendorFinanceWorkspace />
        </SuperAdminPage>
    )
}
