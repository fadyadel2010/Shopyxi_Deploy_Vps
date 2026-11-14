import {
    CalendarRange,
    LineChart as LineChartIcon,
    RotateCcw,
    ShieldAlert,
} from 'lucide-react'
import { useLayoutEffect, useRef, useState, type ReactElement } from 'react'
import {
    CartesianGrid,
    Legend,
    Line,
    LineChart,
    ResponsiveContainer,
    Tooltip,
    XAxis,
    YAxis,
} from 'recharts'
import { useSuperAdminLocale } from '../i18n/useSuperAdminLocale'
import { EmptyState, ErrorState, saButton, saInput } from '../ui/SuperAdminUI'
import type {
    SuperAdminFinancialOverview,
    SuperAdminFinancialTrendGroupBy,
    SuperAdminFinancialTrends,
    SuperAdminRefundAnalysis,
} from './financeService'

interface FinanceChartsProps {
    overview?: SuperAdminFinancialOverview
    trends?: SuperAdminFinancialTrends
    refunds?: SuperAdminRefundAnalysis
    isTrendsLoading?: boolean
    isRefundsLoading?: boolean
    trendsError?: string | null
    refundsError?: string | null
    fromDate: string
    toDate: string
    vendorSearch: string
    promoCode: string
    groupBy: SuperAdminFinancialTrendGroupBy
    onFromDateChange: (value: string) => void
    onToDateChange: (value: string) => void
    onVendorSearchChange: (value: string) => void
    onPromoCodeChange: (value: string) => void
    onGroupByChange: (value: SuperAdminFinancialTrendGroupBy) => void
    onResetFilters: () => void
}

const COMMERCEHUB_AUTHORITY_CURRENCY = 'EGP'
const UNSUPPORTED_COMMERCEHUB_CURRENCY = 'CommerceHub currency blocked'

function formatCurrency(value: number, currency: string, locale: string, blockedLabel: string): string {
    if ((currency || '').trim().toUpperCase() !== COMMERCEHUB_AUTHORITY_CURRENCY) {
        return blockedLabel
    }

    return new Intl.NumberFormat(locale, {
        style: 'currency',
        currency: COMMERCEHUB_AUTHORITY_CURRENCY,
        minimumFractionDigits: 2,
        maximumFractionDigits: 2,
    }).format(Number(value ?? 0))
}

function formatCompactCurrency(value: number, currency: string, locale: string, blockedLabel: string): string {
    if ((currency || '').trim().toUpperCase() !== COMMERCEHUB_AUTHORITY_CURRENCY) {
        return blockedLabel
    }

    return new Intl.NumberFormat(locale, {
        style: 'currency',
        currency: COMMERCEHUB_AUTHORITY_CURRENCY,
        notation: 'compact',
        minimumFractionDigits: 0,
        maximumFractionDigits: 1,
    }).format(Number(value ?? 0))
}

function formatDateLabel(value: string | undefined, locale: string, fallback: string): string {
    if (!value) {
        return fallback
    }

    const date = new Date(value)
    if (Number.isNaN(date.getTime())) {
        return fallback
    }

    return date.toLocaleDateString(locale, {
        month: 'short',
        day: 'numeric',
    })
}

function ChartSkeleton() {
    return (
        <div className="h-[320px] rounded-xl border border-slate-200 bg-slate-50 p-4">
            <div className="h-full animate-pulse rounded-lg bg-gradient-to-br from-slate-100 via-white to-slate-100" />
        </div>
    )
}

// Recharts starts at -1/-1 before its first ResizeObserver notification. Do not
// mount a measuring chart until its real container has a positive layout box.
function MeasuredChart({ children }: { children: ReactElement }) {
    const ref = useRef<HTMLDivElement>(null)
    const [size, setSize] = useState({ width: 0, height: 0 })
    useLayoutEffect(() => {
        const node = ref.current
        if (!node) return
        const measure = () => {
            const box = node.getBoundingClientRect()
            setSize({ width: box.width, height: box.height })
        }
        measure()
        const observer = new ResizeObserver(measure)
        observer.observe(node)
        return () => observer.disconnect()
    }, [])
    return <div ref={ref} className="h-full min-w-0">
        {size.width > 0 && size.height > 0 ? <ResponsiveContainer width="100%" height="100%" initialDimension={size}>{children}</ResponsiveContainer> : null}
    </div>
}

