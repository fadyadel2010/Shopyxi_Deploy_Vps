import { useState, type FormEvent } from 'react'
import { Building2, FolderTree, Loader2, Radar, ShieldCheck, Sparkles } from 'lucide-react'
import { useSuperAdminLocale } from '../i18n/useSuperAdminLocale'
import { InlineNotice, StatusBadge, saButton, saInput } from '../ui/SuperAdminUI'
import type {
    CommissionCategoryOption,
    CommissionPreviewResult,
    CommissionVendorOption,
} from './commissionService'

interface CommissionPreviewPanelProps {
    categories: CommissionCategoryOption[]
    vendors: CommissionVendorOption[]
    errorMessage?: string | null
    getAppliedRuleLabel: (result: CommissionPreviewResult) => string
    isLoadingLookups: boolean
    isSubmitting: boolean
    result?: CommissionPreviewResult | null
    onPreview: (vendorId?: string | null, categoryId?: string | null) => Promise<void> | void
}

function formatCategoryLabel(category: CommissionCategoryOption): string {
    const prefix = category.level > 0 ? `${'-- '.repeat(category.level)}` : ''
    const emoji = category.iconEmoji ? `${category.iconEmoji} ` : ''
    return `${prefix}${emoji}${category.name}`
}

export default function CommissionPreviewPanel({
    categories,
    vendors,
    errorMessage,
    getAppliedRuleLabel,
    isLoadingLookups,
    isSubmitting,
    result,
    onPreview,
}: CommissionPreviewPanelProps) {
    const [vendorId, setVendorId] = useState<string>('')
    const [categoryId, setCategoryId] = useState<string>('')
    const { tr, formatNumber } = useSuperAdminLocale()

    async function handleSubmit(event: FormEvent<HTMLFormElement>) {
        event.preventDefault()
        await onPreview(vendorId || null, categoryId || null)
    }

    const formatRate = (rate: number) =>
        `${formatNumber(rate * 100, { maximumFractionDigits: 2 })}%`

    const sourceLabel = (source: string) => {
        if (source === 'Vendor') return tr('Vendor', 'بائع')
        if (source === 'Category') return tr('Category', 'فئة')
        if (source === 'Global') return tr('Global', 'عام')
        return source
    }

    return (
        <section className="overflow-hidden rounded-2xl border border-slate-200 bg-white shadow-sm">
            <div className="flex flex-col gap-3 border-b border-slate-100 px-4 py-4 sm:flex-row sm:items-start sm:justify-between sm:px-5">
                <div className="min-w-0">
                    <p className="text-[11px] font-semibold uppercase tracking-[0.18em] text-sky-700">
                        {tr('Resolution preview', 'معاينة الاحتساب')}
                    </p>
                    <h2 className="mt-1.5 text-base font-semibold text-slate-950">
                        {tr('Preview commission before orders are created', 'معاينة العمولة قبل إنشاء الطلبات')}
                    </h2>
                    <p className="mt-1 text-sm leading-6 text-slate-500">
                        {tr(
                            'Use a vendor, a category, or both to see exactly which rule wins and why.',
                            'اختر بائعًا أو فئة أو كليهما لمعرفة القاعدة الفائزة وسبب اختيارها بدقة.',
                        )}
                    </p>
                </div>

                <span className="inline-flex items-center gap-1.5 self-start rounded-full border border-sky-200 bg-sky-50 px-2.5 py-1 text-xs font-semibold text-sky-800 sm:self-auto">
                    <Radar className="h-3.5 w-3.5" />
                    {tr('Read-only preview', 'معاينة للقراءة فقط')}
                </span>
            </div>

            <div className="grid gap-4 px-4 py-4 lg:grid-cols-[1.15fr_1fr] sm:px-5">
                <form className="space-y-3" onSubmit={handleSubmit}>
                    <div className="grid gap-3 sm:grid-cols-2">
                        <label className="space-y-1.5">
                            <span className="text-[10px] font-semibold uppercase tracking-[0.16em] text-slate-500">
                                {tr('Vendor', 'البائع')}
                            </span>
                            <select
                                aria-label={tr('Preview vendor', 'البائع للمعاينة')}
                                value={vendorId}
                                disabled={isLoadingLookups}
                                onChange={(event) => setVendorId(event.target.value)}
                                className={saInput}
                            >
                                <option value="">{tr('Any vendor', 'أي بائع')}</option>
                                {vendors.map((vendor) => (
                                    <option key={vendor.id} value={vendor.id}>
                                        {vendor.name}
                                    </option>
                                ))}
                            </select>
                        </label>

                        <label className="space-y-1.5">
                            <span className="text-[10px] font-semibold uppercase tracking-[0.16em] text-slate-500">
                                {tr('Category', 'الفئة')}
                            </span>
                            <select
                                aria-label={tr('Preview category', 'الفئة للمعاينة')}
                                value={categoryId}
                                disabled={isLoadingLookups}
                                onChange={(event) => setCategoryId(event.target.value)}
                                className={saInput}
                            >
                                <option value="">{tr('Any category', 'أي فئة')}</option>
                                {categories.map((category) => (
                                    <option key={category.id} value={category.id}>
                                        {formatCategoryLabel(category)}
                                    </option>
                                ))}
                            </select>
                        </label>
                    </div>

                    <div className="rounded-xl border border-slate-200 bg-slate-50 px-3.5 py-3">
                        <p className="text-[10px] font-semibold uppercase tracking-[0.16em] text-slate-500">
                            {tr('Precedence reminder', 'تذكير بالأسبقية')}
                        </p>
                        <p className="mt-1.5 text-sm leading-6 text-slate-600">
                            {tr(
                                'Vendor overrides category, then global. Checkout is blocked when no active SQL rule matches or commission authority is ambiguous; there is no legacy fallback for new orders.',
                                'تتقدم قاعدة البائع على الفئة ثم العام. يُحجب إتمام الطلب عندما لا تتطابق قاعدة SQL نشطة أو تكون صلاحية العمولة غامضة؛ ولا يوجد احتياطي قديم للطلبات الجديدة.',
                            )}
                        </p>
                    </div>

                    <button
                        type="submit"
                        className={saButton.primary}
                        disabled={isLoadingLookups || isSubmitting}
                    >
                        {isSubmitting ? (
                            <Loader2 className="h-4 w-4 animate-spin" />
                        ) : (
                            <Sparkles className="h-4 w-4" />
                        )}
                        {isSubmitting
                            ? tr('Previewing…', 'جارٍ المعاينة…')
                            : tr('Preview commission', 'معاينة العمولة')}
                    </button>

                    {errorMessage ? (
                        <InlineNotice tone="rose" title={tr('Preview failed', 'فشلت المعاينة')}>
                            <p dir="auto">{errorMessage}</p>
                            <button
                                type="submit"
                                className={`${saButton.secondary} mt-2 px-2.5 py-1.5 text-xs`}
                            >
                                {tr('Retry preview', 'إعادة المعاينة')}
                            </button>
                        </InlineNotice>
                    ) : null}
                </form>

                <div className="rounded-xl border border-slate-200 bg-slate-50/70 p-3.5">
                    <p className="text-[10px] font-semibold uppercase tracking-[0.16em] text-slate-500">
                        {tr('Preview result', 'نتيجة المعاينة')}
                    </p>

                    {result ? (
                        <div className="mt-3 space-y-3">
                            <div className="rounded-xl border border-emerald-200 bg-emerald-50 p-3.5">
                                <p className="text-[10px] font-semibold uppercase tracking-[0.14em] text-emerald-700">
                                    {tr('Commission rate', 'نسبة العمولة')}
                                </p>
                                <p className="mt-1.5 text-3xl font-semibold text-emerald-950" dir="auto">
                                    {formatRate(result.rate)}
                                </p>
                                <p className="mt-1.5 text-sm font-medium text-emerald-900/85">
                                    {tr(
                                        `Vendor receives ${formatRate(1 - result.rate)} of the product subtotal.`,
                                        `يستلم البائع ${formatRate(1 - result.rate)} من مجموع المنتجات الفرعي.`,
                                    )}
                                </p>
                            </div>

                            <div className="rounded-xl border border-sky-200 bg-sky-50 px-3.5 py-2.5 text-sm font-semibold text-sky-900">
                                {getAppliedRuleLabel(result)}
                            </div>

                            <div className="grid gap-2 sm:grid-cols-2">
                                <div className="rounded-lg border border-slate-200 bg-white px-3 py-2.5 shadow-sm">
                                    <p className="text-[10px] font-semibold uppercase tracking-[0.14em] text-slate-500">
                                        {tr('Source', 'المصدر')}
                                    </p>
                                    <p className="mt-1 flex items-center gap-1.5 text-sm font-semibold text-slate-900">
                                        {result.source === 'Vendor' ? (
                                            <Building2 className="h-4 w-4 text-sky-600" />
                                        ) : null}
                                        {result.source === 'Category' ? (
                                            <FolderTree className="h-4 w-4 text-violet-600" />
                                        ) : null}
                                        {result.source === 'Global' ? (
                                            <ShieldCheck className="h-4 w-4 text-emerald-600" />
                                        ) : null}
                                        {sourceLabel(result.source)}
                                    </p>
                                </div>

                                <div className="rounded-lg border border-slate-200 bg-white px-3 py-2.5 shadow-sm">
                                    <p className="text-[10px] font-semibold uppercase tracking-[0.14em] text-slate-500">
                                        {tr('Priority', 'الأولوية')}
                                    </p>
                                    <p className="mt-1 text-sm font-semibold text-slate-900" dir="auto">
                                        {result.priority ?? tr('N/A', 'غير متاح')}
                                    </p>
                                </div>

                                <div className="rounded-lg border border-slate-200 bg-white px-3 py-2.5 shadow-sm sm:col-span-2">
                                    <p className="text-[10px] font-semibold uppercase tracking-[0.14em] text-slate-500">
                                        {tr('Rule ID', 'معرّف القاعدة')}
                                    </p>
                                    <p className="mt-1 break-all font-mono text-xs text-slate-700" dir="ltr">
                                        {result.ruleId ?? tr('No SQL rule evidence', 'لا يوجد دليل قاعدة SQL')}
                                    </p>
                                </div>
                            </div>

                            <p className="rounded-lg border border-slate-200 bg-white px-3 py-2.5 text-sm leading-6 text-slate-600">
                                {tr(
                                    'Commission resolution applies to the product subtotal only. Shipping is excluded from the commission base.',
                                    'يُطبق احتساب العمولة على مجموع المنتجات الفرعي فقط. الشحن مستبعد من وعاء العمولة.',
                                )}
                            </p>

                            {result.usedLegacyFallback ? (
                                <InlineNotice tone="amber" title={tr('Legacy evidence', 'دليل قديم')}>
                                    {tr(
                                        'This response reports legacy commission evidence. It is not valid SQL commission authority for a new checkout.',
                                        'تشير هذه الاستجابة إلى دليل عمولة قديم. وهو ليس صلاحية عمولة SQL صالحة لإتمام طلب جديد.',
                                    )}
                                </InlineNotice>
                            ) : null}
                        </div>
                    ) : (
                        <div className="mt-3 rounded-xl border border-dashed border-slate-300 bg-white/80 px-4 py-6 text-center">
                            <StatusBadge tone="slate">
                                <Radar className="h-3.5 w-3.5" />
                                {tr('No preview yet', 'لا توجد معاينة بعد')}
                            </StatusBadge>
                            <p className="mt-2 text-sm leading-6 text-slate-500">
                                {tr(
                                    'Select optional vendor and category inputs, then run a preview to inspect the exact resolved rate.',
                                    'اختر بائعًا وفئة اختياريين ثم شغّل المعاينة لفحص النسبة المحتسبة بدقة.',
                                )}
                            </p>
                        </div>
                    )}
                </div>
            </div>
        </section>
    )
}
