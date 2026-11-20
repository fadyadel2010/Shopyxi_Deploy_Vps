import { useEffect, useMemo, useRef, useState } from 'react'
import { useConfirmDialog } from '../../common/confirm/ConfirmDialogProvider'
import { useNavigate } from 'react-router-dom'
import {
    AlertCircle,
    CalendarClock,
    CheckCircle2,
    Clock3,
    Eye,
    FilePlus2,
    FileText,
    Globe,
    Loader2,
    PencilLine,
    RefreshCcw,
    Trash2,
} from 'lucide-react'
import { marketplaceCmsService } from './marketplaceCmsService'
import { useSuperAdminLocale } from '../i18n/useSuperAdminLocale'
import {
    EmptyState,
    ErrorState,
    GuidePanel,
    InlineNotice,
    KpiCard,
    LoadingState,
    PageHeader,
    StatusBadge,
    SuperAdminPage,
    saButton,
} from '../ui/SuperAdminUI'

const statusStyles = {
    Published: 'bg-emerald-50 text-emerald-700',
    Draft: 'bg-amber-50 text-amber-700',
    UnpublishedChanges: 'bg-sky-50 text-sky-700',
}

export function MarketplaceCmsPagesPage() {
    const navigate = useNavigate()
    const { confirmAction } = useConfirmDialog()
    const operationRef = useRef(false)
    const { tr, formatDate, formatDateTime, formatNumber } = useSuperAdminLocale()

    const [pages, setPages] = useState([])
    const [isLoading, setIsLoading] = useState(true)
    const [busyPageId, setBusyPageId] = useState(null)
    const [error, setError] = useState('')
    const [actionError, setActionError] = useState('')
    const [actionNotice, setActionNotice] = useState('')

    const publicBaseUrl = useMemo(
        () => import.meta.env.VITE_MARKETPLACE_PUBLIC_URL || window.location.origin,
        [],
    )

    const publishedCount = pages.filter((page) => Boolean(page.hasPublished)).length
    const draftCount = pages.filter((page) => Boolean(page.hasDraft)).length
    const pendingPublishCount = pages.filter((page) => page.status === 'UnpublishedChanges').length
    const latestUpdate = pages.reduce((latest, page) => {
        if (!page.updatedAtUtc) return latest
        if (!latest) return page.updatedAtUtc
        return new Date(page.updatedAtUtc).getTime() > new Date(latest).getTime() ? page.updatedAtUtc : latest
    }, '')

    useEffect(() => {
        loadPages()
    }, [])

    async function loadPages() {
        setIsLoading(true)
        setError('')
        setActionError('')
        setActionNotice('')

        try {
            const data = await marketplaceCmsService.listPages()
            setPages(data)
        } catch (err) {
            console.error('Failed to load marketplace CMS pages', err)
            setPages([])
            setError(tr('Unable to load marketplace CMS pages.', 'تعذّر تحميل صفحات CMS الخاصة بالسوق.'))
        } finally {
            setIsLoading(false)
        }
    }

    async function handlePublish(page) {
        if (operationRef.current) return
        operationRef.current = true
        setActionError('')
        setActionNotice('')
        try {
            await confirmAction({
                title: tr('Publish page', 'نشر الصفحة'),
                description: tr(`Publish "${page.title}" to the marketplace domain now?`, `هل تريد نشر "${page.title}" على نطاق السوق الآن؟`),
                confirmLabel: tr('Publish', 'نشر'),
                awaitAction: true,
                onConfirm: async () => {
                    setBusyPageId(page.id)
                    const published = await marketplaceCmsService.publishPage(page.id, page.draftVersion)
                    setPages((current) => current.map((item) => (
                        item.id === published.id ? adoptPageResponse(item, published) : item
                    )))
                    setActionNotice(
                        tr(
                            `"${page.title}" is now live on the marketplace domain.`,
                            `أصبحت "${page.title}" مباشرة الآن على نطاق السوق.`,
                        ),
                    )
                },
            })
        } catch (err) {
            console.error('Failed to publish marketplace CMS page', err)
            if (marketplaceCmsService.isConcurrencyConflict(err)) {
                await loadPages()
            }
            setActionError(tr('Failed to publish marketplace page. Refresh before trying again.', 'تعذّر نشر صفحة السوق. حدّث الصفحة قبل إعادة المحاولة.'))
        } finally {
            operationRef.current = false
            setBusyPageId(null)
        }
    }

    async function handleDelete(page) {
        if (operationRef.current) return
        operationRef.current = true
        setActionError('')
        setActionNotice('')
        try {
            await confirmAction({
                title: tr('Delete page', 'حذف الصفحة'),
                description: tr(`Delete "${page.title}" and all of its versions?`, `هل تريد حذف "${page.title}" وكل إصداراتها؟`),
                confirmLabel: tr('Delete', 'حذف'),
                variant: 'danger',
                awaitAction: true,
                onConfirm: async () => {
                    setBusyPageId(page.id)
                    const deleted = await marketplaceCmsService.deletePage(
                        page.id,
                        marketplaceCmsService.getListItemVersion(page),
                    )
                    setPages((current) => current.filter((item) => item.id !== deleted.id))
                    setActionNotice(tr(`"${page.title}" was deleted.`, `تم حذف "${page.title}".`))
                },
            })
        } catch (err) {
            console.error('Failed to delete marketplace CMS page', err)
            if (marketplaceCmsService.isConcurrencyConflict(err)) {
                await loadPages()
            }
            setActionError(tr('Failed to delete marketplace page. Refresh before trying again.', 'تعذّر حذف صفحة السوق. حدّث الصفحة قبل إعادة المحاولة.'))
        } finally {
            operationRef.current = false
            setBusyPageId(null)
        }
    }

    async function handlePreview(page) {
        if (operationRef.current) return
        operationRef.current = true
        setBusyPageId(page.id)
        setActionError('')

        try {
            const previewAccess = await marketplaceCmsService.previewPage(page.id)
            const previewUrl = `${publicBaseUrl}/${previewAccess.slug}?preview=true&previewToken=${encodeURIComponent(previewAccess.previewToken)}`
            window.open(previewUrl, '_blank', 'noopener,noreferrer')
        } catch (err) {
            console.error('Failed to open marketplace CMS preview', err)
            setActionError(tr('Unable to create a secure preview link.', 'تعذّر إنشاء رابط معاينة آمن.'))
        } finally {
            operationRef.current = false
            setBusyPageId(null)
        }
    }

    function getStatusLabel(page) {
        if (page.status === 'UnpublishedChanges') {
            return tr('Draft ready to publish', 'مسودة جاهزة للنشر')
        }
        if (page.status === 'Published') {
            return tr('Published', 'منشور')
        }
        return tr('Draft', 'مسودة')
    }

    function renderStatus(page) {
        const icon = page.status === 'Published'
            ? <CheckCircle2 size={14} />
            : page.status === 'UnpublishedChanges'
                ? <AlertCircle size={14} />
                : <Clock3 size={14} />

        return (
            <span className={`inline-flex items-center gap-1.5 rounded-full border px-2.5 py-1 text-xs font-semibold ${statusStyles[page.status] || statusStyles.Draft}`}>
                {icon}
                {getStatusLabel(page)}
            </span>
        )
    }

    function renderMode(page) {
        return page.contentType === 'Blocks' ? tr('Blocks', 'كتل') : tr('Rich text', 'نص منسق')
    }

    return (
        <SuperAdminPage>
            <PageHeader
                eyebrow={tr('Super Admin · Global content', 'الإدارة العليا · المحتوى العام')}
                title={tr('Marketplace CMS pages', 'صفحات CMS الخاصة بالسوق')}
                description={tr(
                    'Create and publish global marketplace pages such as About, Privacy, Terms, and campaign landing pages. These pages are separate from tenant storefront CMS content.',
                    'أنشئ صفحات السوق العامة وانشرها، مثل من نحن والخصوصية والشروط وصفحات الحملات. وهذه الصفحات منفصلة عن محتوى CMS الخاص بمتاجر المستأجرين.',
                )}
                actions={
                    <>
                        <button type="button" onClick={() => navigate('/super-admin/marketplace/footer')} className={saButton.secondary}>
                            <Globe size={16} />
                            {tr('Footer & Social', 'التذييل والروابط الاجتماعية')}
                        </button>
                        <button type="button" onClick={loadPages} className={saButton.secondary}>
                            <RefreshCcw size={16} />
                            {tr('Refresh', 'تحديث')}
                        </button>
                        <button
                            type="button"
                            onClick={() => navigate('/super-admin/marketplace/pages/new')}
                            className={saButton.primary}
                        >
                            <FilePlus2 size={16} />
                            {tr('New Page', 'صفحة جديدة')}
                        </button>
                    </>
                }
            />

            <div className="grid gap-3 sm:grid-cols-2 xl:grid-cols-4">
                <KpiCard
                    label={tr('Marketplace pages', 'صفحات السوق')}
                    value={formatNumber(pages.length)}
                    hint={tr('Global pages served from the marketplace root.', 'صفحات عامة تُقدَّم من جذر السوق.')}
                    tone="sky"
                    icon={<FileText className="h-4 w-4 text-sky-700" />}
                    isLoading={isLoading}
                />
                <KpiCard
                    label={tr('Published live', 'منشورة مباشرة')}
                    value={formatNumber(publishedCount)}
                    hint={tr('Visible to marketplace visitors now.', 'مرئية لزوار السوق الآن.')}
                    tone="emerald"
                    icon={<Globe className="h-4 w-4 text-emerald-700" />}
                    isLoading={isLoading}
                />
                <KpiCard
                    label={tr('Drafts waiting', 'مسودات في الانتظار')}
                    value={formatNumber(draftCount)}
                    hint={pendingPublishCount > 0
                        ? tr(
                            `${formatNumber(pendingPublishCount)} with unpublished changes`,
                            `${formatNumber(pendingPublishCount)} بها تغييرات غير منشورة`,
                        )
                        : tr('No unpublished changes', 'لا توجد تغييرات غير منشورة')}
                    tone={pendingPublishCount > 0 ? 'amber' : 'slate'}
                    icon={<Clock3 className="h-4 w-4" />}
                    isLoading={isLoading}
                />
                <KpiCard
                    label={tr('Latest update', 'آخر تحديث')}
                    value={latestUpdate ? formatDate(latestUpdate) : tr('No pages yet', 'لا صفحات بعد')}
                    hint={latestUpdate
                        ? tr(`Last edited ${formatDateTime(latestUpdate)}`, `آخر تحرير ${formatDateTime(latestUpdate)}`)
                        : tr('Based on the most recently edited page.', 'استنادًا إلى أحدث صفحة تم تحريرها.')}
                    tone="slate"
                    icon={<CalendarClock className="h-4 w-4" />}
                    isLoading={isLoading}
                />
            </div>

            <GuidePanel
                guide={{
                    en: {
                        title: 'Marketplace CMS pages guide',
                        purpose:
                            'Manages global pages published on the Shopixy marketplace domain, such as About, Privacy, Terms, and campaign landing pages. These pages are independent of tenant storefront CMS content.',
                        when:
                            'Use it when adding a new marketplace policy page, updating legal copy, launching a campaign landing page, or previewing and publishing a draft.',
                        steps: [
                            'Create a page with a unique slug, title, and content mode.',
                            'Edit the body in the CMS page editor and translate it into Arabic.',
                            'Preview the page through a secure preview link before publishing.',
                            'Publish the draft to make the page live at the marketplace root, for example /about.',
                            'Use Refresh after another session changes a page to reload the authoritative list.',
                        ],
                        statuses: [
                            { label: 'Published', description: 'No draft remains; the live published version is the current version.' },
                            { label: 'Draft', description: 'A draft exists and has never been published.' },
                            { label: 'Draft ready to publish', description: 'A draft exists alongside a published version; the draft differs from live.' },
                            { label: 'Blocks / Rich text', description: 'The page body is composed of structured blocks or a rich-text document.' },
                        ],
                        safety: [
                            'Publishing replaces the live page on the marketplace domain immediately.',
                            'Deleting removes the page and every version; this cannot be undone from this screen.',
                            'Concurrency conflicts reload the authoritative list instead of overwriting another session.',
                            'Tenant storefront CMS pages are never read or modified from this screen.',
                            'Preview links use a short-lived preview token and open in a new tab.',
                        ],
                    },
                    ar: {
                        title: 'دليل صفحات CMS الخاصة بالسوق',
                        purpose:
                            'يدير الصفحات العامة المنشورة على نطاق سوق Shopixy، مثل من نحن والخصوصية والشروط وصفحات الحملات. وهذه الصفحات مستقلة عن محتوى CMS الخاص بمتاجر المستأجرين.',
                        when:
                            'استخدمه عند إضافة صفحة سياسات جديدة للسوق، أو تحديث نص قانوني، أو إطلاق صفحة هبوط لحملة، أو معاينة مسودة ونشرها.',
                        steps: [
                            'أنشئ صفحة بمعرّف فريد وعنوان ووضع محتوى.',
                            'حرّر المحتوى في محرر صفحات CMS وترجمه إلى العربية.',
                            'عاين الصفحة عبر رابط معاينة آمن قبل النشر.',
                            'انشر المسودة لتصبح الصفحة مباشرة على جذر السوق، مثل /about.',
                            'استخدم تحديث بعد تغيير صفحة في جلسة أخرى لإعادة تحميل القائمة المعتمدة.',
                        ],
                        statuses: [
                            { label: 'منشور', description: 'لا توجد مسودة متبقية؛ النسخة المنشورة المباشرة هي النسخة الحالية.' },
                            { label: 'مسودة', description: 'توجد مسودة ولم تُنشر من قبل.' },
                            { label: 'مسودة جاهزة للنشر', description: 'توجد مسودة إلى جانب نسخة منشورة؛ والمسودة تختلف عن المباشر.' },
                            { label: 'كتل / نص منسق', description: 'يتكوّن محتوى الصفحة من كتل منظمة أو مستند نص منسق.' },
                        ],
                        safety: [
                            'النشر يستبدل الصفحة المباشرة على نطاق السوق فورًا.',
                            'الحذف يزيل الصفحة وكل إصداراتها؛ ولا يمكن التراجع عنه من هذه الشاشة.',
                            'تعارضات التزامن تعيد تحميل القائمة المعتمدة بدل الكتابة فوق جلسة أخرى.',
                            'لا تُقرأ صفحات CMS الخاصة بمتاجر المستأجرين أو تُعدَّل من هذه الشاشة.',
                            'روابط المعاينة تستخدم رمزًا مؤقتًا وتفتح في تبويب جديد.',
                        ],
                    },
                }}
            />

            <InlineNotice tone="sky">
                {tr(
                    'Public routes resolve at the marketplace root, for example /about, /privacy, and /terms. Tenant storefront pages remain isolated in the storefront app and are not read from this system.',
                    'تُحل المسارات العامة على جذر السوق، مثل /about و/privacy و/terms. وتبقى صفحات متاجر المستأجرين معزولة في تطبيق المتجر ولا تُقرأ من هذا النظام.',
                )}
            </InlineNotice>

            {error ? (
                <ErrorState
                    title={tr('Marketplace pages unavailable', 'صفحات السوق غير متاحة')}
                    description={error}
                    onRetry={loadPages}
                    retryLabel={tr('Retry', 'إعادة المحاولة')}
                />
            ) : null}

            {actionError ? (
                <InlineNotice tone="rose" title={tr('Action not completed', 'لم يكتمل الإجراء')}>
                    <span dir="auto" role="alert">{actionError}</span>
                </InlineNotice>
            ) : null}

            {actionNotice ? (
                <InlineNotice tone="emerald" title={tr('Done', 'تم')}>
                    {actionNotice}
                </InlineNotice>
            ) : null}

            {!error && isLoading ? (
                <LoadingState label={tr('Loading marketplace pages…', 'جارٍ تحميل صفحات السوق…')} />
            ) : !error && pages.length === 0 ? (
                <EmptyState
                    title={tr('No marketplace pages yet', 'لا صفحات سوق بعد')}
                    description={tr(
                        'Start with an About page, a policy page, or a campaign landing page for the marketplace domain.',
                        'ابدأ بصفحة من نحن أو صفحة سياسات أو صفحة هبوط لحملة على نطاق السوق.',
                    )}
                    action={
                        <button
                            type="button"
                            onClick={() => navigate('/super-admin/marketplace/pages/new')}
                            className={saButton.primary}
                        >
                            <FilePlus2 size={16} />
                            {tr('Create First Page', 'إنشاء أول صفحة')}
                        </button>
                    }
                />
            ) : !error ? (
                <section className="overflow-hidden rounded-2xl border border-slate-200 bg-white shadow-sm">
                    <div className="hidden grid-cols-[1.3fr_1fr_1fr_1fr_auto] gap-4 border-b border-slate-100 bg-slate-50 px-5 py-3 text-[11px] font-semibold uppercase tracking-wide text-slate-500 lg:grid">
                        <span>{tr('Page', 'الصفحة')}</span>
                        <span>{tr('Mode', 'الوضع')}</span>
                        <span>{tr('Status', 'الحالة')}</span>
                        <span>{tr('Updated', 'آخر تحديث')}</span>
                        <span className="text-end">{tr('Actions', 'إجراءات')}</span>
                    </div>

                    <div className="divide-y divide-slate-100">
                        {pages.map((page) => {
                            const isBusy = busyPageId === page.id

                            return (
                                <div
                                    key={page.id}
                                    className="grid min-w-0 gap-3 px-5 py-4 lg:grid-cols-[1.3fr_1fr_1fr_1fr_auto] lg:items-center lg:gap-4"
                                >
                                    <div className="min-w-0">
                                        <div className="truncate font-semibold text-slate-900" dir="auto">{page.title}</div>
                                        <div className="mt-0.5 truncate text-xs text-slate-500" dir="ltr">/{page.slug}</div>
                                    </div>

                                    <div className="flex flex-wrap items-center gap-2 text-sm text-slate-600">
                                        <span className="text-[11px] font-semibold uppercase tracking-wide text-slate-400 lg:hidden">
                                            {tr('Mode', 'الوضع')}
                                        </span>
                                        <StatusBadge tone={page.contentType === 'Blocks' ? 'violet' : 'slate'}>
                                            {renderMode(page)}
                                        </StatusBadge>
                                    </div>

                                    <div className="flex flex-wrap items-center gap-2">
                                        <span className="text-[11px] font-semibold uppercase tracking-wide text-slate-400 lg:hidden">
                                            {tr('Status', 'الحالة')}
                                        </span>
                                        {renderStatus(page)}
                                    </div>

                                    <div className="flex flex-wrap items-center gap-2 text-xs text-slate-500">
                                        <span className="text-[11px] font-semibold uppercase tracking-wide text-slate-400 lg:hidden">
                                            {tr('Updated', 'آخر تحديث')}
                                        </span>
                                        {page.updatedAtUtc ? formatDateTime(page.updatedAtUtc) : tr('Not available', 'غير متاح')}
                                    </div>

                                    <div className="flex flex-wrap justify-start gap-2 lg:justify-end">
                                        <button
                                            type="button"
                                            onClick={() => navigate(`/super-admin/marketplace/pages/${page.id}`)}
                                            className="rounded-lg border border-slate-200 p-2 text-slate-600 transition hover:border-slate-300 hover:bg-slate-50"
                                            title={tr('Edit', 'تحرير')}
                                            aria-label={tr(`Edit ${page.title}`, `تحرير ${page.title}`)}
                                        >
                                            <PencilLine size={16} />
                                        </button>
                                        <button
                                            type="button"
                                            onClick={() => handlePreview(page)}
                                            disabled={isBusy}
                                            className="rounded-lg border border-slate-200 p-2 text-slate-600 transition hover:border-slate-300 hover:bg-slate-50 disabled:opacity-60"
                                            title={tr('Preview', 'معاينة')}
                                            aria-label={tr(`Preview ${page.title}`, `معاينة ${page.title}`)}
                                        >
                                            {isBusy ? <Loader2 size={16} className="animate-spin" /> : <Eye size={16} />}
                                        </button>
                                        <button
                                            type="button"
                                            onClick={() => handlePublish(page)}
                                            disabled={isBusy || !page.hasDraft}
                                            className="rounded-lg border border-emerald-200 p-2 text-emerald-700 transition hover:bg-emerald-50 disabled:opacity-60"
                                            title={tr('Publish', 'نشر')}
                                            aria-label={tr(`Publish ${page.title}`, `نشر ${page.title}`)}
                                        >
                                            {isBusy ? <Loader2 size={16} className="animate-spin" /> : <Globe size={16} />}
                                        </button>
                                        <button
                                            type="button"
                                            onClick={() => handleDelete(page)}
                                            disabled={isBusy}
                                            className="rounded-lg border border-rose-200 p-2 text-rose-700 transition hover:bg-rose-50 disabled:opacity-60"
                                            title={tr('Delete', 'حذف')}
                                            aria-label={tr(`Delete ${page.title}`, `حذف ${page.title}`)}
                                        >
                                            {isBusy ? <Loader2 size={16} className="animate-spin" /> : <Trash2 size={16} />}
                                        </button>
                                    </div>
                                </div>
                            )
                        })}
                    </div>
                </section>
            ) : null}

            {!error && !isLoading && pages.length > 0 ? (
                <p className="px-1 text-xs text-slate-500">
                    {tr(
                        `${formatNumber(pages.length)} pages · preview links use short-lived tokens`,
                        `${formatNumber(pages.length)} صفحات · روابط المعاينة تستخدم رموزًا مؤقتة`,
                    )}
                </p>
            ) : null}
        </SuperAdminPage>
    )
}

function adoptPageResponse(current, response) {
    const hasDraft = Boolean(response.draft)
    const hasPublished = Boolean(response.published)

    return {
        ...current,
        id: response.id,
        slug: response.slug,
        title: response.title,
        titleLocalized: response.titleLocalized,
        contentType: response.contentType,
        hasDraft,
        hasPublished,
        hasPendingChanges: response.hasPendingChanges,
        status: hasDraft ? (hasPublished ? 'UnpublishedChanges' : 'Draft') : 'Published',
        draftVersion: response.draft?.version ?? null,
        publishedVersion: response.published?.version ?? null,
        updatedAtUtc: response.updatedAtUtc,
        publishedAtUtc: response.publishedAtUtc,
    }
}

export default MarketplaceCmsPagesPage
