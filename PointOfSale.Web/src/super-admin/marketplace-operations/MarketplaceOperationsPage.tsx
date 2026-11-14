import { useState } from 'react'
import { useQuery, useQueryClient } from '@tanstack/react-query'
import { Activity, Database, RefreshCw, RotateCw, SearchCheck, Wrench } from 'lucide-react'
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
} from '../ui/SuperAdminUI'
import {
    getMarketplaceOperationsDashboard,
    runGlobalMarketplaceReindex,
    runMarketplaceCatchUpReindex,
    runTenantMarketplaceReindex,
    verifyMarketplaceListing,
    verifyMarketplaceTenant,
} from './marketplaceOperationsService'

function errorMessage(error: unknown, fallback: string): string {
    return error instanceof Error ? error.message : fallback
}

export default function MarketplaceOperationsPage() {
    const queryClient = useQueryClient()
    const { tr, formatDateTime } = useSuperAdminLocale()
    const [tenantId, setTenantId] = useState('')
    const [listingId, setListingId] = useState('')
    const [message, setMessage] = useState<string | null>(null)
    const [error, setError] = useState<string | null>(null)
    const [busyAction, setBusyAction] = useState<string | null>(null)

    const dashboardQuery = useQuery({
        queryKey: ['super-admin', 'marketplace-operations', 'dashboard'],
        queryFn: getMarketplaceOperationsDashboard,
        refetchInterval: 30000,
    })

    const dashboard = dashboardQuery.data

    async function runAction(action: string, work: (reason: string) => Promise<unknown>) {
        const reason = window.prompt(tr('Operation reason', 'سبب العملية'))?.trim()
        if (!reason || reason.length < 3) return

        setBusyAction(action)
        setError(null)
        setMessage(null)
        try {
            const result = await work(reason)
            setMessage(
                tr(
                    `${action} queued or completed. ${JSON.stringify(result)}`,
                    `${action}: تمت الجدولة أو التنفيذ. ${JSON.stringify(result)}`,
                ),
            )
            await queryClient.invalidateQueries({ queryKey: ['super-admin', 'marketplace-operations'] })
        } catch (caught) {
            setError(errorMessage(caught, tr(`${action} failed.`, `فشل ${action}.`)))
        } finally {
            setBusyAction(null)
        }
    }

    const guide = {
        en: {
            title: 'Marketplace operations guide',
            purpose:
                'Shows the SQL source, projected search documents, and auto-healing state, and provides audited reindex and verification controls for the marketplace search pipeline.',
            when:
                'Use it when search results are stale, a tenant or listing looks missing, the index is not compatible after a deploy, or the healing queue reports failures.',
            steps: [
                'Refresh and compare SQL listings with Mongo projections and Elasticsearch documents.',
                'Read search diagnostics: active/write index, desired index, mapping compatibility, and smoke test.',
                'For one tenant, enter the tenant ID and run tenant reindex or verify tenant.',
                'For one listing, enter the listing ID and verify it.',
                'Use global or catch-up reindex only when the index itself must be rebuilt.',
            ],
            statuses: [
                { label: 'Requires reindex: Yes', description: 'The active index does not match the desired index; rebuild before trusting search.' },
                { label: 'Mapping compatible: No', description: 'The deployed mapping differs; reindex is required.' },
                { label: 'Smoke test Passed / Failed', description: 'Whether a real search query succeeded against the index.' },
                { label: 'Pending / Failed sync', description: 'Listings waiting to reach search, or permanently failed and needing repair.' },
                { label: 'Circuit breaker Closed / Open', description: 'Closed means sync is allowed; Open means automatic sync is paused.' },
            ],
            safety: [
                'Every repair action requires a reason and is audited.',
                'Reindex and verify actions change search state; never run them as a first response without evidence.',
                'Global reindex can be expensive and affects all tenants. Prefer tenant or listing verification first.',
                'This screen never deletes SQL data or changes listings; it rebuilds derived search state.',
            ],
        },
        ar: {
            title: 'دليل عمليات السوق',
            purpose:
                'يعرض مصدر SQL والمستندات المتوقعة وحالة الإصلاح التلقائي، ويوفر إجراءات إعادة فهرسة وتحقق مدققة لخط بحث السوق.',
            when:
                'استخدمه عند تأخر نتائج البحث أو فقدان بائع أو قائمة أو عدم توافق الفهرس بعد النشر أو إبلاغ قائمة الإصلاح عن أعطال.',
            steps: [
                'حدّث الصفحة وقارن قوائم SQL مع إسقاطات Mongo ومستندات Elasticsearch.',
                'اقرأ تشخيص البحث: الفهرس النشط والكتابة والمطلوب وتوافق التعيين واختبار الدخان.',
                'لبائع واحد، أدخل معرّف البائع ثم أعد الفهرسة أو تحقق منه.',
                'لقائمة واحدة، أدخل معرّف القائمة ثم تحقق منها.',
                'استخدم إعادة الفهرسة الشاملة أو اللاحقة فقط عند الحاجة لإعادة بناء الفهرس نفسه.',
            ],
            statuses: [
                { label: 'يتطلب إعادة فهرسة: نعم', description: 'الفهرس النشط لا يطابق المطلوب؛ أعد البناء قبل الوثوق بالبحث.' },
                { label: 'التعيين متوافق: لا', description: 'تعيين النشر مختلف؛ إعادة الفهرسة مطلوبة.' },
                { label: 'اختبار الدخان ناجح / فاشل', description: 'هل نجح استعلام بحث حقيقي على الفهرس.' },
                { label: 'مزامنة معلّقة / فاشلة', description: 'قوائم تنتظر الوصول للبحث أو فشلت نهائيًا وتحتاج إصلاحًا.' },
                { label: 'قاطع الدائرة مغلق / مفتوح', description: 'المغلق يعني السماح بالمزامنة، والمفتوح يعني إيقافها مؤقتًا.' },
            ],
            safety: [
                'كل إجراء إصلاح يتطلب سببًا ويُسجل في التدقيق.',
                'إعادة الفهرسة والتحقق تغيّر حالة البحث؛ لا تشغّلها كأول رد دون دليل.',
                'إعادة الفهرسة الشاملة مكلفة وتؤثر على كل البائعين. فضّل التحقق من البائع أو القائمة أولًا.',
                'هذه الشاشة لا تحذف بيانات SQL ولا تعدّل القوائم؛ بل تعيد بناء حالة البحث المشتقة.',
            ],
        },
    }

    const metrics = [
        { icon: Database, label: tr('SQL listings', 'قوائم SQL'), value: dashboard?.sqlListings ?? 0, tone: 'slate' as const },
        { icon: SearchCheck, label: tr('SQL published', 'منشور في SQL'), value: dashboard?.sqlPublishedListings ?? 0, tone: 'emerald' as const },
        { icon: Wrench, label: tr('Suppressed', 'مخفي'), value: dashboard?.sqlSuppressedListings ?? 0, tone: 'amber' as const },
        { icon: Activity, label: tr('Mongo docs', 'مستندات Mongo'), value: dashboard?.mongoProjectedListings ?? 0, tone: 'slate' as const },
        { icon: SearchCheck, label: tr('ES active docs', 'مستندات ES النشطة'), value: dashboard?.elasticsearchActiveDocumentCount ?? 0, tone: 'slate' as const },
        { icon: RotateCw, label: tr('Search pending', 'بحث معلّق'), value: dashboard?.searchSyncPendingListings ?? 0, tone: 'amber' as const },
        { icon: RotateCw, label: tr('Search failed', 'بحث فاشل'), value: dashboard?.searchSyncFailedListings ?? 0, tone: 'rose' as const },
        { icon: Activity, label: tr('Healing queue', 'قائمة الإصلاح'), value: dashboard?.autoHealing.queueDepth ?? 0, tone: 'slate' as const },
        { icon: Activity, label: tr('Healing failures', 'أعطال الإصلاح'), value: dashboard?.autoHealing.failureCount ?? 0, tone: 'rose' as const },
        { icon: Activity, label: tr('Dropped', 'مُسقَطة'), value: dashboard?.autoHealing.droppedCount ?? 0, tone: 'amber' as const },
    ]

    const toneClasses = {
        slate: 'border-slate-200 bg-white text-slate-950',
        emerald: 'border-emerald-200 bg-emerald-50 text-emerald-900',
        amber: 'border-amber-200 bg-amber-50 text-amber-900',
        rose: 'border-rose-200 bg-rose-50 text-rose-900',
    }

    return (
        <SuperAdminPage>
            <PageHeader
                eyebrow={tr('Super Admin · Operations', 'الإدارة العليا · العمليات')}
                title={tr('Marketplace operations', 'عمليات السوق')}
                description={tr(
                    'SQL-derived reindex, search sync, projection recovery, diagnostics, and repair controls.',
                    'إعادة فهرسة مستمدة من SQL ومزامنة بحث واستعادة إسقاطات وتشخيص وأدوات إصلاح.',
                )}
                actions={
                    <button type="button" className={saButton.primary} onClick={() => dashboardQuery.refetch()}>
                        <RefreshCw className={`h-4 w-4 ${dashboardQuery.isFetching ? 'animate-spin' : ''}`} />
                        {tr('Refresh', 'تحديث')}
                    </button>
                }
                aside={<StatusBadge tone={dashboard?.autoHealing.circuitBreakerState === 'Open' ? 'rose' : 'emerald'}>
                    {tr('Healing', 'الإصلاح')}: {dashboard?.autoHealing.circuitBreakerState ?? tr('Unknown', 'غير معروف')}
                </StatusBadge>}
            />

            <GuidePanel guide={guide} />

            {error ? <InlineNotice tone="rose" title={tr('Operation failed', 'فشلت العملية')}>{error}</InlineNotice> : null}
            {message ? <InlineNotice tone="emerald" title={tr('Operation result', 'نتيجة العملية')}>{message}</InlineNotice> : null}

            <div className="grid gap-2 sm:grid-cols-2 xl:grid-cols-5">
                {metrics.map((metric) => {
                    const Icon = metric.icon
                    return (
                        <div key={metric.label} className={`rounded-xl border px-3.5 py-2.5 shadow-sm ${toneClasses[metric.tone]}`}>
                            <div className="flex items-center gap-1.5 text-[10px] font-semibold uppercase tracking-[0.12em] opacity-70">
                                <Icon className="h-3.5 w-3.5" />
                                {metric.label}
                            </div>
                            <p className="mt-1 text-xl font-semibold" dir="auto">{metric.value}</p>
                        </div>
                    )
                })}
            </div>

            <div className="grid gap-4 xl:grid-cols-[minmax(0,1fr)_400px]">
                <SectionCard
                    title={tr('Search diagnostics', 'تشخيص البحث')}
                    description={tr(
                        'Compare the live index with the desired mapping before running a repair.',
                        'قارن الفهرس المباشر مع التعيين المطلوب قبل تنفيذ إصلاح.',
                    )}
                >
                    <div className="grid gap-2 md:grid-cols-2">
                        {[
                            { label: tr('Active index', 'الفهرس النشط'), value: dashboard?.searchIndexStatus.activeIndexName ?? tr('Not available', 'غير متاح') },
                            { label: tr('Write index', 'فهرس الكتابة'), value: dashboard?.searchIndexStatus.writeIndexName ?? tr('Not available', 'غير متاح') },
                            { label: tr('Desired index', 'الفهرس المطلوب'), value: dashboard?.searchIndexStatus.desiredIndexName ?? tr('Not available', 'غير متاح') },
                            { label: tr('Requires reindex', 'يتطلب إعادة فهرسة'), value: dashboard?.searchIndexStatus.requiresReindex ? tr('Yes', 'نعم') : tr('No', 'لا') },
                            { label: tr('Mapping compatible', 'التعيين متوافق'), value: dashboard?.searchIndexStatus.mappingCompatible ? tr('Yes', 'نعم') : tr('No', 'لا') },
                            { label: tr('Smoke test', 'اختبار الدخان'), value: dashboard?.searchIndexVerification.smokeTestPassed ? tr('Passed', 'ناجح') : tr('Failed', 'فاشل') },
                            { label: tr('Circuit breaker', 'قاطع الدائرة'), value: dashboard?.autoHealing.circuitBreakerState ?? tr('Unknown', 'غير معروف') },
                            { label: tr('Last background scan', 'آخر فحص خلفي'), value: formatDateTime(dashboard?.autoHealing.lastBackgroundReconciledAtUtc, tr('Not recorded', 'غير مسجل')) },
                        ].map((detail) => (
                            <div key={detail.label} className="rounded-lg border border-slate-200 bg-slate-50 px-3 py-2.5">
                                <p className="text-[10px] font-semibold uppercase tracking-[0.12em] text-slate-500">{detail.label}</p>
                                <p className="mt-1 break-words text-sm font-medium text-slate-900" dir="auto">{detail.value}</p>
                            </div>
                        ))}
                    </div>
                </SectionCard>

                <SectionCard
                    title={tr('Repair controls', 'أدوات الإصلاح')}
                    description={tr(
                        'Each action requires a reason and is recorded in the audit trail.',
                        'كل إجراء يتطلب سببًا ويُسجل في سجل التدقيق.',
                    )}
                >
                    <div className="space-y-2.5">
                        <button type="button" className={`${saButton.primary} w-full`} disabled={busyAction != null} onClick={() => runAction(tr('Global reindex', 'إعادة فهرسة شاملة'), runGlobalMarketplaceReindex)}>
                            <RotateCw className="h-4 w-4" />
                            {tr('Global reindex', 'إعادة فهرسة شاملة')}
                        </button>
                        <button type="button" className={`${saButton.secondary} w-full`} disabled={busyAction != null} onClick={() => runAction(tr('Catch-up reindex', 'إعادة فهرسة لاحقة'), runMarketplaceCatchUpReindex)}>
                            <RotateCw className="h-4 w-4" />
                            {tr('Catch-up reindex', 'إعادة فهرسة لاحقة')}
                        </button>
                    </div>

                    <div className="mt-4 space-y-2">
                        <input value={tenantId} onChange={(event) => setTenantId(event.target.value)} placeholder={tr('Tenant ID', 'معرّف البائع')} className={saInput} />
                        <div className="grid grid-cols-2 gap-2">
                            <button type="button" className={`${saButton.secondary} px-2`} disabled={!tenantId.trim() || busyAction != null} onClick={() => runAction(tr('Tenant reindex', 'إعادة فهرسة البائع'), (reason) => runTenantMarketplaceReindex(tenantId.trim(), reason))}>
                                {tr('Tenant reindex', 'إعادة فهرسة')}
                            </button>
                            <button type="button" className={`${saButton.secondary} px-2`} disabled={!tenantId.trim() || busyAction != null} onClick={() => runAction(tr('Tenant verify', 'تحقق البائع'), (reason) => verifyMarketplaceTenant(tenantId.trim(), reason))}>
                                {tr('Verify tenant', 'تحقق')}
                            </button>
                        </div>
                    </div>

                    <div className="mt-4 space-y-2">
                        <input value={listingId} onChange={(event) => setListingId(event.target.value)} placeholder={tr('Listing ID', 'معرّف القائمة')} className={saInput} />
                        <button type="button" className={`${saButton.secondary} w-full`} disabled={!listingId.trim() || busyAction != null} onClick={() => runAction(tr('Listing verify', 'تحقق القائمة'), (reason) => verifyMarketplaceListing(listingId.trim(), reason))}>
                            {tr('Verify listing', 'تحقق من القائمة')}
                        </button>
                    </div>
                </SectionCard>
            </div>
        </SuperAdminPage>
    )
}
