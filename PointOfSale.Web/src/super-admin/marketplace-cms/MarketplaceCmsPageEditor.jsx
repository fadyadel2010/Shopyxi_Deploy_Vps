import { useEffect, useMemo, useRef, useState } from 'react'
import { useNavigate, useParams } from 'react-router-dom'
import { ArrowLeft, Eye, FileText, Globe, Languages, Loader2, Plus, RefreshCcw, RotateCcw, Save, Sparkles, Trash2 } from 'lucide-react'
import { marketplaceCmsService } from './marketplaceCmsService'
import { useConfirmDialog } from '../../common/confirm/ConfirmDialogProvider'
import { sanitizeRichTextHtml } from '@/utils/htmlSanitizer'
import { getAuthoringErrorFeedback } from '../../modules/storefront/authoringPlatform'
import { useSuperAdminLocale } from '../i18n/useSuperAdminLocale'
import {
    ErrorState,
    GuidePanel,
    InlineNotice,
    KpiCard,
    LoadingState,
    PageHeader,
    SuperAdminPage,
    saButton,
    saInput,
} from '../ui/SuperAdminUI'

const CONTENT_TYPES = { RichText: 'RichText', Blocks: 'Blocks' }
const SUPPORTED_LANGUAGES = ['en', 'ar']
const EMPTY_LOCALIZED_FIELD = { en: '', ar: '' }

function createEmptyForm() {
    return {
        slug: '',
        titleLocalized: { ...EMPTY_LOCALIZED_FIELD },
        contentType: CONTENT_TYPES.RichText,
        localizedContent: {
            en: '',
            ar: '',
        },
        seo: {
            metaTitleLocalized: { ...EMPTY_LOCALIZED_FIELD },
            metaDescriptionLocalized: { ...EMPTY_LOCALIZED_FIELD },
            openGraphImageUrl: '',
        },
    }
}

function createBlock(type) {
    if (type === 'image') {
        return { type, data: { imageUrl: '', alt: '', caption: '' } }
    }

    if (type === 'faq') {
        return { type, data: { items: [{ question: '', answer: '' }] } }
    }

    return { type: 'text', data: { text: '', alignment: 'left' } }
}