function TrendTooltip({
    active,
    payload,
    label,
    currency,
}: {
    active?: boolean
    payload?: Array<{ dataKey?: string; value?: number }>
    label?: string
    currency: string
}) {
    const { tr, locale } = useSuperAdminLocale()
    if (!active || !payload?.length) {
        return null
    }

    const rowByKey = new Map(payload.map((item) => [String(item.dataKey), Number(item.value ?? 0)]))
    const blocked = tr(UNSUPPORTED_COMMERCEHUB_CURRENCY, 'عملة CommerceHub غير مدعومة')

    return (
        <div className="rounded-xl border border-slate-200 bg-white p-3.5 shadow-xl shadow-slate-900/10">
            <p className="text-sm font-semibold text-slate-950">
                {formatDateLabel(label, locale, tr('Unknown', 'غير معروف'))}
            </p>
            <div className="mt-2.5 space-y-1.5 text-sm text-slate-600">
                <div className="flex items-center justify-between gap-4">
                    <span>{tr('Gross Commission', 'إجمالي العمولة')}</span>
                    <span className="font-semibold text-sky-700">
                        {formatCurrency(rowByKey.get('grossCommission') ?? 0, currency, locale, blocked)}
                    </span>
                </div>
                <div className="flex items-center justify-between gap-4">
                    <span>{tr('Promo Cost', 'تكلفة العروض')}</span>
                    <span className="font-semibold text-amber-700">
                        {formatCurrency(rowByKey.get('promoCost') ?? 0, currency, locale, blocked)}
                    </span>
                </div>
                <div className="flex items-center justify-between gap-4">
                    <span>{tr('Net Revenue', 'صافي الإيراد')}</span>
                    <span className="font-semibold text-emerald-700">
                        {formatCurrency(rowByKey.get('netRevenue') ?? 0, currency, locale, blocked)}
                    </span>
                </div>
                <div className="flex items-center justify-between gap-4">
                    <span>{tr('Refund Amount', 'قيمة المرتجعات')}</span>
                    <span className="font-semibold text-rose-700">
                        {formatCurrency(rowByKey.get('refundAmount') ?? 0, currency, locale, blocked)}
                    </span>
                </div>
            </div>
        </div>
    )
}

function FilterPill({
    label,
    value,
}: {
    label: string
    value: string
}) {
    return (
        <div className="rounded-full border border-slate-200 bg-white px-3 py-1.5 text-xs text-slate-600">
            <span className="font-semibold text-slate-900">{label}:</span> {value}
        </div>
    )
}

