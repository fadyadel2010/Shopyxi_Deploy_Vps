import { useEffect, useMemo, useState } from 'react'
import { useQuery, useQueryClient } from '@tanstack/react-query'
import { AlertTriangle, EyeOff, RefreshCw, RotateCcw, Search } from 'lucide-react'
import { useDebounce } from '../../hooks/useDebounce'
import { useSuperAdminLocale } from '../i18n/useSuperAdminLocale'
import {
    GuidePanel,
    InlineNotice,
    PageHeader,
    SectionCard,
    StatusBadge,
    SuperAdminPage,
    saButton,
    saInput,
    saTable,
} from '../ui/SuperAdminUI'
import {
    getMarketplaceGovernanceDashboard,
    getMarketplaceGovernanceListings,
    restoreMarketplaceListing,
    restoreMarketplaceVendor,
    suppressMarketplaceListing,
    suspendMarketplaceVendor,
    type GovernanceListingStatus,
    type MarketplaceGovernanceListingFilters,
} from './marketplaceGovernanceService'

const DEFAULT_FILTERS: MarketplaceGovernanceListingFilters = {
    page: 1,
    pageSize: 50,
    q: '',
    status: '',
    vendorStatus: '',
    tenantStatus: '',
    overrideEnabled: '',
}

type Tone = 'slate' | 'emerald' | 'amber' | 'rose'

function badgeTone(value: string): Tone {
    if (value === 'suppressed' || value === 'Suspended') return 'amber'
    if (value === 'orphaned' || value === 'Banned' || value === 'Deleted') return 'rose'
    if (value === 'published' || value === 'Active') return 'emerald'
    return 'slate'
}

function getErrorMessage(error: unknown, fallback: string): string {
    return error instanceof Error ? error.message : fallback
}