export function MarketplaceCmsPageEditor() {
    const { pageId } = useParams()
    const navigate = useNavigate()
    const { confirm, confirmAction } = useConfirmDialog()
    const operationRef = useRef(false)
    const acceptedPageIdRef = useRef(null)
    const { tr, formatDate, formatDateTime } = useSuperAdminLocale()
    const isNewPage = pageId === 'new'

    const [page, setPage] = useState(null)
    const [form, setForm] = useState(createEmptyForm())
    const [acceptedFingerprint, setAcceptedFingerprint] = useState(() => getMarketplaceFormFingerprint(createEmptyForm()))
    const [isLoading, setIsLoading] = useState(!isNewPage)
    const [isSaving, setIsSaving] = useState(false)
    const [isPublishing, setIsPublishing] = useState(false)
    const [activeLanguage, setActiveLanguage] = useState('en')
    const [translatingField, setTranslatingField] = useState('')
    const [error, setError] = useState('')
    const [notice, setNotice] = useState('')

    const publicBaseUrl = useMemo(
        () => import.meta.env.VITE_MARKETPLACE_PUBLIC_URL || window.location.origin,
        [],
    )
    const currentFingerprint = useMemo(() => getMarketplaceFormFingerprint(form), [form])
    const isDirty = currentFingerprint !== acceptedFingerprint

    useEffect(() => {
        if (!isDirty) return undefined
        const handleBeforeUnload = (event) => {
            event.preventDefault()
            event.returnValue = ''
        }
        window.addEventListener('beforeunload', handleBeforeUnload)
        return () => window.removeEventListener('beforeunload', handleBeforeUnload)
    }, [isDirty])

    useEffect(() => {
        if (isNewPage) {
            acceptedPageIdRef.current = null
            setPage(null)
            const emptyForm = createEmptyForm()
            setForm(emptyForm)
            setAcceptedFingerprint(getMarketplaceFormFingerprint(emptyForm))
            setIsLoading(false)
            return
        }
        // A successful create already supplied authoritative identity/version
        // evidence. Canonical navigation must not race that response with an
        // older read while the confirmed publish is still settling.
        if (acceptedPageIdRef.current === pageId) {
            setIsLoading(false)
            return
        }

        let cancelled = false

        async function loadPage() {
            setIsLoading(true)
            setError('')

            try {
                const data = await marketplaceCmsService.getPage(pageId)
                if (!cancelled) {
                    adoptMarketplaceResponse(data)
                }
            } catch (err) {
                if (!cancelled) {
                    console.error('Failed to load marketplace CMS page', err)
                    setError(tr('Unable to load marketplace page.', 'تعذّر تحميل صفحة السوق.'))
                }
            } finally {
                if (!cancelled) {
                    setIsLoading(false)
                }
            }
        }

        loadPage()

        return () => {
            cancelled = true
        }
    }, [isNewPage, pageId])

    function adoptMarketplaceResponse(response) {
        acceptedPageIdRef.current = response.id
        const acceptedForm = fromPageResponse(response)
        setPage(response)
        setForm(acceptedForm)
        setAcceptedFingerprint(getMarketplaceFormFingerprint(acceptedForm))
        return response
    }

    function patchForm(patch) {
        setForm((current) => ({ ...current, ...patch }))
    }

    function authoringFailure(error) {
        const { kind } = getAuthoringErrorFeedback(error)
        if (kind === 'conflict' || kind === 'precondition') {
            return tr('The page changed. Refresh the latest version before retrying your draft.', 'تغيّرت الصفحة. حدّث أحدث إصدار قبل إعادة محاولة حفظ المسودة.')
        }
        if (kind === 'forbidden') {
            return tr('You do not have permission to complete this action.', 'ليس لديك إذن لإكمال هذا الإجراء.')
        }
        if (kind === 'validation' || kind === 'duplicate' || kind === 'already-exists') {
            return tr('Review the page fields and unique slug, then try again.', 'راجع حقول الصفحة والمعرّف الفريد، ثم أعد المحاولة.')
        }
        return tr('The authoring request could not be completed. Your current draft is retained; refresh before retrying.', 'تعذّر إكمال طلب التحرير. تم الاحتفاظ بالمسودة الحالية؛ حدّث الصفحة قبل إعادة المحاولة.')
    }

    function patchLocalizedField(fieldKey, value) {
        setForm((current) => ({
            ...current,
            [fieldKey]: {
                ...(current[fieldKey] || EMPTY_LOCALIZED_FIELD),
                [activeLanguage]: value,
            },
        }))
    }

    function patchSeo(fieldKey, value) {
        setForm((current) => ({
            ...current,
            seo: {
                ...current.seo,
                [fieldKey]: {
                    ...(current.seo[fieldKey] || EMPTY_LOCALIZED_FIELD),
                    [activeLanguage]: value,
                },
            },
        }))
    }

    function patchLocalizedContent(value) {
        setForm((current) => ({
            ...current,
            localizedContent: {
                ...current.localizedContent,
                [activeLanguage]: value,
            },
        }))
    }

    async function handleSave() {
        if (operationRef.current) return null
        operationRef.current = true
        try { return await saveDraft() } finally { operationRef.current = false }
    }

    async function saveDraft() {
        if (!form.slug.trim() || !form.titleLocalized.en.trim()) {
            setError(tr('English title and slug are required.', 'العنوان الإنجليزي والمعرّف مطلوبان.'))
            return null
        }
        if (!isNewPage && !isDirty) return page

        setIsSaving(true)
        setError('')
        setNotice('')

        try {
            const payload = toRequestPayload(form)
            const saved = isNewPage
                ? await marketplaceCmsService.createPage(payload)
                : await marketplaceCmsService.updatePage(
                    pageId,
                    payload,
                    marketplaceCmsService.getPageVersion(page),
                )

            adoptMarketplaceResponse(saved)
            setNotice(tr('Draft saved and backend response adopted.', 'تم حفظ المسودة واعتماد استجابة الخادم.'))

            if (isNewPage) {
                navigate(`/super-admin/marketplace/pages/${saved.id}`, { replace: true })
            }

            return saved
        } catch (err) {
            console.error('Failed to save marketplace CMS page', err)
            if (marketplaceCmsService.isConcurrencyConflict(err)) {
                const refreshed = await refreshPageAfterConflict()
                setError(
                    refreshed
                        ? tr(
                              'The latest page version has been loaded.',
                              'تم تحميل أحدث إصدار للصفحة.',
                          )
                        : tr(
                              'The latest page version could not be loaded.',
                              'تعذّر تحميل أحدث إصدار للصفحة.',
                          ),
                )
            } else {
                setError(authoringFailure(err))
            }
            return null
        } finally {
            setIsSaving(false)
        }
    }

    async function handlePublish() {
        if (operationRef.current) return
        operationRef.current = true
        try {
            await confirmAction({
                title: tr('Publish page', 'نشر الصفحة'),
                description: tr('Save any changes and publish this page to the marketplace domain now?', 'هل تريد حفظ التغييرات ونشر هذه الصفحة على نطاق السوق الآن؟'),
                confirmLabel: tr('Publish', 'نشر'),
                awaitAction: true,
                onConfirm: async () => {
                    setIsPublishing(true)
                    const target = (isDirty || !page) ? await saveDraft() : page
                    if (!target) return false
                    const published = await marketplaceCmsService.publishPage(
                        target.id,
                        marketplaceCmsService.getPageVersion(target),
                    )
                    adoptMarketplaceResponse(published)
                    setNotice(tr('Marketplace page published successfully.', 'تم نشر صفحة السوق بنجاح.'))
                },
            })
        } catch (err) {
            console.error('Failed to publish marketplace CMS page', err)
            if (marketplaceCmsService.isConcurrencyConflict(err)) {
                await refreshPageAfterConflict()
            }
            setError(authoringFailure(err))
        } finally {
            operationRef.current = false
            setIsPublishing(false)
        }
    }

    async function refreshPageAfterConflict() {
        try {
            const latest = await marketplaceCmsService.getPage(pageId)
            setPage(latest)
            return true
        } catch (refreshError) {
            console.error('Failed to refresh marketplace CMS page after a version conflict', refreshError)
            setPage(null)
            return false
        }
    }

    async function handlePreview() {
        if (isNewPage) {
            setError(tr('Save the page first so a preview draft exists.', 'احفظ الصفحة أولًا لتتوفر مسودة للمعاينة.'))
            return
        }
        if (isDirty) {
            setError(
                tr(
                    'Save your changes before previewing. Preview never saves or changes versions.',
                    'احفظ تغييراتك قبل المعاينة. المعاينة لا تحفظ ولا تغيّر الإصدارات.',
                ),
            )
            return
        }

        try {
            const previewAccess = await marketplaceCmsService.previewPage(pageId)
            const previewUrl = `${publicBaseUrl}/${activeLanguage}/${previewAccess.slug}?preview=true&previewToken=${encodeURIComponent(previewAccess.previewToken)}`
            window.open(previewUrl, '_blank', 'noopener,noreferrer')
        } catch (err) {
            console.error('Failed to create marketplace preview link', err)
            setError(authoringFailure(err))
        }
    }

    async function handleAutoTranslate(fieldKey) {
        if (operationRef.current) return
        if (activeLanguage !== 'ar') {
            return
        }

        const translationTarget = getTranslationTarget(form, fieldKey, setForm)
        if (!translationTarget) {
            return
        }

        if (!hasTranslatableContent(translationTarget.source)) {
            setError(tr('Add English content first, then translate it into Arabic.', 'أضف المحتوى الإنجليزي أولًا ثم ترجمه إلى العربية.'))
            return
        }

        operationRef.current = true
        try {
            const translate = async () => {
                setTranslatingField(fieldKey)
                const translated = await marketplaceCmsService.translateContent(translationTarget.source, 'en', 'ar')
                translationTarget.apply(translated)
            }
            if (hasTranslatableContent(translationTarget.target)) {
                await confirmAction({
                    title: tr('Replace Arabic content', 'استبدال المحتوى العربي'),
                    description: tr('Arabic content already exists. Overwrite it with a fresh AI translation?', 'المحتوى العربي موجود بالفعل. هل تريد استبداله بترجمة جديدة من الذكاء الاصطناعي؟'),
                    awaitAction: true,
                    onConfirm: translate,
                })
            } else await translate()
        } catch (err) {
            console.error('Marketplace translation failed', err)
            setError(tr('Unable to translate this field right now.', 'تعذّرت ترجمة هذا الحقل حاليًا.'))
        } finally {
            operationRef.current = false
            setTranslatingField('')
        }
    }

    function addBlock(type) {
        setForm((current) => ({
            ...current,
            contentType: CONTENT_TYPES.Blocks,
            localizedContent: {
                ...current.localizedContent,
                [activeLanguage]: [...ensureBlocks(current.localizedContent?.[activeLanguage]), createBlock(type)],
            },
        }))
    }

    function updateBlock(index, nextBlock) {
        setForm((current) => ({
            ...current,
            localizedContent: {
                ...current.localizedContent,
                [activeLanguage]: ensureBlocks(current.localizedContent?.[activeLanguage]).map((block, blockIndex) =>
                    blockIndex === index ? nextBlock : block,
                ),
            },
        }))
    }

    async function removeBlock(index) {
        if (operationRef.current) return
        if (!await confirm({ title: tr('Delete block', 'حذف الكتلة'), description: tr('Delete this content block?', 'هل تريد حذف كتلة المحتوى هذه؟'), variant: 'danger' })) return
        setForm((current) => ({
            ...current,
            localizedContent: {
                ...current.localizedContent,
                [activeLanguage]: ensureBlocks(current.localizedContent?.[activeLanguage]).filter((_, blockIndex) => blockIndex !== index),
            },
        }))
    }

    async function handleCancel() {
        if (operationRef.current) return
        if (
            !isDirty ||
            await confirm({ description: tr('Discard all unsaved marketplace page changes?', 'هل تريد تجاهل كل تغييرات صفحة السوق غير المحفوظة؟') })
        ) {
            const acceptedForm = page ? fromPageResponse(page) : createEmptyForm()
            setForm(acceptedForm)
            setAcceptedFingerprint(getMarketplaceFormFingerprint(acceptedForm))
            setError('')
            setNotice('')
        }
    }

    async function handleRefresh() {
        if (operationRef.current) return
        if (
            isDirty &&
            !await confirm({
                description: tr(
                    'Discard unsaved changes and load the latest marketplace page?',
                    'هل تريد تجاهل التغييرات غير المحفوظة وتحميل أحدث إصدار لصفحة السوق؟',
                ),
            })
        ) return
        if (isNewPage) {
            handleCancel()
            return
        }
        setIsLoading(true)
        setError('')
        try {
            adoptMarketplaceResponse(await marketplaceCmsService.getPage(pageId))
        } catch (err) {
            setError(authoringFailure(err))
        } finally {
            setIsLoading(false)
        }
    }

    if (isLoading) {
        return (
            <SuperAdminPage>
                <LoadingState label={tr('Loading marketplace page…', 'جارٍ تحميل صفحة السوق…')} />
            </SuperAdminPage>
        )
    }

    if (!isNewPage && !page && !isDirty) {
        return (
            <SuperAdminPage>
                <PageHeader
                    eyebrow={tr('Super Admin · Marketplace content', 'الإدارة العليا · محتوى السوق')}
                    title={tr('Marketplace page editor', 'محرر صفحات السوق')}
                    description={tr(
                        'Edit a global marketplace CMS page, review its bilingual content, then save and publish it to the marketplace domain.',
                        'حرّر صفحة CMS عامة للسوق، وراجع محتواها ثنائي اللغة، ثم احفظها وانشرها على نطاق السوق.',
                    )}
                />
                <ErrorState
                    title={tr('Marketplace page editor unavailable', 'محرر صفحات السوق غير متاح')}
                    description={error || tr('No authoritative page response was loaded.', 'لم يُحمَّل أي رد معتمد للصفحة.')}
                    onRetry={handleRefresh}
                    retryLabel={tr('Retry loading', 'إعادة محاولة التحميل')}
                />
            </SuperAdminPage>
        )
    }

    const activeLanguageLabel = activeLanguage === 'ar'
        ? tr('Arabic', 'العربية')
        : tr('English', 'الإنجليزية')
    const pageTitle = isNewPage
        ? tr('Create marketplace page', 'إنشاء صفحة سوق')
        : getLocalizedValue(form.titleLocalized, activeLanguage)
            || form.titleLocalized.en
            || tr('Edit marketplace page', 'تحرير صفحة السوق')

    return (
        <SuperAdminPage>
            <PageHeader
                eyebrow={tr('Super Admin · Marketplace content', 'الإدارة العليا · محتوى السوق')}
                title={pageTitle}
                description={
                    isNewPage
                        ? tr(
                              'Create a global marketplace page. Save the draft before previewing or publishing.',
                              'أنشئ صفحة سوق عامة. احفظ المسودة قبل المعاينة أو النشر.',
                          )
                        : page?.publishedAtUtc
                            ? tr(
                                  `Last published ${formatDateTime(page.publishedAtUtc)}.`,
                                  `آخر نشر ${formatDateTime(page.publishedAtUtc)}.`,
                              )
                            : tr('This page is still in draft mode.', 'هذه الصفحة ما زالت في وضع المسودة.')
                }
                actions={
                    <div className="flex flex-wrap items-center gap-2">
                        <button
                            type="button"
                            disabled={isSaving || isPublishing}
                            onClick={async () => {
                                if (operationRef.current) return
                                if (
                                    !isDirty ||
                                    await confirm({
                                        description: tr(
                                            'Leave this page and discard unsaved changes?',
                                            'هل تريد مغادرة هذه الصفحة وتجاهل التغييرات غير المحفوظة؟',
                                        ),
                                    })
                                ) {
                                    navigate('/super-admin/marketplace/pages')
                                }
                            }}
                            className={`${saButton.ghost} border border-slate-200 bg-white`}
                            title={tr('Back to pages', 'العودة إلى الصفحات')}
                        >
                            <ArrowLeft size={16} />
                            {tr('Back', 'رجوع')}
                        </button>
                        <ActionButton onClick={handleCancel} disabled={!isDirty || isSaving || isPublishing} icon={RotateCcw}>
                            {tr('Cancel', 'إلغاء')}
                        </ActionButton>
                        <ActionButton onClick={handleRefresh} disabled={isSaving || isPublishing} icon={RefreshCcw}>
                            {tr('Refresh', 'تحديث')}
                        </ActionButton>
                        <ActionButton onClick={handlePreview} disabled={isNewPage} icon={Eye}>
                            {tr('Preview', 'معاينة')}
                        </ActionButton>
                        <ActionButton
                            onClick={handleSave}
                            disabled={isSaving || isPublishing || !isDirty}
                            icon={isSaving ? Loader2 : Save}
                            spinning={isSaving}
                        >
                            {tr('Save Draft', 'حفظ المسودة')}
                        </ActionButton>
                        <ActionButton
                            onClick={handlePublish}
                            disabled={isPublishing || isSaving}
                            icon={isPublishing ? Loader2 : Globe}
                            primary
                            spinning={isPublishing}
                        >
                            {tr('Publish', 'نشر')}
                        </ActionButton>
                    </div>
                }
            />

            <div className="grid gap-3 sm:grid-cols-2 xl:grid-cols-4">
                <KpiCard
                    label={tr('Draft state', 'حالة المسودة')}
                    value={isDirty ? tr('Unsaved changes', 'تغييرات غير محفوظة') : tr('Draft in sync', 'المسودة متزامنة')}
                    hint={
                        isDirty
                            ? tr('Save the draft before previewing or publishing.', 'احفظ المسودة قبل المعاينة أو النشر.')
                            : tr('Local fields match the last accepted backend response.', 'الحقول المحلية تطابق آخر استجابة معتمدة من الخادم.')
                    }
                    tone={isDirty ? 'amber' : 'emerald'}
                    icon={<Save className="h-4 w-4" />}
                />
                <KpiCard
                    label={tr('Content mode', 'وضع المحتوى')}
                    value={form.contentType === CONTENT_TYPES.Blocks ? tr('Blocks', 'كتل') : tr('Rich text', 'نص منسق')}
                    hint={tr(
                        'Saved with the page and applied by the marketplace renderer.',
                        'يُحفظ مع الصفحة ويطبقه عارض السوق.',
                    )}
                    tone="sky"
                    icon={<FileText className="h-4 w-4" />}
                />
                <KpiCard
                    label={tr('Editing language', 'لغة التحرير')}
                    value={activeLanguageLabel}
                    hint={tr(
                        'Independent from the admin interface language.',
                        'مستقلة عن لغة واجهة الإدارة.',
                    )}
                    tone="violet"
                    icon={<Languages className="h-4 w-4" />}
                />
                <KpiCard
                    label={tr('Live publication', 'النشر المباشر')}
                    value={page?.publishedAtUtc ? formatDate(page.publishedAtUtc) : tr('Not published', 'غير منشورة')}
                    hint={
                        page?.publishedAtUtc
                            ? tr(
                                  `Published ${formatDateTime(page.publishedAtUtc)} · resolves at the marketplace root.`,
                                  `نُشرت ${formatDateTime(page.publishedAtUtc)} · تُحل على جذر السوق.`,
                              )
                            : tr('Publish creates the first live version.', 'النشر ينشئ أول نسخة مباشرة.')
                    }
                    tone={page?.publishedAtUtc ? 'emerald' : 'slate'}
                    icon={<Globe className="h-4 w-4" />}
                />
            </div>

            <GuidePanel
                guide={{
                    en: {
                        title: 'Marketplace page editor guide',
                        purpose:
                            'Edits one global marketplace CMS page: its slug, bilingual title and SEO fields, and its rich-text or block content. Saving stores a draft; publishing makes the page live at the marketplace root.',
                        when:
                            'Use it to create About, Privacy, Terms, or campaign pages, correct legal copy, translate content into Arabic, or publish an approved draft.',
                        steps: [
                            'Enter the page title and a unique slug; the slug resolves at the marketplace root, for example /about.',
                            'Choose the content mode: rich text for prose, or blocks for structured text, image, and FAQ sections.',
                            'Switch the content language tab and edit the English or Arabic fields independently.',
                            'Use Auto Translate on the Arabic tab to draft from English, then review the result before saving.',
                            'Save the draft, then preview it with a secure preview link.',
                            'Publish only after the draft is saved and reviewed; publishing replaces the live page.',
                        ],
                        statuses: [
                            { label: 'Draft in sync', description: 'Local fields match the last accepted backend response.' },
                            { label: 'Unsaved edits', description: 'Local edits are not saved; preview and publish are blocked until saved.' },
                            { label: 'Not published', description: 'The page has no live version yet; the first publish creates one.' },
                            { label: 'Rich text / Blocks', description: 'The page body is a rich-text document or structured blocks.' },
                            { label: 'EN / AR tabs', description: 'Choose which content language you are editing; the admin UI language is separate.' },
                        ],
                        safety: [
                            'Saving never changes the live marketplace page; only Publish does.',
                            'Preview refuses to run while unsaved changes exist, so it cannot silently save or bump a version.',
                            'Publishing uses the latest version evidence and refreshes after a concurrency conflict rather than overwriting another session.',
                            'Leaving, refreshing, cancelling, or deleting a block with unsaved changes asks for confirmation.',
                            'Auto Translate fills the Arabic draft only; review it before saving or publishing.',
                            'Tenant storefront CMS pages are separate and are not modified by this editor.',
                        ],
                    },
                    ar: {
                        title: 'دليل محرر صفحات السوق',
                        purpose:
                            'يحرّر صفحة CMS عامة واحدة للسوق: معرّفها وعنوانها ثنائي اللغة وحقول SEO ومحتواها النصي أو المكوّن من كتل. الحفظ يخزّن مسودة، والنشر يجعل الصفحة مباشرة على جذر السوق.',
                        when:
                            'استخدمه لإنشاء صفحات من نحن أو الخصوصية أو الشروط أو الحملات، أو تصحيح نص قانوني، أو الترجمة إلى العربية، أو نشر مسودة معتمدة.',
                        steps: [
                            'أدخل عنوان الصفحة ومعرّفًا فريدًا؛ ويُحل المعرّف على جذر السوق، مثل /about.',
                            'اختر وضع المحتوى: نص منسق للنصوص الطويلة، أو كتل لأقسام النص والصورة والأسئلة الشائعة.',
                            'بدّل تبويب لغة المحتوى وحرّر الحقول الإنجليزية أو العربية بشكل مستقل.',
                            'استخدم الترجمة التلقائية في تبويب العربية لصياغة المحتوى من الإنجليزية، ثم راجع النتيجة قبل الحفظ.',
                            'احفظ المسودة، ثم عاينها عبر رابط معاينة آمن.',
                            'انشر فقط بعد حفظ المسودة ومراجعتها؛ فالنشر يستبدل الصفحة المباشرة.',
                        ],
                        statuses: [
                            { label: 'المسودة متزامنة', description: 'الحقول المحلية تطابق آخر استجابة معتمدة من الخادم.' },
                            { label: 'تغييرات غير محفوظة', description: 'التعديلات المحلية غير محفوظة؛ وتُمنع المعاينة والنشر حتى الحفظ.' },
                            { label: 'غير منشورة', description: 'لا تملك الصفحة نسخة مباشرة بعد؛ والنشر الأول ينشئها.' },
                            { label: 'نص منسق / كتل', description: 'محتوى الصفحة مستند نص منسق أو كتل منظمة.' },
                            { label: 'تبويبات EN / AR', description: 'اختر لغة المحتوى التي تحررها؛ ولغة واجهة الإدارة منفصلة.' },
                        ],
                        safety: [
                            'الحفظ لا يغيّر صفحة السوق المباشرة؛ النشر وحده يفعل ذلك.',
                            'المعاينة ترفض العمل عند وجود تغييرات غير محفوظة، فلا تحفظ أو ترفع إصدارًا بصمت.',
                            'النشر يستخدم أحدث إثبات إصدار ويعيد التحميل بعد تعارض التزامن بدل الكتابة فوق جلسة أخرى.',
                            'المغادرة أو التحديث أو الإلغاء أو حذف كتلة مع تغييرات غير محفوظة تطلب تأكيدًا.',
                            'الترجمة التلقائية تملأ المسودة العربية فقط؛ راجعها قبل الحفظ أو النشر.',
                            'صفحات CMS الخاصة بمتاجر المستأجرين منفصلة ولا يعدّلها هذا المحرر.',
                        ],
                    },
                }}
            />

            <div className="flex flex-col gap-2 rounded-2xl border border-slate-200 bg-white p-3 shadow-sm sm:flex-row sm:items-center sm:justify-between">
                <LanguageTabs value={activeLanguage} onChange={setActiveLanguage} />
                <p className="text-xs leading-5 text-slate-500">
                    {tr(
                        'Content language is independent from the admin interface language. English and Arabic fields are stored separately.',
                        'لغة المحتوى مستقلة عن لغة واجهة الإدارة. وتُخزَّن الحقول الإنجليزية والعربية بشكل منفصل.',
                    )}
                </p>
            </div>

            {error ? (
                <InlineNotice tone="rose" title={tr('Action not completed', 'لم يكتمل الإجراء')}>
                    <span dir="auto" role="alert">{error}</span>
                </InlineNotice>
            ) : null}

            {notice ? (
                <InlineNotice tone="emerald" title={tr('Done', 'تم')}>
                    {notice}
                </InlineNotice>
            ) : null}

            <fieldset disabled={isSaving || isPublishing || Boolean(translatingField)} className="min-w-0 border-0 p-0">
            <div className="grid gap-4 xl:grid-cols-[340px_minmax(0,1fr)]">
                <div className="space-y-4">
                    <Panel title={tr('Page details', 'تفاصيل الصفحة')}>
                        <TranslatableFieldHeader
                            label={tr('Title', 'العنوان')}
                            activeLanguage={activeLanguage}
                            isTranslating={translatingField === 'title'}
                            onTranslate={() => handleAutoTranslate('title')}
                        />
                        <LabeledInput
                            label={null}
                            value={getLocalizedValue(form.titleLocalized, activeLanguage)}
                            onChange={(value) => patchLocalizedField('titleLocalized', value)}
                        />
                        <LabeledInput
                            label={tr('Slug', 'المعرّف')}
                            value={form.slug}
                            onChange={(value) => patchForm({ slug: normalizeSlugInput(value) })}
                            helpText={tr(
                                'This resolves at the marketplace root, for example /about',
                                'يُحل هذا على جذر السوق، مثل /about',
                            )}
                        />
                        <LabeledSelect
                            label={tr('Content mode', 'وضع المحتوى')}
                            value={form.contentType}
                            options={[CONTENT_TYPES.RichText, CONTENT_TYPES.Blocks]}
                            optionLabels={{
                                [CONTENT_TYPES.RichText]: tr('Rich text', 'نص منسق'),
                                [CONTENT_TYPES.Blocks]: tr('Blocks', 'كتل'),
                            }}
                            onChange={(value) => patchForm({
                                contentType: value,
                                localizedContent: {
                                    en: value === CONTENT_TYPES.Blocks
                                        ? ensureBlocks(form.localizedContent.en)
                                        : ensureRichTextRaw(form.localizedContent.en),
                                    ar: value === CONTENT_TYPES.Blocks
                                        ? ensureBlocks(form.localizedContent.ar)
                                        : ensureRichTextRaw(form.localizedContent.ar),
                                },
                            })}
                        />
                    </Panel>

                    <Panel title={tr('SEO settings', 'إعدادات SEO')}>
                        <TranslatableFieldHeader
                            label={tr('Meta title', 'عنوان الميتا')}
                            activeLanguage={activeLanguage}
                            isTranslating={translatingField === 'metaTitle'}
                            onTranslate={() => handleAutoTranslate('metaTitle')}
                        />
                        <LabeledInput
                            label={null}
                            value={getLocalizedValue(form.seo.metaTitleLocalized, activeLanguage)}
                            onChange={(value) => patchSeo('metaTitleLocalized', value)}
                        />
                        <TranslatableFieldHeader
                            label={tr('Meta description', 'وصف الميتا')}
                            activeLanguage={activeLanguage}
                            isTranslating={translatingField === 'metaDescription'}
                            onTranslate={() => handleAutoTranslate('metaDescription')}
                        />
                        <LabeledTextarea
                            label={null}
                            value={getLocalizedValue(form.seo.metaDescriptionLocalized, activeLanguage)}
                            onChange={(value) => patchSeo('metaDescriptionLocalized', value)}
                            rows={4}
                        />
                        <LabeledInput
                            label={tr('Open Graph image URL', 'عنوان صورة Open Graph')}
                            value={form.seo.openGraphImageUrl}
                            onChange={(value) => setForm((current) => ({
                                ...current,
                                seo: {
                                    ...current.seo,
                                    openGraphImageUrl: value,
                                },
                            }))}
                        />
                    </Panel>
                </div>

                <Panel title={tr('Content', 'المحتوى')}>
                    <p className="text-sm leading-6 text-slate-500">
                        {form.contentType === CONTENT_TYPES.RichText
                            ? tr(
                                  'Use the rich text editor and preview the result before publishing.',
                                  'استخدم محرر النص المنسق وعاين النتيجة قبل النشر.',
                              )
                            : tr(
                                  'Build the page from blocks so it stays ready for a future marketplace page builder.',
                                  'ابنِ الصفحة من كتل لتبقى جاهزة لمنشئ صفحات السوق المستقبلي.',
                              )}
                    </p>

                    <div className="flex flex-wrap items-center justify-between gap-3 rounded-xl border border-slate-200 bg-slate-50 px-3 py-2.5">
                        <span className="text-sm font-medium text-slate-700">
                            {tr(`Editing ${activeLanguageLabel} content`, `تحرير المحتوى ${activeLanguageLabel}`)}
                        </span>
                        <button
                            type="button"
                            onClick={() => handleAutoTranslate('content')}
                            disabled={activeLanguage !== 'ar' || translatingField === 'content'}
                            className="inline-flex items-center gap-2 rounded-lg border border-slate-200 bg-white px-2.5 py-1.5 text-xs font-semibold text-slate-700 transition hover:border-slate-300 hover:bg-slate-50 disabled:opacity-50"
                        >
                            {translatingField === 'content' ? <Loader2 size={14} className="animate-spin" /> : <Sparkles size={14} />}
                            {tr('Auto Translate', 'ترجمة تلقائية')}
                        </button>
                    </div>

                    {form.contentType === CONTENT_TYPES.Blocks ? (
                        <div className="flex flex-wrap gap-2">
                            {[
                                { type: 'text', label: tr('Text', 'نص') },
                                { type: 'image', label: tr('Image', 'صورة') },
                                { type: 'faq', label: tr('FAQ', 'الأسئلة الشائعة') },
                            ].map(({ type, label }) => (
                                <button
                                    key={type}
                                    type="button"
                                    onClick={() => addBlock(type)}
                                    className={saButton.secondary}
                                >
                                    <Plus size={14} />
                                    {label}
                                </button>
                            ))}
                        </div>
                    ) : null}

                    {form.contentType === CONTENT_TYPES.RichText ? (
                        <RichTextEditor
                            value={ensureRichText(form.localizedContent[activeLanguage])}
                            onChange={patchLocalizedContent}
                            dir={activeLanguage === 'ar' ? 'rtl' : 'ltr'}
                        />
                    ) : (
                        <BlocksEditor
                            blocks={ensureBlocks(form.localizedContent[activeLanguage])}
                            onUpdate={updateBlock}
                            onRemove={removeBlock}
                        />
                    )}
                </Panel>
            </div>
            </fieldset>
        </SuperAdminPage>
    )
}