export default function FinanceCharts({
    overview,
    trends,
    refunds,
    isTrendsLoading,
    isRefundsLoading,
    trendsError,
    refundsError,
    fromDate,
    toDate,
    vendorSearch,
    promoCode,
    groupBy,
    onFromDateChange,
    onToDateChange,
    onVendorSearchChange,
    onPromoCodeChange,
    onGroupByChange,
    onResetFilters,
}: FinanceChartsProps) {
    const { tr, locale } = useSuperAdminLocale()
    const currency = trends?.currency || refunds?.currency || overview?.currency || 'EGP'
    const trendRows = trends?.points ?? []
    const refundRows = refunds?.points ?? []
    const blocked = tr(UNSUPPORTED_COMMERCEHUB_CURRENCY, 'عملة CommerceHub غير مدعومة')

    return (
        <section className="overflow-hidden rounded-2xl border border-slate-200 bg-white shadow-sm">
            <div className="border-b border-slate-100 px-4 py-4 sm:px-5">
                <div className="flex flex-col gap-4 xl:flex-row xl:items-end xl:justify-between">
                    <div className="max-w-2xl">
                        <div className="inline-flex items-center gap-2 rounded-full border border-slate-200 bg-slate-50 px-3 py-1.5 text-xs font-semibold text-slate-700">
                            <CalendarRange className="h-3.5 w-3.5" />
                            {tr('Financial filters', 'المرشحات المالية')}
                        </div>
                        <h2 className="mt-2.5 text-lg font-semibold text-slate-950">
                            {tr(
                                'Revenue, promo funding, and refund drag from the CommerceHub ledger',
                                'الإيراد وتمويل العروض وأثر المرتجعات من دفتر CommerceHub',
                            )}
                        </h2>
                        <p className="mt-1 text-sm leading-6 text-slate-500">
                            {tr(
                                'Overview cards and charts are scoped by financial recognition date, vendor, and promo code without re-implementing checkout or refund math.',
                                'تُحدد نطاقات البطاقات والمخططات بتاريخ الاعتراف المالي والبائع وكود الخصم دون إعادة تنفيذ حسابات الدفع أو الاسترداد.',
                            )}
                        </p>
                    </div>

                    <div className="grid gap-2.5 sm:grid-cols-2 xl:grid-cols-3">
                        <label className="space-y-1.5">
                            <span className="text-[10px] font-semibold uppercase tracking-[0.16em] text-slate-500">
                                {tr('From', 'من')}
                            </span>
                            <input
                                type="date"
                                value={fromDate}
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
                                value={toDate}
                                onChange={(event) => onToDateChange(event.target.value)}
                                className={saInput}
                            />
                        </label>

                        <label className="space-y-1.5">
                            <span className="text-[10px] font-semibold uppercase tracking-[0.16em] text-slate-500">
                                {tr('Bucket', 'التجميع')}
                            </span>
                            <select
                                value={groupBy}
                                onChange={(event) => onGroupByChange(event.target.value as SuperAdminFinancialTrendGroupBy)}
                                className={saInput}
                            >
                                <option value="Day">{tr('Daily', 'يومي')}</option>
                                <option value="Month">{tr('Monthly', 'شهري')}</option>
                            </select>
                        </label>

                        <label className="space-y-1.5 sm:col-span-2 xl:col-span-2">
                            <span className="text-[10px] font-semibold uppercase tracking-[0.16em] text-slate-500">
                                {tr('Vendor filter', 'مرشح البائع')}
                            </span>
                            <input
                                type="text"
                                value={vendorSearch}
                                onChange={(event) => onVendorSearchChange(event.target.value)}
                                placeholder={tr('Search vendor name, slug, email, or id', 'ابحث باسم البائع أو المعرّف أو البريد أو الرقم')}
                                className={saInput}
                            />
                        </label>

                        <label className="space-y-1.5">
                            <span className="text-[10px] font-semibold uppercase tracking-[0.16em] text-slate-500">
                                {tr('Promo code', 'كود الخصم')}
                            </span>
                            <input
                                type="text"
                                value={promoCode}
                                onChange={(event) => onPromoCodeChange(event.target.value)}
                                placeholder="PROMO2026"
                                className={`${saInput} uppercase`}
                            />
                        </label>

                        <div className="flex items-end">
                            <button type="button" className={`${saButton.secondary} w-full`} onClick={onResetFilters}>
                                <RotateCcw className="h-4 w-4" />
                                {tr('Reset', 'إعادة ضبط')}
                            </button>
                        </div>
                    </div>
                </div>
            </div>

            <div className="flex flex-wrap gap-2 border-b border-slate-100 bg-slate-50/70 px-4 py-3 sm:px-5">
                <FilterPill label={tr('Scope', 'النطاق')} value={overview?.orderScope || 'SubOrder'} />
                <FilterPill
                    label={tr('Date', 'التاريخ')}
                    value={`${fromDate || tr('Open', 'مفتوح')} → ${toDate || tr('Open', 'مفتوح')}`}
                />
                <FilterPill label={tr('Vendor', 'البائع')} value={vendorSearch.trim() || tr('All vendors', 'كل البائعين')} />
                <FilterPill label={tr('Promo', 'العرض')} value={promoCode.trim().toUpperCase() || tr('All promos', 'كل العروض')} />
            </div>

            <div className="grid gap-4 px-4 py-4 sm:px-5 xl:grid-cols-2">
                <div className="rounded-xl border border-slate-200 bg-white p-4 shadow-sm">
                    <div className="flex items-center gap-2 text-sm font-semibold text-slate-900">
                        <LineChartIcon className="h-4 w-4" />
                        {tr('Revenue vs promo', 'الإيراد مقابل العروض')}
                    </div>
                    <p className="mt-1 text-sm text-slate-500">
                        {tr(
                            'Gross commission, promo cost, and net revenue over time.',
                            'إجمالي العمولة وتكلفة العروض وصافي الإيراد بمرور الوقت.',
                        )}
                    </p>

                    {isTrendsLoading && !trends ? (
                        <div className="mt-3">
                            <ChartSkeleton />
                        </div>
                    ) : trendsError ? (
                        <div className="mt-3">
                            <ErrorState title={tr('Trends unavailable', 'المخططات غير متاحة')} description={trendsError} />
                        </div>
                    ) : trendRows.length === 0 ? (
                        <div className="mt-3">
                            <EmptyState
                                title={tr('No trend rows', 'لا توجد صفوف اتجاه')}
                                description={tr(
                                    'No financial trend rows matched the selected scope.',
                                    'لم تُطابق أي صفوف اتجاه مالي النطاق المحدد.',
                                )}
                            />
                        </div>
                    ) : (
                        <div className="mt-3 h-[280px]" dir="ltr">
                            <MeasuredChart>
                                <LineChart data={trendRows} margin={{ top: 12, right: 16, left: 0, bottom: 0 }}>
                                    <CartesianGrid strokeDasharray="3 3" stroke="#e2e8f0" />
                                    <XAxis
                                        dataKey="bucketStartUtc"
                                        tickFormatter={(value) => formatDateLabel(value, locale, tr('Unknown', 'غير معروف'))}
                                        stroke="#64748b"
                                        tickLine={false}
                                        axisLine={false}
                                    />
                                    <YAxis
                                        stroke="#64748b"
                                        tickLine={false}
                                        axisLine={false}
                                        tickFormatter={(value) => formatCompactCurrency(value, currency, locale, blocked)}
                                    />
                                    <Tooltip content={<TrendTooltip currency={currency} />} />
                                    <Legend />
                                    <Line type="monotone" dataKey="grossCommission" name={tr('Gross Commission', 'إجمالي العمولة')} stroke="#0284c7" strokeWidth={3} dot={false} />
                                    <Line type="monotone" dataKey="promoCost" name={tr('Promo Cost', 'تكلفة العروض')} stroke="#d97706" strokeWidth={3} dot={false} />
                                    <Line type="monotone" dataKey="netRevenue" name={tr('Net Revenue', 'صافي الإيراد')} stroke="#059669" strokeWidth={3} dot={false} />
                                </LineChart>
                            </MeasuredChart>
                        </div>
                    )}
                </div>

                <div className="rounded-xl border border-slate-200 bg-white p-4 shadow-sm">
                    <div className="flex items-center gap-2 text-sm font-semibold text-slate-900">
                        <ShieldAlert className="h-4 w-4" />
                        {tr('Refund trend', 'اتجاه المرتجعات')}
                    </div>
                    <p className="mt-1 text-sm text-slate-500">
                        {tr(
                            'Refund value and promo reversals across the same reporting window.',
                            'قيمة المرتجعات وعكس العروض خلال نفس فترة التقرير.',
                        )}
                    </p>

                    {isRefundsLoading && !refunds ? (
                        <div className="mt-3">
                            <ChartSkeleton />
                        </div>
                    ) : refundsError ? (
                        <div className="mt-3">
                            <ErrorState title={tr('Refund trend unavailable', 'اتجاه المرتجعات غير متاح')} description={refundsError} />
                        </div>
                    ) : refundRows.length === 0 ? (
                        <div className="mt-3">
                            <EmptyState
                                title={tr('No refunds', 'لا توجد مرتجعات')}
                                description={tr(
                                    'No refunds were completed in the selected scope.',
                                    'لم تُكتمل أي مرتجعات في النطاق المحدد.',
                                )}
                            />
                        </div>
                    ) : (
                        <div className="mt-3 h-[280px]" dir="ltr">
                            <MeasuredChart>
                                <LineChart data={refundRows} margin={{ top: 12, right: 16, left: 0, bottom: 0 }}>
                                    <CartesianGrid strokeDasharray="3 3" stroke="#e2e8f0" />
                                    <XAxis
                                        dataKey="bucketStartUtc"
                                        tickFormatter={(value) => formatDateLabel(value, locale, tr('Unknown', 'غير معروف'))}
                                        stroke="#64748b"
                                        tickLine={false}
                                        axisLine={false}
                                    />
                                    <YAxis
                                        stroke="#64748b"
                                        tickLine={false}
                                        axisLine={false}
                                        tickFormatter={(value) => formatCompactCurrency(value, currency, locale, blocked)}
                                    />
                                    <Legend />
                                    <Line type="monotone" dataKey="refundAmount" name={tr('Refund Amount', 'قيمة المرتجعات')} stroke="#dc2626" strokeWidth={3} dot={false} />
                                    <Line type="monotone" dataKey="promoReversalAmount" name={tr('Promo Reversal', 'عكس العروض')} stroke="#7c3aed" strokeWidth={3} dot={false} />
                                </LineChart>
                            </MeasuredChart>
                        </div>
                    )}
                </div>
            </div>
        </section>
    )
}