export default function MarketplaceGovernancePage() {
    const queryClient = useQueryClient()
    const { tr, formatDateTime, formatNumber } = useSuperAdminLocale()
    const [filters, setFilters] = useState(DEFAULT_FILTERS)
    const [selectedListingId, setSelectedListingId] = useState<string | null>(null)
    const [isActionRunning, setIsActionRunning] = useState(false)
    const [actionError, setActionError] = useState<string | null>(null)
    const [actionMessage, setActionMessage] = useState<string | null>(null)
    const debouncedSearch = useDebounce(filters.q ?? '', 350)

    const effectiveFilters = useMemo(
        () => ({
            ...filters,
            q: debouncedSearch,
        }),
        [debouncedSearch, filters],
    )

    const dashboardQuery = useQuery({
        queryKey: ['super-admin', 'marketplace-governance', 'dashboard'],
        queryFn: getMarketplaceGovernanceDashboard,
    })

    const listingsQuery = useQuery({
        queryKey: ['super-admin', 'marketplace-governance', 'listings', effectiveFilters],
        queryFn: () => getMarketplaceGovernanceListings(effectiveFilters),
    })

    const listings = listingsQuery.isError ? [] : listingsQuery.data?.listings ?? []
    const selectedListing = listings.find((listing) => listing.listingId === selectedListingId) ?? null
    const totalPages = listingsQuery.data?.meta?.totalPages ?? 0
    const totalCount = listingsQuery.data?.meta?.totalCount ?? 0

    useEffect(() => {
        if (selectedListingId && !selectedListing && !listingsQuery.isFetching) {
            setSelectedListingId(null)
        }
    }, [listingsQuery.isFetching, selectedListing, selectedListingId])

    function updateFilter<K extends keyof MarketplaceGovernanceListingFilters>(
        key: K,
        value: MarketplaceGovernanceListingFilters[K],
    ) {
        setFilters((current) => ({
            ...current,
            [key]: value,
            page: key === 'page' || key === 'pageSize' ? (value as number) : 1,
        }))
    }

    async function refreshAll() {
        await Promise.all([dashboardQuery.refetch(), listingsQuery.refetch()])
    }

    async function runListingAction(action: 'suppress' | 'restore') {
        if (!selectedListing) return

        const reason = window.prompt(
            action === 'suppress'
                ? tr('Suppress listing reason', 'سبب إخفاء القائمة')
                : tr('Restore listing reason', 'سبب استعادة القائمة'),
        )?.trim()
        if (!reason || reason.length < 3) return

        setActionError(null)
        setActionMessage(null)
        setIsActionRunning(true)
        try {
            const result = action === 'suppress'
                ? await suppressMarketplaceListing(selectedListing.listingId, reason)
                : await restoreMarketplaceListing(selectedListing.listingId, reason)

            setActionMessage(result.message)
            await queryClient.invalidateQueries({ queryKey: ['super-admin', 'marketplace-governance'] })
        } catch (error) {
            setActionError(getErrorMessage(error, tr('Marketplace governance action failed.', 'فشل إجراء حوكمة السوق.')))
        } finally {
            setIsActionRunning(false)
        }
    }

    async function runVendorAction(action: 'suspend' | 'restore') {
        if (!selectedListing) return

        const reason = window.prompt(
            action === 'suspend'
                ? tr('Suspend vendor marketplace participation reason', 'سبب تعليق مشاركة البائع في السوق')
                : tr('Restore vendor marketplace participation reason', 'سبب استعادة مشاركة البائع في السوق'),
        )?.trim()
        if (!reason || reason.length < 3) return

        setActionError(null)
        setActionMessage(null)
        setIsActionRunning(true)
        try {
            const result = action === 'suspend'
                ? await suspendMarketplaceVendor(selectedListing.tenantId, reason)
                : await restoreMarketplaceVendor(selectedListing.tenantId, reason)

            setActionMessage(result.message)
            await queryClient.invalidateQueries({ queryKey: ['super-admin', 'marketplace-governance'] })
        } catch (error) {
            setActionError(getErrorMessage(error, tr('Vendor governance action failed.', 'فشل إجراء حوكمة البائع.')))
        } finally {
            setIsActionRunning(false)
        }
    }

    const dashboard = dashboardQuery.isFetching || dashboardQuery.isError ? undefined : dashboardQuery.data
    const actionDisabled = listingsQuery.isFetching || listingsQuery.isError || isActionRunning

    const visibilityLabels: Record<string, string> = {
        published: tr('Published', 'منشور'),
        unpublished: tr('Unpublished', 'غير منشور'),
        suppressed: tr('Suppressed', 'مخفي'),
        orphaned: tr('Orphaned', 'يتيم'),
        vendor_suspended: tr('Vendor suspended', 'البائع معلّق'),
        tenant_suspended: tr('Tenant suspended', 'المتجر معلّق'),
    }

    const statusLabel = (value: string) => {
        const labels: Record<string, string> = {
            Active: tr('Active', 'نشط'),
            Suspended: tr('Suspended', 'معلّق'),
            Banned: tr('Banned', 'محظور'),
            Deleted: tr('Deleted', 'محذوف'),
            FinanciallyLocked: tr('Financially locked', 'مقفل ماليًا'),
        }
        return labels[value] ?? value
    }

    const guide = {
        en: {
            title: 'Marketplace governance guide',
            purpose:
                'Controls effective marketplace visibility at the SQL authority layer. It suppresses individual listings or suspends vendor participation, and shows why a listing is visible or hidden.',
            when:
                'Use it for policy violations, counterfeit or unsafe listings, vendor enforcement, orphaned listings, or when investigating why a listing is missing from the marketplace.',
            steps: [
                'Filter by visibility, vendor status, tenant status, price override, or free text.',
                'Compare publication flags with effective visibility; the two are not the same.',
                'Select a listing to inspect its tenant, vendor, suppression owner, and orphan state.',
                'Run suppress/restore for the listing or suspend/restore for the vendor, with a reason.',
                'Refresh and confirm the new visibility state and audit entry.',
            ],
            statuses: [
                { label: 'Published', description: 'The publication flag is set, but tenant/vendor restrictions may still hide it.' },
                { label: 'Suppressed', description: 'Explicitly hidden by governance; the suppression reason is preserved.' },
                { label: 'Orphaned', description: 'The listing references missing product or tenant data and cannot be sold safely.' },
                { label: 'Vendor / tenant suspended', description: 'Visibility is removed because the vendor or tenant is not active.' },
                { label: 'Override present', description: 'A Super Admin price override exists; this does not affect visibility by itself.' },
            ],
            safety: [
                'Suppression and vendor suspension are audited and require a reason.',
                'These actions change what customers can see and buy. Verify the correct listing and tenant first.',
                'Do not suppress to hide a data integrity problem; fix the orphan or mapping instead.',
                'Restoring a vendor does not restore listings that were explicitly suppressed individually.',
            ],
        },
        ar: {
            title: 'دليل حوكمة السوق',
            purpose:
                'يتحكم في الظهور الفعلي في السوق على طبقة سلطة SQL. يخفي قوائم محددة أو يعلّق مشاركة بائع، ويوضح سبب ظهور القائمة أو إخفائها.',
            when:
                'استخدمه عند مخالفات السياسات أو القوائم المقلدة أو غير الآمنة أو إنفاذ بحق بائع أو القوائم اليتيمة أو التحقيق في اختفاء قائمة من السوق.',
            steps: [
                'رشّح حسب الظهور أو حالة البائع أو حالة المتجر أو تعديل السعر أو نص حر.',
                'قارن علامات النشر بالظهور الفعلي؛ فهما ليسا الشيء نفسه.',
                'اختر قائمة لفحص المتجر والبائع ومالك الإخفاء وحالة اليتم.',
                'نفّذ إخفاء/استعادة القائمة أو تعليق/استعادة البائع مع ذكر السبب.',
                'حدّث الصفحة وتأكد من حالة الظهور الجديدة وقيد التدقيق.',
            ],
            statuses: [
                { label: 'منشور', description: 'علامة النشر مفعّلة، لكن قيود المتجر أو البائع قد تُخفيه.' },
                { label: 'مخفي', description: 'مخفي صراحةً بواسطة الحوكمة مع الاحتفاظ بسبب الإخفاء.' },
                { label: 'يتيم', description: 'تشير القائمة إلى منتج أو متجر مفقود ولا يمكن بيعها بأمان.' },
                { label: 'البائع / المتجر معلّق', description: 'أُزيل الظهور لأن البائع أو المتجر غير نشط.' },
                { label: 'يوجد تعديل سعر', description: 'يوجد تعديل سعر من الإدارة العليا؛ لا يؤثر على الظهور بحد ذاته.' },
            ],
            safety: [
                'الإخفاء وتعليق البائع مدققان ويتطلبان سببًا.',
                'هذه الإجراءات تغيّر ما يراه العملاء ويشترونه. تحقق من القائمة والمتجر الصحيحين أولًا.',
                'لا تستخدم الإخفاء لإخفاء مشكلة سلامة بيانات؛ أصلح اليتم أو الربط بدلًا من ذلك.',
                'استعادة البائع لا تستعيد القوائم التي أُخفيت فرديًا بشكل صريح.',
            ],
        },
    }

    return (
        <SuperAdminPage>
            <PageHeader
                eyebrow={tr('Super Admin · Marketplace', 'الإدارة العليا · السوق')}
                title={tr('Marketplace governance', 'حوكمة السوق')}
                description={tr(
                    'SQL-authority controls for listing suppression, vendor suspension, visibility filters, and audited actions.',
                    'أدوات سلطة SQL لإخفاء القوائم وتعليق البائعين ومرشحات الظهور والإجراءات المدققة.',
                )}
                actions={
                    <button type="button" className={saButton.primary} onClick={() => refreshAll()}>
                        <RefreshCw className={`h-4 w-4 ${dashboardQuery.isFetching || listingsQuery.isFetching ? 'animate-spin' : ''}`} />
                        {tr('Refresh', 'تحديث')}
                    </button>
                }
            />

            <div className="grid gap-2 sm:grid-cols-2 xl:grid-cols-5">
                {[
                    { label: tr('Listings', 'القوائم'), value: dashboard?.totalListings },
                    { label: tr('Publication flag set', 'علامة النشر مفعّلة'), value: dashboard?.publishedListings, tone: 'emerald' as const },
                    { label: tr('Explicit suppression', 'إخفاء صريح'), value: dashboard?.suppressedListings, tone: 'amber' as const },
                    { label: tr('Orphaned', 'يتيمة'), value: dashboard?.orphanedListings, tone: 'rose' as const },
                    { label: tr('Audit events', 'أحداث التدقيق'), value: dashboard?.recentGovernanceAuditCount },
                    { label: tr('Active vendors', 'بائعون نشطون'), value: dashboard?.activeVendors },
                    { label: tr('Suspended vendors', 'بائعون معلّقون'), value: dashboard?.suspendedVendors, tone: 'amber' as const },
                    { label: tr('Banned vendors', 'بائعون محظورون'), value: dashboard?.bannedVendors, tone: 'rose' as const },
                    { label: tr('Active tenants', 'متاجر نشطة'), value: dashboard?.activeTenants },
                    { label: tr('Suspended tenants', 'متاجر معلّقة'), value: dashboard?.suspendedTenants, tone: 'amber' as const },
                ].map((metric) => (
                    <div
                        key={metric.label}
                        className={`rounded-xl border px-3.5 py-2.5 shadow-sm ${
                            metric.tone === 'emerald'
                                ? 'border-emerald-200 bg-emerald-50 text-emerald-900'
                                : metric.tone === 'amber'
                                  ? 'border-amber-200 bg-amber-50 text-amber-900'
                                  : metric.tone === 'rose'
                                    ? 'border-rose-200 bg-rose-50 text-rose-900'
                                    : 'border-slate-200 bg-white text-slate-950'
                        }`}
                    >
                        <p className="text-[10px] font-semibold uppercase tracking-[0.12em] opacity-70">{metric.label}</p>
                        <p className="mt-1 text-lg font-semibold" dir="auto">
                            {metric.value == null ? tr('Unavailable', 'غير متاح') : formatNumber(metric.value)}
                        </p>
                    </div>
                ))}
            </div>

            <GuidePanel guide={guide} />

            {dashboardQuery.isError || listingsQuery.isError ? (
                <InlineNotice tone="rose" title={tr('Governance data could not be verified', 'تعذّر التحقق من بيانات الحوكمة')}>
                    {getErrorMessage(listingsQuery.error ?? dashboardQuery.error, tr('Refresh to retry.', 'حدّث لإعادة المحاولة.'))}{' '}
                    {tr('This is not an empty listing result.', 'هذه ليست نتيجة قوائم فارغة.')}
                </InlineNotice>
            ) : null}

            <InlineNotice tone="sky" title={tr('Publication flag vs effective visibility', 'علامة النشر مقابل الظهور الفعلي')}>
                {tr(
                    'Publication flags are not public visibility counts. Effective visibility also includes tenant and vendor restrictions; use the Suppressed filter to inspect all effectively suppressed listings.',
                    'علامات النشر ليست أعداد ظهور عامة. يشمل الظهور الفعلي أيضًا قيود المتجر والبائع؛ استخدم مرشح "مخفي" لفحص كل القوائم المخفية فعليًا.',
                )}
            </InlineNotice>

            {actionError ? <InlineNotice tone="rose" title={tr('Action failed', 'فشل الإجراء')}>{actionError}</InlineNotice> : null}
            {actionMessage ? <InlineNotice tone="emerald" title={tr('Action result', 'نتيجة الإجراء')}>{actionMessage}</InlineNotice> : null}

            <section className="rounded-xl border border-slate-200 bg-white p-3 shadow-sm">
                <div className="grid gap-2.5 xl:grid-cols-6">
                    <div className="relative xl:col-span-2">
                        <Search className="pointer-events-none absolute start-3.5 top-1/2 h-4 w-4 -translate-y-1/2 text-slate-400" />
                        <input
                            value={filters.q}
                            onChange={(event) => updateFilter('q', event.target.value)}
                            placeholder={tr('Search listing, tenant, SKU, category', 'ابحث في القائمة أو المتجر أو SKU أو الفئة')}
                            className={`${saInput} ps-10`}
                        />
                    </div>
                    <select aria-label={tr('Effective listing visibility', 'الظهور الفعلي للقائمة')} value={filters.status} onChange={(event) => updateFilter('status', event.target.value as GovernanceListingStatus)} className={saInput}>
                        <option value="">{tr('All visibility', 'كل حالات الظهور')}</option>
                        <option value="published">{tr('Published', 'منشور')}</option>
                        <option value="unpublished">{tr('Unpublished', 'غير منشور')}</option>
                        <option value="suppressed">{tr('Suppressed', 'مخفي')}</option>
                        <option value="orphaned">{tr('Orphaned', 'يتيم')}</option>
                        <option value="vendor_suspended">{tr('Vendor suspended', 'البائع معلّق')}</option>
                        <option value="tenant_suspended">{tr('Tenant suspended', 'المتجر معلّق')}</option>
                    </select>
                    <select aria-label={tr('Vendor governance status', 'حالة حوكمة البائع')} value={filters.vendorStatus} onChange={(event) => updateFilter('vendorStatus', event.target.value as MarketplaceGovernanceListingFilters['vendorStatus'])} className={saInput}>
                        <option value="">{tr('All vendors', 'كل البائعين')}</option>
                        <option value="Active">{tr('Vendor active', 'البائع نشط')}</option>
                        <option value="Suspended">{tr('Vendor suspended', 'البائع معلّق')}</option>
                        <option value="Banned">{tr('Vendor banned', 'البائع محظور')}</option>
                        <option value="FinanciallyLocked">{tr('Vendor financial lock', 'قفل مالي للبائع')}</option>
                    </select>
                    <select aria-label={tr('Tenant status', 'حالة المتجر')} value={filters.tenantStatus} onChange={(event) => updateFilter('tenantStatus', event.target.value as MarketplaceGovernanceListingFilters['tenantStatus'])} className={saInput}>
                        <option value="">{tr('All tenants', 'كل المتاجر')}</option>
                        <option value="Active">{tr('Tenant active', 'المتجر نشط')}</option>
                        <option value="Suspended">{tr('Tenant suspended', 'المتجر معلّق')}</option>
                        <option value="Deleted">{tr('Tenant deleted', 'المتجر محذوف')}</option>
                    </select>
                    <select aria-label={tr('Listing price override', 'تعديل سعر القائمة')} value={filters.overrideEnabled} onChange={(event) => updateFilter('overrideEnabled', event.target.value as MarketplaceGovernanceListingFilters['overrideEnabled'])} className={saInput}>
                        <option value="">{tr('All prices', 'كل الأسعار')}</option>
                        <option value="true">{tr('Override present', 'يوجد تعديل')}</option>
                        <option value="false">{tr('No override', 'لا يوجد تعديل')}</option>
                    </select>
                </div>
            </section>

            <div className="grid gap-4 xl:grid-cols-[minmax(0,1.35fr)_minmax(340px,0.8fr)]">
                <section className="overflow-hidden rounded-xl border border-slate-200 bg-white shadow-sm">
                    <div className="overflow-x-auto">
                        <table className="min-w-full divide-y divide-slate-200">
                            <thead className={saTable.head}>
                                <tr>
                                    <th className={saTable.headerCell}>{tr('Listing', 'القائمة')}</th>
                                    <th className={saTable.headerCell}>{tr('Tenant', 'المتجر')}</th>
                                    <th className={saTable.headerCell}>{tr('Visibility', 'الظهور')}</th>
                                    <th className={saTable.headerCell}>{tr('Vendor', 'البائع')}</th>
                                    <th className={saTable.headerCell}>{tr('Category', 'الفئة')}</th>
                                    <th className={saTable.headerCell}>{tr('Updated', 'آخر تحديث')}</th>
                                </tr>
                            </thead>
                            <tbody className="divide-y divide-slate-100 text-sm text-slate-700">
                                {listings.map((listing) => (
                                    <tr
                                        key={listing.listingId}
                                        className={`cursor-pointer hover:bg-slate-50 ${selectedListingId === listing.listingId ? 'bg-emerald-50/70' : ''}`}
                                        onClick={() => setSelectedListingId(listing.listingId)}
                                    >
                                        <td className={saTable.cell}>
                                            <p className="font-semibold text-slate-950">{listing.productName}</p>
                                            <p className="mt-0.5 text-xs text-slate-500">{listing.sku ?? listing.variantName ?? listing.variantId}</p>
                                        </td>
                                        <td className={saTable.cell}>
                                            <p className="font-medium text-slate-900">{listing.tenantName}</p>
                                            <span className="mt-1.5 inline-block">
                                                <StatusBadge tone={badgeTone(listing.tenantStatus)}>{statusLabel(listing.tenantStatus)}</StatusBadge>
                                            </span>
                                        </td>
                                        <td className={saTable.cell}>
                                            <StatusBadge tone={badgeTone(listing.visibilityState)}>
                                                {visibilityLabels[listing.visibilityState] ?? listing.visibilityState}
                                            </StatusBadge>
                                            {listing.isOrphaned ? <p className="mt-1.5 text-xs text-rose-700">{listing.orphanReason}</p> : null}
                                        </td>
                                        <td className={saTable.cell}>
                                            <StatusBadge tone={badgeTone(listing.vendorGovernanceStatus)}>
                                                {statusLabel(listing.vendorGovernanceStatus)}
                                            </StatusBadge>
                                        </td>
                                        <td className={saTable.cell}>{listing.categoryName ?? listing.categoryId}</td>
                                        <td className={saTable.cell}>{formatDateTime(listing.updatedAtUtc ?? listing.createdAtUtc, tr('Not recorded', 'غير مسجل'))}</td>
                                    </tr>
                                ))}
                            </tbody>
                        </table>
                    </div>
                    <div className="flex flex-wrap items-center justify-between gap-2 border-t border-slate-100 px-4 py-3 text-sm text-slate-600">
                        <p>
                            {listingsQuery.isError
                                ? tr('Listing count unavailable', 'عدد القوائم غير متاح')
                                : tr(
                                      `${formatNumber(totalCount)} listings, page ${filters.page} of ${totalPages || 1}`,
                                      `${formatNumber(totalCount)} قائمة، صفحة ${filters.page} من ${totalPages || 1}`,
                                  )}
                        </p>
                        <div className="flex items-center gap-2">
                            <button type="button" disabled={filters.page <= 1} className={saButton.secondary} onClick={() => updateFilter('page', filters.page - 1)}>
                                {tr('Previous', 'السابق')}
                            </button>
                            <button type="button" disabled={!totalPages || filters.page >= totalPages} className={saButton.primary} onClick={() => updateFilter('page', filters.page + 1)}>
                                {tr('Next', 'التالي')}
                            </button>
                        </div>
                    </div>
                </section>

                <SectionCard
                    className="self-start"
                    title={tr('Listing details', 'تفاصيل القائمة')}
                    description={tr(
                        'Inspect SQL authority, suppression state, and governance actions.',
                        'افحص سلطة SQL وحالة الإخفاء وإجراءات الحوكمة.',
                    )}
                >
                    {!selectedListing ? (
                        <div className="grid min-h-[220px] place-items-center rounded-xl border border-dashed border-slate-300 bg-slate-50/70 px-4 text-center">
                            <div>
                                <AlertTriangle className="mx-auto h-7 w-7 text-slate-400" />
                                <p className="mt-2 text-sm font-semibold text-slate-800">{tr('Select a listing', 'اختر قائمة')}</p>
                                <p className="mx-auto mt-1 max-w-xs text-sm leading-6 text-slate-500">
                                    {tr(
                                        'Select a listing to inspect SQL authority, suppression state, and governance actions.',
                                        'اختر قائمة لفحص سلطة SQL وحالة الإخفاء وإجراءات الحوكمة.',
                                    )}
                                </p>
                            </div>
                        </div>
                    ) : (
                        <div className="space-y-3">
                            <div className="rounded-xl border border-slate-200 bg-slate-50 p-3.5">
                                <StatusBadge tone={badgeTone(selectedListing.visibilityState)}>
                                    {visibilityLabels[selectedListing.visibilityState] ?? selectedListing.visibilityState}
                                </StatusBadge>
                                <h3 className="mt-2 text-lg font-semibold text-slate-950">{selectedListing.productName}</h3>
                                <p className="mt-1 text-sm text-slate-600">{selectedListing.tenantName} · {selectedListing.sku ?? selectedListing.variantId}</p>
                            </div>

                            {selectedListing.isSuppressed ? (
                                <InlineNotice tone="amber" title={tr('Listing suppressed', 'القائمة مخفية')}>
                                    {selectedListing.suppressionReason ?? tr('Listing is hidden by marketplace governance.', 'القائمة مخفية بواسطة حوكمة السوق.')}
                                </InlineNotice>
                            ) : null}

                            {selectedListing.isOrphaned ? (
                                <InlineNotice tone="rose" title={tr('Orphaned listing', 'قائمة يتيمة')}>
                                    {selectedListing.orphanReason}
                                </InlineNotice>
                            ) : null}

                            <div className="grid gap-2">
                                {[
                                    { label: tr('Listing', 'القائمة'), value: selectedListing.listingId },
                                    { label: tr('Tenant', 'المتجر'), value: `${selectedListing.tenantName} (${statusLabel(selectedListing.tenantStatus)})` },
                                    { label: tr('Vendor governance', 'حوكمة البائع'), value: statusLabel(selectedListing.vendorGovernanceStatus) },
                                    { label: tr('Suppression owner', 'مالك الإخفاء'), value: selectedListing.suppressionOwner ?? tr('None', 'لا يوجد') },
                                    { label: tr('Suppressed at', 'وقت الإخفاء'), value: formatDateTime(selectedListing.suppressionTimestampUtc, tr('Not recorded', 'غير مسجل')) },
                                    { label: tr('Override present', 'يوجد تعديل سعر'), value: selectedListing.overridePresent ? tr(`Yes, v${selectedListing.priceOverrideMutationVersion}`, `نعم، الإصدار ${selectedListing.priceOverrideMutationVersion}`) : tr('No', 'لا') },
                                ].map((detail) => (
                                    <div key={detail.label} className="rounded-lg border border-slate-200 bg-white px-3 py-2.5">
                                        <p className="text-[10px] font-semibold uppercase tracking-[0.12em] text-slate-500">{detail.label}</p>
                                        <p className="mt-0.5 break-words text-sm font-medium text-slate-900" dir="auto">{detail.value}</p>
                                    </div>
                                ))}
                            </div>

                            <div className="flex flex-wrap gap-2">
                                <button type="button" disabled={actionDisabled} className={saButton.secondary} onClick={() => runListingAction('suppress')}>
                                    <EyeOff className="h-4 w-4" />
                                    {tr('Suppress listing', 'إخفاء القائمة')}
                                </button>
                                <button type="button" disabled={actionDisabled} className={saButton.success} onClick={() => runListingAction('restore')}>
                                    <RotateCcw className="h-4 w-4" />
                                    {tr('Restore listing', 'استعادة القائمة')}
                                </button>
                                <button type="button" disabled={actionDisabled} className={saButton.danger} onClick={() => runVendorAction('suspend')}>
                                    {tr('Suspend vendor', 'تعليق البائع')}
                                </button>
                                <button type="button" disabled={actionDisabled} className={saButton.secondary} onClick={() => runVendorAction('restore')}>
                                    {tr('Restore vendor', 'استعادة البائع')}
                                </button>
                            </div>
                        </div>
                    )}
                </SectionCard>
            </div>
        </SuperAdminPage>
    )
}