function fromPageResponse(page) {
    const workingVersion = page?.draft || page?.published

    if (!workingVersion) {
        return createEmptyForm()
    }

    return {
        slug: workingVersion.slug || '',
        titleLocalized: normalizeLocalizedField(workingVersion.titleLocalized, workingVersion.title),
        contentType: workingVersion.contentType || CONTENT_TYPES.RichText,
        localizedContent: normalizeLocalizedContent(
            workingVersion.contentType || CONTENT_TYPES.RichText,
            workingVersion.localizedContent,
            workingVersion.content,
        ),
        seo: {
            metaTitleLocalized: normalizeLocalizedField(
                workingVersion.seo?.metaTitleLocalized,
                workingVersion.seo?.metaTitle,
            ),
            metaDescriptionLocalized: normalizeLocalizedField(
                workingVersion.seo?.metaDescriptionLocalized,
                workingVersion.seo?.metaDescription,
            ),
            openGraphImageUrl: workingVersion.seo?.openGraphImageUrl || '',
        },
    }
}

function toRequestPayload(form) {
    const englishContent = normalizeContentPayload(form.contentType, form.localizedContent.en)
    const arabicContent = normalizeContentPayload(form.contentType, form.localizedContent.ar)

    return {
        slug: form.slug.trim(),
        title: form.titleLocalized.en.trim(),
        titleLocalized: trimLocalizedField(form.titleLocalized),
        contentType: form.contentType,
        content: englishContent,
        localizedContent: {
            en: englishContent,
            ...(hasTranslatableContent(arabicContent) ? { ar: arabicContent } : {}),
        },
        seo: {
            metaTitle: emptyToNull(form.seo.metaTitleLocalized.en),
            metaTitleLocalized: trimLocalizedField(form.seo.metaTitleLocalized),
            metaDescription: emptyToNull(form.seo.metaDescriptionLocalized.en),
            metaDescriptionLocalized: trimLocalizedField(form.seo.metaDescriptionLocalized),
            openGraphImageUrl: emptyToNull(form.seo.openGraphImageUrl),
        },
    }
}

export function getMarketplaceFormFingerprint(form) {
    return JSON.stringify(toRequestPayload(form))
}

function emptyToNull(value) {
    return value && value.trim() ? value.trim() : null
}

function normalizeSlugInput(value) {
    return value.toLowerCase().replace(/[^a-z0-9-]+/g, '-').replace(/--+/g, '-').replace(/^-|-$/g, '')
}

function normalizeLocalizedField(localizedField, fallbackValue = '') {
    return {
        en: localizedField?.en || fallbackValue || '',
        ar: localizedField?.ar || '',
    }
}

function normalizeLocalizedContent(contentType, localizedContent, fallbackContent) {
    return {
        en: normalizeContentValue(contentType, localizedContent?.en ?? fallbackContent),
        ar: normalizeContentValue(contentType, localizedContent?.ar),
    }
}

function normalizeContentValue(contentType, value) {
    if (contentType === CONTENT_TYPES.Blocks) {
        return ensureBlocks(value)
    }

    return ensureRichTextRaw(value)
}

function normalizeContentPayload(contentType, value) {
    return contentType === CONTENT_TYPES.Blocks
        ? ensureBlocks(value)
        : ensureRichText(value)
}

function ensureBlocks(value) {
    return Array.isArray(value) ? value : []
}

function ensureRichTextRaw(value) {
    return typeof value === 'string' ? value : ''
}

function ensureRichText(value) {
    return sanitizeRichTextHtml(ensureRichTextRaw(value))
}

function trimLocalizedField(field) {
    const trimmed = {
        en: (field?.en || '').trim(),
        ar: (field?.ar || '').trim(),
    }

    return trimmed.en || trimmed.ar ? trimmed : null
}

function getLocalizedValue(field, language) {
    return field?.[language] || ''
}

function hasTranslatableContent(value) {
    if (typeof value === 'string') {
        return value.trim().length > 0
    }

    if (Array.isArray(value)) {
        return value.length > 0
    }

    if (value && typeof value === 'object') {
        return Object.keys(value).length > 0
    }

    return false
}

function getTranslationTarget(form, fieldKey, setForm) {
    switch (fieldKey) {
        case 'title':
            return {
                source: form.titleLocalized.en,
                target: form.titleLocalized.ar,
                apply: (translated) => setForm((current) => ({
                    ...current,
                    titleLocalized: {
                        ...current.titleLocalized,
                        ar: translated,
                    },
                })),
            }
        case 'metaTitle':
            return {
                source: form.seo.metaTitleLocalized.en,
                target: form.seo.metaTitleLocalized.ar,
                apply: (translated) => setForm((current) => ({
                    ...current,
                    seo: {
                        ...current.seo,
                        metaTitleLocalized: {
                            ...current.seo.metaTitleLocalized,
                            ar: translated,
                        },
                    },
                })),
            }
        case 'metaDescription':
            return {
                source: form.seo.metaDescriptionLocalized.en,
                target: form.seo.metaDescriptionLocalized.ar,
                apply: (translated) => setForm((current) => ({
                    ...current,
                    seo: {
                        ...current.seo,
                        metaDescriptionLocalized: {
                            ...current.seo.metaDescriptionLocalized,
                            ar: translated,
                        },
                    },
                })),
            }
        case 'content':
            return {
                source: form.localizedContent.en,
                target: form.localizedContent.ar,
                apply: (translated) => setForm((current) => ({
                    ...current,
                    localizedContent: {
                        ...current.localizedContent,
                        ar: translated,
                    },
                })),
            }
        default:
            return null
    }
}

function ActionButton({ children, disabled, icon: Icon, onClick, primary = false, spinning = false }) {
    return (
        <button
            type="button"
            onClick={onClick}
            disabled={disabled}
            className={[
                'inline-flex items-center gap-2 rounded-xl px-4 py-2 text-sm font-medium transition disabled:opacity-50',
                primary
                    ? 'bg-emerald-600 text-white hover:bg-emerald-500'
                    : 'border border-slate-200 bg-white text-slate-700 hover:border-slate-300 hover:bg-slate-50',
            ].join(' ')}
        >
            <Icon size={16} className={spinning ? 'animate-spin' : ''} />
            {children}
        </button>
    )
}

function LanguageTabs({ value, onChange }) {
    const { tr } = useSuperAdminLocale()
    return (
        <div
            className="inline-flex rounded-xl border border-slate-200 bg-slate-50 p-1"
            role="group"
            aria-label={tr('Content language', 'لغة المحتوى')}
        >
            {SUPPORTED_LANGUAGES.map((language) => (
                <button
                    key={language}
                    type="button"
                    onClick={() => onChange(language)}
                    aria-pressed={value === language}
                    title={language === 'ar' ? tr('Edit Arabic content', 'تحرير المحتوى العربي') : tr('Edit English content', 'تحرير المحتوى الإنجليزي')}
                    className={[
                        'rounded-lg px-4 py-1.5 text-sm font-semibold transition',
                        value === language
                            ? 'bg-slate-900 text-white'
                            : 'text-slate-600 hover:bg-white',
                    ].join(' ')}
                >
                    {language.toUpperCase()}
                </button>
            ))}
        </div>
    )
}

function TranslatableFieldHeader({ label, activeLanguage, isTranslating, onTranslate }) {
    const { tr } = useSuperAdminLocale()
    return (
        <div className="flex items-center justify-between gap-3">
            <span className="text-sm font-semibold text-slate-700">
                {label} ({activeLanguage.toUpperCase()})
            </span>
            <button
                type="button"
                onClick={onTranslate}
                disabled={activeLanguage !== 'ar' || isTranslating}
                className="inline-flex items-center gap-1.5 rounded-lg border border-slate-200 bg-white px-2.5 py-1.5 text-[11px] font-semibold uppercase tracking-wide text-slate-600 transition hover:border-slate-300 hover:bg-slate-50 disabled:opacity-50"
            >
                {isTranslating ? <Loader2 size={14} className="animate-spin" /> : <Sparkles size={14} />}
                {tr('Auto Translate', 'ترجمة تلقائية')}
            </button>
        </div>
    )
}

function Panel({ children, title }) {
    return (
        <section className="rounded-2xl border border-slate-200 bg-white p-4 shadow-sm sm:p-5">
            <h2 className="text-base font-semibold text-slate-950">{title}</h2>
            <div className="mt-4 space-y-4">{children}</div>
        </section>
    )
}

function LabeledInput({ label, value, onChange, helpText }) {
    return (
        <label className="block">
            {label ? <span className="mb-1.5 block text-sm font-medium text-slate-700">{label}</span> : null}
            <input
                value={value}
                dir="auto"
                onChange={(event) => onChange(event.target.value)}
                className={saInput}
            />
            {helpText ? <span className="mt-1.5 block text-xs text-slate-400">{helpText}</span> : null}
        </label>
    )
}

function LabeledTextarea({ label, value, onChange, rows = 4 }) {
    return (
        <label className="block">
            {label ? <span className="mb-1.5 block text-sm font-medium text-slate-700">{label}</span> : null}
            <textarea
                value={value}
                dir="auto"
                onChange={(event) => onChange(event.target.value)}
                rows={rows}
                className={`${saInput} resize-y`}
            />
        </label>
    )
}

function LabeledSelect({ label, value, options, onChange, optionLabels = {} }) {
    return (
        <label className="block">
            <span className="mb-1.5 block text-sm font-medium text-slate-700">{label}</span>
            <select
                value={value}
                onChange={(event) => onChange(event.target.value)}
                className={saInput}
            >
                {options.map((option) => (
                    <option key={option} value={option}>{optionLabels[option] ?? option}</option>
                ))}
            </select>
        </label>
    )
}

function RichTextEditor({ value, onChange, dir = 'auto' }) {
    const { tr } = useSuperAdminLocale()
    const editorRef = useRef(null)
    const isEditingRef = useRef(false)
    const sanitizedValue = useMemo(() => ensureRichText(value), [value])

    useEffect(() => {
        // Only sync from props when not actively editing to avoid overwriting mid-edit
        if (editorRef.current && editorRef.current.innerHTML !== sanitizedValue && !isEditingRef.current) {
            editorRef.current.innerHTML = sanitizedValue
        }
    }, [sanitizedValue])

    function runCommand(command, commandValue) {
        if (!editorRef.current) {
            return
        }

        editorRef.current.focus()
        document.execCommand(command, false, commandValue)
        onChange(ensureRichText(editorRef.current.innerHTML))
    }

    function handleOnBlur() {
        if (!editorRef.current) {
            return
        }

        isEditingRef.current = false

        const raw = editorRef.current.innerHTML
        const sanitizedNextValue = ensureRichText(raw)
        
        // Normalize the editor DOM if sanitization changed the content
        if (editorRef.current.innerHTML !== sanitizedNextValue) {
            editorRef.current.innerHTML = sanitizedNextValue
        }
        
        // Trigger onChange whenever the raw DOM content differs from stored value
        // OR when sanitization changed the content (nextValue !== value)
        // This ensures form state is updated even if sanitization normalizes the content
        if (raw !== sanitizedValue || sanitizedNextValue !== sanitizedValue) {
            onChange(sanitizedNextValue)
        }
    }

    return (
        <div className="overflow-hidden rounded-xl border border-slate-200">
            <div className="flex flex-wrap gap-1.5 border-b border-slate-200 bg-slate-50 p-2.5">
                {[
                    { command: 'bold', label: 'B', name: tr('Bold', 'غامق') },
                    { command: 'italic', label: 'I', name: tr('Italic', 'مائل') },
                    { command: 'insertUnorderedList', label: 'UL', name: tr('Bulleted list', 'قائمة نقطية') },
                ].map(({ command, label, name }) => (
                    <button
                        key={command}
                        type="button"
                        onClick={() => runCommand(command)}
                        aria-label={name}
                        title={name}
                        className="rounded-lg border border-slate-200 bg-white px-3 py-1.5 text-sm font-medium text-slate-700 transition hover:border-slate-300 hover:bg-slate-50"
                    >
                        {label}
                    </button>
                ))}
                <button
                    type="button"
                    onClick={() => runCommand('formatBlock', '<h2>')}
                    aria-label={tr('Heading 2', 'عنوان 2')}
                    title={tr('Heading 2', 'عنوان 2')}
                    className="rounded-lg border border-slate-200 bg-white px-3 py-1.5 text-sm font-medium text-slate-700 transition hover:border-slate-300 hover:bg-slate-50"
                >
                    H2
                </button>
            </div>
            <div
                ref={editorRef}
                contentEditable
                dir={dir}
                suppressContentEditableWarning
                onFocus={() => {
                    isEditingRef.current = true
                }}
                onInput={() => {
                    // Input is handled, but sanitization is deferred to onBlur to avoid lag
                    // This keeps the editor responsive while typing
                }}
                onBlur={handleOnBlur}
                className="min-h-[360px] px-5 py-4 text-sm leading-7 text-slate-700 outline-none"
            />
        </div>
    )
}

function BlocksEditor({ blocks, onUpdate, onRemove }) {
    const { tr } = useSuperAdminLocale()
    const blockLabels = {
        text: tr('Text block', 'كتلة نص'),
        image: tr('Image block', 'كتلة صورة'),
        faq: tr('FAQ block', 'كتلة أسئلة شائعة'),
    }
    const alignmentLabels = {
        left: tr('Left', 'يسار'),
        center: tr('Center', 'وسط'),
        right: tr('Right', 'يمين'),
    }

    if (!blocks.length) {
        return (
            <div className="rounded-xl border border-dashed border-slate-300 bg-slate-50 px-6 py-12 text-center text-sm text-slate-500">
                {tr(
                    'Add your first block to start building the page.',
                    'أضف أول كتلة لبدء بناء الصفحة.',
                )}
            </div>
        )
    }

    return (
        <div className="space-y-3">
            {blocks.map((block, index) => (
                <div key={`${block.type}-${index}`} className="rounded-xl border border-slate-200 p-3.5">
                    <div className="mb-3 flex items-center justify-between gap-2">
                        <span className="text-sm font-semibold text-slate-800">
                            {blockLabels[block.type] || block.type}
                        </span>
                        <button
                            type="button"
                            onClick={() => onRemove(index)}
                            aria-label={tr('Delete block', 'حذف الكتلة')}
                            title={tr('Delete block', 'حذف الكتلة')}
                            className="rounded-lg border border-rose-200 bg-white p-1.5 text-rose-700 transition hover:bg-rose-50"
                        >
                            <Trash2 size={14} />
                        </button>
                    </div>

                    {block.type === 'text' ? (
                        <div className="space-y-3">
                            <LabeledTextarea
                                label={tr('Text', 'النص')}
                                value={block.data?.text || ''}
                                onChange={(value) => onUpdate(index, { ...block, data: { ...block.data, text: value } })}
                                rows={5}
                            />
                            <LabeledSelect
                                label={tr('Alignment', 'المحاذاة')}
                                value={block.data?.alignment || 'left'}
                                options={['left', 'center', 'right']}
                                optionLabels={alignmentLabels}
                                onChange={(value) => onUpdate(index, { ...block, data: { ...block.data, alignment: value } })}
                            />
                        </div>
                    ) : null}

                    {block.type === 'image' ? (
                        <div className="space-y-3">
                            <LabeledInput
                                label={tr('Image URL', 'عنوان الصورة')}
                                value={block.data?.imageUrl || ''}
                                onChange={(value) => onUpdate(index, { ...block, data: { ...block.data, imageUrl: value } })}
                            />
                            <LabeledInput
                                label={tr('Alt text', 'النص البديل')}
                                value={block.data?.alt || ''}
                                onChange={(value) => onUpdate(index, { ...block, data: { ...block.data, alt: value } })}
                            />
                            <LabeledInput
                                label={tr('Caption', 'التعليق')}
                                value={block.data?.caption || ''}
                                onChange={(value) => onUpdate(index, { ...block, data: { ...block.data, caption: value } })}
                            />
                        </div>
                    ) : null}

                    {block.type === 'faq' ? (
                        <LabeledTextarea
                            label={tr('FAQ items JSON', 'عناصر الأسئلة الشائعة JSON')}
                            value={JSON.stringify(block.data?.items || [], null, 2)}
                            onChange={(value) => {
                                try {
                                    const items = JSON.parse(value)
                                    onUpdate(index, { ...block, data: { ...block.data, items } })
                                } catch {
                                    // Invalid JSON must not replace the last accepted FAQ items.
                                    return
                                }
                            }}
                            rows={8}
                        />
                    ) : null}
                </div>
            ))}
        </div>
    )
}

export default MarketplaceCmsPageEditor
