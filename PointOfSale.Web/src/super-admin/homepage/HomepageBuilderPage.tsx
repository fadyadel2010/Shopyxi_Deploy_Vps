import { useEffect, useMemo, useState } from 'react'
import {
    closestCenter,
    DndContext,
    DragOverlay,
    KeyboardSensor,
    PointerSensor,
    useSensor,
    useSensors,
    type DragEndEvent,
    type DragStartEvent,
} from '@dnd-kit/core'
import {
    SortableContext,
    arrayMove,
    sortableKeyboardCoordinates,
    useSortable,
    verticalListSortingStrategy,
} from '@dnd-kit/sortable'
import { CSS } from '@dnd-kit/utilities'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import {
    AlertTriangle,
    Eye,
    EyeOff,
    GripVertical,
    LayoutTemplate,
    Loader2,
    Plus,
    RefreshCw,
    Save,
    Sparkles,
    Trash2,
} from 'lucide-react'

import { useToast } from '../../components/Toast'
import { useSuperAdminLocale } from '../i18n/useSuperAdminLocale'
import {
    EmptyState,
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
import HomepageBuilderPreview from './HomepageBuilderPreview'
import {
    generateHomepageStarterTemplate,
    getHomepageSchemas,
    getHomepagePreviewStoreConfig,
    getHomepageSections,
    getHomepageTenants,
    isHomepageWorkspaceConflict,
    publishHomepageSections,
    saveHomepageDraft,
    translateMarketplaceContent,
    type HomepageSectionsResult,
    type HomepageSectionFieldSchema,
    type HomepageSectionSchema,
    type HomepageSectionType,
} from './homepageService'
import {
    buildSaveHomepageDraftPayload,
    createBuilderSection,
    createBuilderSectionsFingerprint,
    formatSectionType,
    getFieldDisplayValue,
    getLocalizedFieldDisplayValue,
    getSectionSchema,
    getSectionTitle,
    getSectionTitleForLanguage,
    hasValidationIssues,
    hydrateBuilderSections,
    normalizeBuilderSectionsOrder,
    updateLocalizedFieldValue,
    validateBuilderSections,
    type HomepageBuilderSection,
} from './homepageBuilderUtils'

const homepageSchemasQueryKey = ['super-admin', 'homepage', 'schemas'] as const
const homepageTenantsQueryKey = ['super-admin', 'homepage', 'tenants'] as const
const homepageSectionsQueryKey = (tenantId: string | null, mode: 'preview' | 'published') =>
    ['super-admin', 'homepage', 'sections', tenantId ?? 'global', mode] as const
const homepagePreviewConfigQueryKey = (tenantId: string | null) =>
    ['super-admin', 'homepage', 'preview-config', tenantId ?? 'default'] as const

type ScopeMode = 'global' | 'tenant'

function getErrorMessage(error: unknown, fallback: string): string {
    return error instanceof Error && error.message ? error.message : fallback
}

function getLatestPublishedTimestamp(sections: Array<{ publishedAt?: string | null }>): string | null {
    const timestamps = sections
        .map((section) => section.publishedAt)
        .filter((value): value is string => Boolean(value))
        .map((value) => new Date(value))
        .filter((value) => !Number.isNaN(value.getTime()))
        .sort((left, right) => right.getTime() - left.getTime())

    return timestamps[0]?.toISOString() ?? null
}

function ScopeButton({
    disabled,
    isActive,
    label,
    description,
    onClick,
}: {
    disabled?: boolean
    isActive: boolean
    label: string
    description: string
    onClick: () => void
}) {
    return (
        <button
            type="button"
            onClick={onClick}
            disabled={disabled}
            className={[
                'rounded-xl border px-4 py-3 text-start transition',
                isActive
                    ? 'border-sky-400/40 bg-sky-500/[0.12] text-slate-950 shadow-[0_24px_50px_-36px_rgba(14,165,233,0.75)]'
                    : 'border-slate-200 bg-white text-slate-600 hover:border-slate-300 hover:bg-slate-50',
                disabled ? 'cursor-not-allowed opacity-60' : '',
            ].join(' ')}
        >
            <p className="text-sm font-semibold">{label}</p>
            <p className="mt-1 text-xs leading-5 text-slate-500">{description}</p>
        </button>
    )
}

function SchemaFieldInput({
    disabled,
    field,
    value,
    onChange,
}: {
    disabled?: boolean
    field: HomepageSectionFieldSchema
    value: string
    onChange: (value: string) => void
}) {
    const { tr } = useSuperAdminLocale()
    const baseClassName = `${saInput} bg-white`
    const placeholder = tr(`Enter ${field.label.toLowerCase()}`, `أدخل ${field.label}`)

    if (field.kind === 'textarea' || field.kind === 'guid-list') {
        return (
            <textarea
                value={value}
                onChange={(event) => onChange(event.target.value)}
                disabled={disabled}
                rows={field.kind === 'guid-list' ? 6 : 4}
                className={`${baseClassName} resize-y`}
                placeholder={
                    field.kind === 'guid-list'
                        ? tr('One GUID per line or comma-separated', 'معرّف GUID واحد في كل سطر أو مفصولًا بفواصل')
                        : placeholder
                }
            />
        )
    }

    if (field.kind === 'select') {
        return (
            <select disabled={disabled} value={value} onChange={(event) => onChange(event.target.value)} className={baseClassName}>
                {field.options.map((option) => (
                    <option key={option} value={option}>
                        {option}
                    </option>
                ))}
            </select>
        )
    }

    if (field.kind === 'number') {
        return (
            <input
                type="number"
                min="1"
                step="1"
                value={value}
                onChange={(event) => onChange(event.target.value)}
                disabled={disabled}
                className={baseClassName}
                placeholder={placeholder}
            />
        )
    }

    return (
        <input
            type="text"
            value={value}
            onChange={(event) => onChange(event.target.value)}
            disabled={disabled}
            className={baseClassName}
            placeholder={placeholder}
        />
    )
}

function shouldDisplaySchemaField(
    field: HomepageSectionFieldSchema,
    sectionData: Record<string, unknown>,
): boolean {
    if (field.kind !== 'guid-list') {
        return true
    }

    const source = typeof sectionData.source === 'string' ? sectionData.source.trim().toLowerCase() : 'all'
    return source === 'curated'
}

function SortableSectionItem({
    section,
    schema,
    isSelected,
    hasIssues,
    onSelect,
    onRemove,
}: {
    section: HomepageBuilderSection
    schema: HomepageSectionSchema | null
    isSelected: boolean
    hasIssues: boolean
    onSelect: () => void
    onRemove: () => void
}) {
    const { tr } = useSuperAdminLocale()
    const { attributes, listeners, setNodeRef, transform, transition, isDragging } = useSortable({
        id: section.clientId,
    })

    const style = {
        transform: CSS.Transform.toString(transform),
        transition,
    }

    return (
        <div
            ref={setNodeRef}
            style={style}
            className={[
                'rounded-xl border bg-white p-3 shadow-sm transition',
                isSelected
                    ? 'border-sky-300 ring-4 ring-sky-100'
                    : 'border-slate-200 hover:border-slate-300 hover:shadow-md',
                isDragging ? 'opacity-60 shadow-lg' : '',
            ].join(' ')}
        >
            <div className="flex items-start gap-2.5">
                <button
                    type="button"
                    {...attributes}
                    {...listeners}
                    className="mt-0.5 flex h-8 w-8 shrink-0 items-center justify-center rounded-lg border border-slate-200 bg-slate-50 text-slate-500 transition hover:border-slate-300 hover:text-slate-700"
                    aria-label={tr(
                        `Reorder ${formatSectionType(section.type)} section`,
                        `إعادة ترتيب قسم ${formatSectionType(section.type)}`,
                    )}
                >
                    <GripVertical className="h-4 w-4" />
                </button>

                <button type="button" onClick={onSelect} className="min-w-0 flex-1 text-start">
                    <div className="flex flex-wrap items-center gap-1.5">
                        <span className="rounded-full border border-slate-200 bg-slate-50 px-2 py-0.5 text-[10px] font-semibold uppercase tracking-[0.14em] text-slate-500">
                            {schema?.displayName ?? formatSectionType(section.type)}
                        </span>
                        <span
                            className={[
                                'rounded-full border px-2 py-0.5 text-[10px] font-semibold uppercase tracking-[0.14em]',
                                section.isActive
                                    ? 'border-emerald-200 bg-emerald-50 text-emerald-700'
                                    : 'border-slate-200 bg-slate-100 text-slate-500',
                            ].join(' ')}
                        >
                            {section.isActive ? tr('Visible', 'ظاهر') : tr('Hidden', 'مخفي')}
                        </span>
                        {section.persistedId ? null : (
                            <span className="rounded-full border border-amber-200 bg-amber-50 px-2 py-0.5 text-[10px] font-semibold uppercase tracking-[0.14em] text-amber-700">
                                {tr('New', 'جديد')}
                            </span>
                        )}
                        {hasIssues ? (
                            <span className="rounded-full border border-rose-200 bg-rose-50 px-2 py-0.5 text-[10px] font-semibold uppercase tracking-[0.14em] text-rose-700">
                                {tr('Invalid', 'غير صالح')}
                            </span>
                        ) : null}
                    </div>

                    <h3 className="mt-2 truncate text-sm font-semibold text-slate-950">
                        {getSectionTitleForLanguage(section, schema ? [schema] : [], 'en')}
                    </h3>
                    <p className="mt-0.5 line-clamp-2 text-xs leading-5 text-slate-500" dir="auto">
                        {schema?.description ??
                            tr(
                                'Schema-backed homepage section ready for draft editing.',
                                'قسم رئيسي مبني على مخطط جاهز لتحرير المسودة.',
                            )}
                    </p>
                </button>

                <button
                    type="button"
                    onClick={onRemove}
                    className="flex h-8 w-8 shrink-0 items-center justify-center rounded-lg border border-rose-200 bg-rose-50 text-rose-600 transition hover:bg-rose-100"
                    aria-label={tr(
                        `Remove ${formatSectionType(section.type)} section`,
                        `إزالة قسم ${formatSectionType(section.type)}`,
                    )}
                >
                    <Trash2 className="h-4 w-4" />
                </button>
            </div>
        </div>
    )
}

function resolveSelectedSectionIdAfterSync(
    currentSelectedSectionId: string | null,
    previousSections: HomepageBuilderSection[],
    nextSections: HomepageBuilderSection[],
): string | null {
    if (!nextSections.length) {
        return null
    }

    if (!currentSelectedSectionId) {
        return nextSections[0].clientId
    }

    const previousSelectedSection = previousSections.find((section) => section.clientId === currentSelectedSectionId)
    if (!previousSelectedSection) {
        return nextSections[0].clientId
    }

    if (previousSelectedSection.persistedId) {
        const matchingPersistedSection = nextSections.find(
            (section) => section.persistedId === previousSelectedSection.persistedId,
        )

        if (matchingPersistedSection) {
            return matchingPersistedSection.clientId
        }
    }

    const matchingShape = nextSections.find(
        (section) =>
            section.type === previousSelectedSection.type && section.order === previousSelectedSection.order,
    )

    return matchingShape?.clientId ?? nextSections[0].clientId
}

export default function HomepageBuilderPage() {
    const queryClient = useQueryClient()
    const { showError, showInfo, showSuccess } = useToast()
    const { tr, formatNumber, formatDateTime } = useSuperAdminLocale()
    const [scopeMode, setScopeMode] = useState<ScopeMode>('global')
    const [selectedTenantId, setSelectedTenantId] = useState('')
    const [hydratedScopeKey, setHydratedScopeKey] = useState<string | null>(null)
    const [acceptedWorkspaceVersion, setAcceptedWorkspaceVersion] = useState<string | null>(null)
    const [sections, setSections] = useState<HomepageBuilderSection[]>([])
    const [savedSectionsSnapshot, setSavedSectionsSnapshot] = useState<HomepageBuilderSection[]>([])
    const [removedPersistedIds, setRemovedPersistedIds] = useState<string[]>([])
    const [selectedSectionId, setSelectedSectionId] = useState<string | null>(null)
    const [pendingAddType, setPendingAddType] = useState<HomepageSectionType>('hero')
    const [activeDragId, setActiveDragId] = useState<string | null>(null)
    const [previewTenantId, setPreviewTenantId] = useState('')
    const [activeLanguage, setActiveLanguage] = useState<'en' | 'ar'>('en')
    const [translatingFieldKey, setTranslatingFieldKey] = useState('')

    const sensors = useSensors(
        useSensor(PointerSensor, {
            activationConstraint: { distance: 8 },
        }),
        useSensor(KeyboardSensor, {
            coordinateGetter: sortableKeyboardCoordinates,
        }),
    )

    const activeTenantId = scopeMode === 'tenant' ? selectedTenantId || null : null
    const canLoadScopedSections = scopeMode === 'global' || Boolean(activeTenantId)
    const scopeKey = `${scopeMode}:${activeTenantId ?? 'global'}`

    const schemasQuery = useQuery({
        queryKey: homepageSchemasQueryKey,
        queryFn: getHomepageSchemas,
        staleTime: 5 * 60_000,
    })

    const tenantsQuery = useQuery({
        queryKey: homepageTenantsQueryKey,
        queryFn: getHomepageTenants,
        staleTime: 5 * 60_000,
    })

    const previewSectionsQuery = useQuery({
        queryKey: homepageSectionsQueryKey(activeTenantId, 'preview'),
        queryFn: () => getHomepageSections(activeTenantId, 'preview'),
        enabled: canLoadScopedSections,
    })

    const publishedSectionsQuery = useQuery({
        queryKey: homepageSectionsQueryKey(activeTenantId, 'published'),
        queryFn: () => getHomepageSections(activeTenantId, 'published'),
        enabled: canLoadScopedSections,
    })

    const schemas = useMemo(() => schemasQuery.data ?? [], [schemasQuery.data])
    const tenants = useMemo(() => tenantsQuery.data ?? [], [tenantsQuery.data])
    const selectedTenant = scopeMode === 'tenant'
        ? tenants.find((tenant) => tenant.id === selectedTenantId) ?? null
        : null
    const scopeLabel = selectedTenant
        ? tr(`${selectedTenant.name} homepage`, `الصفحة الرئيسية لـ ${selectedTenant.name}`)
        : tr('Global homepage', 'الصفحة الرئيسية العامة')
    const effectivePreviewTenantId = scopeMode === 'tenant' ? activeTenantId : previewTenantId || null
    const effectivePreviewTenant =
        effectivePreviewTenantId ? tenants.find((tenant) => tenant.id === effectivePreviewTenantId) ?? null : null

    const previewConfigQuery = useQuery({
        queryKey: homepagePreviewConfigQueryKey(effectivePreviewTenantId),
        queryFn: () => getHomepagePreviewStoreConfig(effectivePreviewTenantId!),
        enabled: Boolean(effectivePreviewTenantId),
        staleTime: 5 * 60_000,
    })

    const validationMap = useMemo(() => validateBuilderSections(sections, schemas), [schemas, sections])
    const validationIssueCount = useMemo(
        () => Object.values(validationMap).reduce((count, issues) => count + issues.length, 0),
        [validationMap],
    )

    const currentFingerprint = useMemo(
        () => createBuilderSectionsFingerprint(sections, schemas),
        [schemas, sections],
    )
    const savedFingerprint = useMemo(
        () => createBuilderSectionsFingerprint(savedSectionsSnapshot, schemas),
        [savedSectionsSnapshot, schemas],
    )

    const isDirty = removedPersistedIds.length > 0 || currentFingerprint !== savedFingerprint
    const hasValidationErrors = hasValidationIssues(validationMap)
    const activeDraftCount = sections.filter((section) => section.isActive).length
    const latestPublishedTimestamp = getLatestPublishedTimestamp(publishedSectionsQuery.data?.sections ?? [])
    const selectedSection = sections.find((section) => section.clientId === selectedSectionId) ?? null
    const selectedSchema = selectedSection ? getSectionSchema(selectedSection.type, schemas) : null
    const selectedSectionIssues = selectedSection ? validationMap[selectedSection.clientId] ?? [] : []
    const activeDragSection = sections.find((section) => section.clientId === activeDragId) ?? null

    const saveDraftMutation = useMutation({
        mutationFn: ({
            payload,
            workspaceVersion,
        }: {
            payload: ReturnType<typeof buildSaveHomepageDraftPayload>
            workspaceVersion: string
        }) => saveHomepageDraft(payload, workspaceVersion),
    })

    const publishMutation = useMutation({
        mutationFn: ({ tenantId, workspaceVersion }: { tenantId?: string | null; workspaceVersion: string }) =>
            publishHomepageSections(tenantId, workspaceVersion),
    })

    const starterTemplateMutation = useMutation({
        mutationFn: ({
            tenantId,
            overwriteExisting,
            workspaceVersion,
        }: {
            tenantId?: string | null
            overwriteExisting: boolean
            workspaceVersion: string
        }) =>
            generateHomepageStarterTemplate({
                tenantId,
                overwriteExisting,
            }, workspaceVersion),
    })

    const isBusy =
        saveDraftMutation.isPending ||
        publishMutation.isPending ||
        starterTemplateMutation.isPending
    const isAuthoringReady =
        canLoadScopedSections &&
        hydratedScopeKey === scopeKey &&
        Boolean(acceptedWorkspaceVersion)

    useEffect(() => {
        if (!schemas.length) {
            return
        }

        if (!schemas.some((schema) => schema.type === pendingAddType)) {
            setPendingAddType(schemas[0].type)
        }
    }, [pendingAddType, schemas])

    useEffect(() => {
        if (scopeMode === 'tenant') {
            return
        }

        if (previewTenantId || tenants.length === 0) {
            return
        }

        setPreviewTenantId(tenants[0].id)
    }, [previewTenantId, scopeMode, tenants])

    useEffect(() => {
        if (!canLoadScopedSections || !schemas.length || !previewSectionsQuery.data) {
            return
        }

        const isInitialHydration = hydratedScopeKey !== scopeKey || !acceptedWorkspaceVersion
        const hasNewAuthoritativeVersion = previewSectionsQuery.data.workspaceVersion !== acceptedWorkspaceVersion
        if (isInitialHydration || (!isDirty && !isBusy && hasNewAuthoritativeVersion)) {
            adoptAuthoritativeSnapshot(previewSectionsQuery.data)
        }
    }, [acceptedWorkspaceVersion, canLoadScopedSections, hydratedScopeKey, isBusy, isDirty, previewSectionsQuery.data, schemas, scopeKey])

    useEffect(() => {
        if (selectedSectionId && sections.some((section) => section.clientId === selectedSectionId)) {
            return
        }

        setSelectedSectionId(sections[0]?.clientId ?? null)
    }, [sections, selectedSectionId])

    useEffect(() => {
        const handleBeforeUnload = (event: BeforeUnloadEvent) => {
            if (!isDirty) {
                return
            }

            event.preventDefault()
            event.returnValue = ''
        }

        window.addEventListener('beforeunload', handleBeforeUnload)
        return () => window.removeEventListener('beforeunload', handleBeforeUnload)
    }, [isDirty])

    useEffect(() => {
        const handleKeyDown = (event: KeyboardEvent) => {
            const target = event.target as HTMLElement | null
            const isInputFocused =
                target instanceof HTMLInputElement ||
                target instanceof HTMLTextAreaElement ||
                target instanceof HTMLSelectElement

            if ((event.ctrlKey || event.metaKey) && event.key.toLowerCase() === 's') {
                event.preventDefault()
                if (!isBusy && isAuthoringReady && isDirty && !hasValidationErrors) {
                    void handleSaveDraft()
                }
                return
            }

            if (event.key === 'Escape' && !isInputFocused) {
                setSelectedSectionId(null)
                return
            }

            if ((event.key === 'Delete' || event.key === 'Backspace') && !isInputFocused && selectedSection) {
                event.preventDefault()
                handleRemoveSection(selectedSection.clientId)
            }
        }

        window.addEventListener('keydown', handleKeyDown)
        return () => window.removeEventListener('keydown', handleKeyDown)
    }, [hasValidationErrors, isAuthoringReady, isBusy, isDirty, selectedSection])

    function resetBuilderStateForScopeChange() {
        setHydratedScopeKey(null)
        setAcceptedWorkspaceVersion(null)
        setSections([])
        setSavedSectionsSnapshot([])
        setRemovedPersistedIds([])
        setSelectedSectionId(null)
    }

    function confirmScopeChange(): boolean {
        if (!isDirty) {
            return true
        }

        return window.confirm(
            tr(
                'You have unsaved homepage draft changes. Switch scopes and discard them?',
                'لديك تغييرات غير محفوظة في مسودة الصفحة الرئيسية. هل تريد تبديل النطاق وتجاهلها؟',
            ),
        )
    }

    function handleScopeModeChange(nextScopeMode: ScopeMode) {
        if (isBusy) {
            return
        }
        if (nextScopeMode === scopeMode) {
            return
        }

        if (!confirmScopeChange()) {
            return
        }

        resetBuilderStateForScopeChange()
        setScopeMode(nextScopeMode)
    }

    function handleTenantChange(nextTenantId: string) {
        if (isBusy) {
            return
        }
        if (nextTenantId === selectedTenantId) {
            return
        }

        if (!confirmScopeChange()) {
            return
        }

        resetBuilderStateForScopeChange()
        setSelectedTenantId(nextTenantId)
    }

    function applySections(nextSections: HomepageBuilderSection[]) {
        if (!isAuthoringReady || isBusy) {
            return
        }

        setSections(normalizeBuilderSectionsOrder(nextSections))
    }

    function adoptAuthoritativeSnapshot(snapshot: HomepageSectionsResult): boolean {
        const hasInvalidScope = snapshot.sections.some(
            (section) => (section.tenantId ?? null) !== activeTenantId,
        )
        if (hasInvalidScope || !snapshot.workspaceVersion) {
            setAcceptedWorkspaceVersion(null)
            setHydratedScopeKey(null)
            showError(
                tr(
                    'Homepage authoring state was rejected because its scope or version evidence is invalid.',
                    'رُفضت حالة تحرير الصفحة الرئيسية لأن نطاقها أو إثبات إصدارها غير صالح.',
                ),
            )
            return false
        }

        const hydratedSections = hydrateBuilderSections(snapshot.sections, schemas)
        const nextSelectedSectionId = resolveSelectedSectionIdAfterSync(
            selectedSectionId,
            sections,
            hydratedSections,
        )
        setSections(hydratedSections)
        setSavedSectionsSnapshot(hydratedSections)
        setRemovedPersistedIds([])
        setSelectedSectionId(nextSelectedSectionId)
        setAcceptedWorkspaceVersion(snapshot.workspaceVersion)
        setHydratedScopeKey(scopeKey)
        queryClient.setQueryData(homepageSectionsQueryKey(activeTenantId, 'preview'), snapshot)
        return true
    }

    async function reloadAuthoritativeSnapshot(): Promise<boolean> {
        const refreshed = await previewSectionsQuery.refetch()
        return refreshed.data ? adoptAuthoritativeSnapshot(refreshed.data) : false
    }

    async function recoverWorkspaceConflict(error: unknown): Promise<boolean> {
        if (!isHomepageWorkspaceConflict(error)) {
            return false
        }

        try {
            const reloaded = await reloadAuthoritativeSnapshot()
            if (reloaded) {
                showInfo(
                    tr(
                        'The Homepage changed in another session. The latest authoritative draft has been loaded.',
                        'تغيّرت الصفحة الرئيسية في جلسة أخرى. تم تحميل أحدث مسودة معتمدة.',
                    ),
                )
            }
        } catch (refreshError) {
            setAcceptedWorkspaceVersion(null)
            setHydratedScopeKey(null)
            showError(
                getErrorMessage(
                    refreshError,
                    tr('The latest Homepage draft could not be loaded.', 'تعذّر تحميل أحدث مسودة للصفحة الرئيسية.'),
                ),
            )
        }

        return true
    }

    async function handleRefresh() {
        if (!isAuthoringReady || isBusy) {
            return
        }

        if (
            isDirty &&
            !window.confirm(
                tr(
                    'Refresh the Homepage draft and discard unsaved changes?',
                    'هل تريد تحديث مسودة الصفحة الرئيسية وتجاهل التغييرات غير المحفوظة؟',
                ),
            )
        ) {
            return
        }

        try {
            await reloadAuthoritativeSnapshot()
        } catch (error) {
            showError(getErrorMessage(error, tr('Unable to refresh the Homepage draft.', 'تعذّر تحديث مسودة الصفحة الرئيسية.')))
        }
    }

    function handleSelectSection(sectionClientId: string) {
        setSelectedSectionId(sectionClientId)
    }

    function handleAddSection() {
        if (!isAuthoringReady || isBusy) {
            return
        }
        const schema = schemas.find((candidate) => candidate.type === pendingAddType)
        if (!schema) {
            showError(tr('Section schema is unavailable right now.', 'مخطط القسم غير متاح حاليًا.'))
            return
        }

        const newSection = createBuilderSection(schema, sections.length, activeTenantId)
        const nextSections = normalizeBuilderSectionsOrder([...sections, newSection])
        applySections(nextSections)
        setSelectedSectionId(newSection.clientId)
    }

    function handleRemoveSection(sectionClientId: string) {
        if (!isAuthoringReady || isBusy) {
            return
        }

        const section = sections.find((candidate) => candidate.clientId === sectionClientId)
        if (!section) {
            return
        }

        const sectionTitle = getSectionTitle(section, schemas)
        const confirmed = window.confirm(
            tr(
                `Remove "${sectionTitle}" from the current draft workspace?`,
                `هل تريد إزالة "${sectionTitle}" من مساحة عمل المسودة الحالية؟`,
            ),
        )

        if (!confirmed) {
            return
        }

        if (section.persistedId) {
            setRemovedPersistedIds((currentIds) =>
                currentIds.includes(section.persistedId!) ? currentIds : [...currentIds, section.persistedId!],
            )
        }

        applySections(sections.filter((candidate) => candidate.clientId !== sectionClientId))
    }

    function handleFieldChange(field: HomepageSectionFieldSchema, rawValue: string) {
        if (!selectedSection || !isAuthoringReady || isBusy) {
            return
        }

        applySections(
            sections.map((section) =>
                section.clientId === selectedSection.clientId
                    ? {
                          ...section,
                          data: updateLocalizedFieldValue(section.data, field, activeLanguage, rawValue),
                      }
                    : section,
            ),
        )
    }

    async function handleTranslateField(field: HomepageSectionFieldSchema) {
        if (!selectedSection || !field.isLocalized || activeLanguage !== 'ar') {
            return
        }

        const englishValue = getLocalizedFieldDisplayValue(selectedSection.data, field, 'en')
        const arabicValue = getLocalizedFieldDisplayValue(selectedSection.data, field, 'ar')
        if (!englishValue.trim()) {
            showInfo(tr('Enter English content first, then translate it into Arabic.', 'أدخل المحتوى الإنجليزي أولًا ثم ترجمه إلى العربية.'))
            return
        }

        if (
            arabicValue.trim() &&
            !window.confirm(
                tr(
                    'Arabic content already exists. Overwrite it with a fresh AI translation?',
                    'المحتوى العربي موجود بالفعل. هل تريد استبداله بترجمة جديدة من الذكاء الاصطناعي؟',
                ),
            )
        ) {
            return
        }

        setTranslatingFieldKey(field.key)
        try {
            const translatedValue = await translateMarketplaceContent(englishValue, 'en', 'ar')
            handleFieldChange(field, translatedValue)
        } catch (error) {
            showError(getErrorMessage(error, tr('Unable to translate this field right now.', 'تعذّرت ترجمة هذا الحقل حاليًا.')))
        } finally {
            setTranslatingFieldKey('')
        }
    }

    function handleSectionTypeChange(nextType: HomepageSectionType) {
        if (!selectedSection || !isAuthoringReady || isBusy) {
            return
        }

        const nextSchema = getSectionSchema(nextType, schemas)
        if (!nextSchema) {
            showError(tr('Section schema is unavailable right now.', 'مخطط القسم غير متاح حاليًا.'))
            return
        }

        const replacementSection = createBuilderSection(nextSchema, selectedSection.order, activeTenantId)
        replacementSection.clientId = selectedSection.clientId
        replacementSection.persistedId = selectedSection.persistedId ?? null
        replacementSection.isActive = selectedSection.isActive
        replacementSection.createdAt = selectedSection.createdAt
        replacementSection.updatedAt = selectedSection.updatedAt
        replacementSection.version = selectedSection.version

        applySections(
            sections.map((section) =>
                section.clientId === selectedSection.clientId ? replacementSection : section,
            ),
        )
    }

    function handleVisibilityToggle() {
        if (!selectedSection || !isAuthoringReady || isBusy) {
            return
        }

        applySections(
            sections.map((section) =>
                section.clientId === selectedSection.clientId
                    ? {
                          ...section,
                          isActive: !section.isActive,
                      }
                    : section,
            ),
        )
    }

    async function handleSaveDraft() {
        if (!isAuthoringReady || !acceptedWorkspaceVersion) {
            showInfo(tr('Load the current Homepage draft before saving changes.', 'حمّل مسودة الصفحة الرئيسية الحالية قبل حفظ التغييرات.'))
            return
        }

        if (!isDirty) {
            return
        }

        if (hasValidationErrors) {
            showError(
                tr(
                    'Resolve the validation issues in the builder before saving the draft.',
                    'عالج مشكلات التحقق في المنشئ قبل حفظ المسودة.',
                ),
            )
            return
        }

        try {
            const savedDraft = await saveDraftMutation.mutateAsync({
                payload: buildSaveHomepageDraftPayload(sections, schemas, activeTenantId),
                workspaceVersion: acceptedWorkspaceVersion,
            })
            adoptAuthoritativeSnapshot(savedDraft)
            showSuccess(tr('Homepage draft saved.', 'تم حفظ مسودة الصفحة الرئيسية.'))
        } catch (error) {
            if (!await recoverWorkspaceConflict(error)) {
                showError(getErrorMessage(error, tr('Unable to save the homepage draft.', 'تعذّر حفظ مسودة الصفحة الرئيسية.')))
            }
        }
    }

    async function handlePublish() {
        if (!isAuthoringReady || !acceptedWorkspaceVersion) {
            showInfo(tr('Load the current Homepage draft before publishing.', 'حمّل مسودة الصفحة الرئيسية الحالية قبل النشر.'))
            return
        }

        if (isDirty) {
            showInfo(tr('Save the current draft before publishing it live.', 'احفظ المسودة الحالية قبل نشرها مباشرة.'))
            return
        }

        if (hasValidationErrors) {
            showError(tr('Resolve validation issues before publishing.', 'عالج مشكلات التحقق قبل النشر.'))
            return
        }

        const confirmed = window.confirm(
            tr(
                `Publish the current ${scopeLabel} draft to live customers? This replaces the currently published homepage for this scope.`,
                `هل تريد نشر مسودة ${scopeLabel} الحالية للعملاء مباشرة؟ سيستبدل هذا الصفحة الرئيسية المنشورة حاليًا لهذا النطاق.`,
            ),
        )

        if (!confirmed) {
            return
        }

        try {
            const published = await publishMutation.mutateAsync({
                tenantId: activeTenantId,
                workspaceVersion: acceptedWorkspaceVersion,
            })
            adoptAuthoritativeSnapshot(published)
            queryClient.setQueryData(homepageSectionsQueryKey(activeTenantId, 'published'), published)
            showSuccess(tr('Homepage draft published.', 'تم نشر مسودة الصفحة الرئيسية.'))
        } catch (error) {
            if (!await recoverWorkspaceConflict(error)) {
                showError(getErrorMessage(error, tr('Unable to publish the homepage draft.', 'تعذّر نشر مسودة الصفحة الرئيسية.')))
            }
        }
    }

    async function handleGenerateStarterTemplate(overwriteExisting: boolean) {
        if (!isAuthoringReady || !acceptedWorkspaceVersion) {
            showInfo(
                tr(
                    'Load the current Homepage draft before generating a starter template.',
                    'حمّل مسودة الصفحة الرئيسية الحالية قبل إنشاء قالب البداية.',
                ),
            )
            return
        }

        if (overwriteExisting) {
            const confirmed = window.confirm(
                tr(
                    'Reset the current draft to the starter homepage template? This replaces the saved draft for this scope but does not publish it live.',
                    'هل تريد إعادة المسودة الحالية إلى قالب الصفحة الرئيسية للبداية؟ سيستبدل هذا المسودة المحفوظة لهذا النطاق دون نشرها مباشرة.',
                ),
            )

            if (!confirmed) {
                return
            }
        }

        try {
            const generatedSections = await starterTemplateMutation.mutateAsync({
                tenantId: activeTenantId,
                overwriteExisting,
                workspaceVersion: acceptedWorkspaceVersion,
            })
            adoptAuthoritativeSnapshot(generatedSections)
            showSuccess(
                overwriteExisting
                    ? tr('Starter template applied to the draft.', 'تم تطبيق قالب البداية على المسودة.')
                    : tr('Starter template generated.', 'تم إنشاء قالب البداية.'),
            )
        } catch (error) {
            if (!await recoverWorkspaceConflict(error)) {
                showError(
                    getErrorMessage(
                        error,
                        tr('Unable to generate the starter homepage template.', 'تعذّر إنشاء قالب البداية للصفحة الرئيسية.'),
                    ),
                )
            }
        }
    }

    function handleDragStart(event: DragStartEvent) {
        if (!isAuthoringReady || isBusy) {
            return
        }

        setActiveDragId(String(event.active.id))
    }

    function handleDragEnd(event: DragEndEvent) {
        setActiveDragId(null)
        if (!isAuthoringReady || isBusy) {
            return
        }

        const { active, over } = event

        if (!over || active.id === over.id) {
            return
        }

        const currentIndex = sections.findIndex((section) => section.clientId === active.id)
        const nextIndex = sections.findIndex((section) => section.clientId === over.id)

        if (currentIndex < 0 || nextIndex < 0) {
            return
        }

        const nextSections = normalizeBuilderSectionsOrder(arrayMove(sections, currentIndex, nextIndex))
        applySections(nextSections)
        showInfo(tr('Draft order updated locally. Save the draft to persist it.', 'تم تحديث ترتيب المسودة محليًا. احفظ المسودة لتثبيته.'))
    }

    if (schemasQuery.isError) {
        return (
            <SuperAdminPage>
                <PageHeader
                    eyebrow={tr('Super Admin · Storefront', 'الإدارة العليا · المتجر')}
                    title={tr('Homepage builder', 'منشئ الصفحة الرئيسية')}
                    description={tr(
                        'Compose the storefront homepage from schema-backed sections, review it in the storefront renderer, then save and publish when the draft is valid.',
                        'كوّن الصفحة الرئيسية للمتجر من أقسام مبنية على مخططات، وراجعها في معاينة المتجر، ثم احفظ وانشر عندما تصبح المسودة صالحة.',
                    )}
                />
                <ErrorState
                    title={tr('Builder unavailable', 'المنشئ غير متاح')}
                    description={getErrorMessage(
                        schemasQuery.error,
                        tr('Unable to load homepage section schemas.', 'تعذّر تحميل مخططات أقسام الصفحة الرئيسية.'),
                    )}
                    onRetry={() => schemasQuery.refetch()}
                    retryLabel={tr('Retry', 'إعادة المحاولة')}
                />
            </SuperAdminPage>
        )
    }

    return (
        <SuperAdminPage>
            <PageHeader
                eyebrow={tr('Super Admin · Storefront', 'الإدارة العليا · المتجر')}
                title={tr('Homepage builder', 'منشئ الصفحة الرئيسية')}
                description={tr(
                    'Compose the storefront homepage from schema-backed sections, review it in the real storefront renderer, then save and publish when the draft is valid.',
                    'كوّن الصفحة الرئيسية للمتجر من أقسام مبنية على مخططات، وراجعها في معاينة المتجر الحقيقية، ثم احفظ وانشر عندما تصبح المسودة صالحة.',
                )}
                actions={
                    <>
                        <button
                            type="button"
                            onClick={() => void handleRefresh()}
                            disabled={!isAuthoringReady || isBusy}
                            className={saButton.secondary}
                        >
                            <RefreshCw className="h-4 w-4" />
                            {tr('Refresh', 'تحديث')}
                        </button>

                        <button
                            type="button"
                            onClick={() => void handleGenerateStarterTemplate(sections.length > 0)}
                            disabled={!isAuthoringReady || isBusy}
                            className={`${saButton.secondary} border-amber-200 bg-amber-50 text-amber-800 hover:border-amber-300 hover:bg-amber-100`}
                        >
                            {starterTemplateMutation.isPending ? (
                                <Loader2 className="h-4 w-4 animate-spin" />
                            ) : (
                                <Sparkles className="h-4 w-4" />
                            )}
                            {sections.length > 0
                                ? tr('Reset to template', 'إعادة إلى القالب')
                                : tr('Generate starter', 'إنشاء قالب البداية')}
                        </button>

                        <button
                            type="button"
                            onClick={() => void handleSaveDraft()}
                            disabled={!isAuthoringReady || !isDirty || hasValidationErrors || isBusy}
                            className={saButton.primary}
                        >
                            {saveDraftMutation.isPending ? (
                                <Loader2 className="h-4 w-4 animate-spin" />
                            ) : (
                                <Save className="h-4 w-4" />
                            )}
                            {tr('Save Draft', 'حفظ المسودة')}
                        </button>

                        <button
                            type="button"
                            onClick={() => void handlePublish()}
                            disabled={!isAuthoringReady || isBusy || hasValidationErrors || isDirty}
                            className={saButton.success}
                        >
                            {publishMutation.isPending ? (
                                <Loader2 className="h-4 w-4 animate-spin" />
                            ) : (
                                <Eye className="h-4 w-4" />
                            )}
                            {tr('Publish', 'نشر')}
                        </button>
                    </>
                }
                aside={
                    <span className="inline-flex items-center gap-1.5 rounded-full border border-sky-200 bg-sky-50 px-2.5 py-1 text-xs font-semibold text-sky-800">
                        <LayoutTemplate className="h-3.5 w-3.5" />
                        {scopeLabel}
                    </span>
                }
            />

            <div className="grid gap-3 sm:grid-cols-2 xl:grid-cols-4">
                <KpiCard
                    label={tr('Editing scope', 'نطاق التحرير')}
                    value={selectedTenant ? selectedTenant.name : tr('Global', 'عام')}
                    hint={`${tr(
                        'Tenant drafts override the global homepage only for that tenant after publish.',
                        'مسودات المستأجر تتجاوز الصفحة الرئيسية العامة لهذا المستأجر فقط بعد النشر.',
                    )} · ${scopeLabel}`}
                    tone="sky"
                    icon={<LayoutTemplate className="h-4 w-4 text-sky-700" />}
                />
                <KpiCard
                    label={tr('Draft health', 'حالة المسودة')}
                    value={isDirty ? tr('Unsaved changes', 'تغييرات غير محفوظة') : tr('Draft in sync', 'المسودة متزامنة')}
                    hint={tr(
                        `${formatNumber(sections.length)} sections · ${formatNumber(activeDraftCount)} visible in preview`,
                        `${formatNumber(sections.length)} أقسام · ${formatNumber(activeDraftCount)} ظاهر في المعاينة`,
                    )}
                    tone={isDirty ? 'amber' : 'emerald'}
                    icon={<Save className="h-4 w-4" />}
                />
                <KpiCard
                    label={tr('Validation', 'التحقق')}
                    value={
                        validationIssueCount > 0
                            ? tr(`${formatNumber(validationIssueCount)} issues`, `${formatNumber(validationIssueCount)} مشكلات`)
                            : tr('No blocking issues', 'لا مشكلات مانعة')
                    }
                    hint={tr(
                        'Publish stays disabled until the draft is valid and saved.',
                        'يبقى النشر معطّلًا حتى تصبح المسودة صالحة ومحفوظة.',
                    )}
                    tone={validationIssueCount > 0 ? 'rose' : 'emerald'}
                    icon={<AlertTriangle className="h-4 w-4" />}
                />
                <KpiCard
                    label={tr('Published snapshot', 'النسخة المنشورة')}
                    value={tr(
                        `${formatNumber(publishedSectionsQuery.data?.sections.length ?? 0)} live sections`,
                        `${formatNumber(publishedSectionsQuery.data?.sections.length ?? 0)} أقسام مباشرة`,
                    )}
                    hint={
                        latestPublishedTimestamp
                            ? tr(
                                  `Last published ${formatDateTime(latestPublishedTimestamp)}`,
                                  `آخر نشر ${formatDateTime(latestPublishedTimestamp)}`,
                              )
                            : tr('Never published for this scope', 'لم يُنشر بعد لهذا النطاق')
                    }
                    tone="slate"
                    icon={<Eye className="h-4 w-4" />}
                />
            </div>

            <GuidePanel
                guide={{
                    en: {
                        title: 'Homepage builder guide',
                        purpose:
                            'Composes the storefront homepage for the global scope or a single tenant override: add ordered sections, edit their schema-backed fields in English and Arabic, validate the draft, preview it through the real storefront renderer, then save and publish.',
                        when:
                            'Use it when launching a storefront, changing seasonal content, testing a tenant-specific layout, or correcting homepage content before customers see it.',
                        steps: [
                            'Choose the editing scope: Global for the shared homepage, or Tenant override for one marketplace tenant.',
                            'Add a section, reorder the left-hand list by dragging, and select the block you want to edit.',
                            'Edit the schema-backed fields in the English and Arabic content tabs; use Auto Translate to draft Arabic from English.',
                            'Resolve every validation issue reported in the list or editor panel.',
                            'Save the draft, confirm the published snapshot is unchanged, then preview the exact storefront rendering.',
                            'Publish only after the draft is saved, valid, and reviewed; publishing replaces the live homepage for the selected scope.',
                        ],
                        statuses: [
                            { label: 'Global scope', description: 'Shared homepage used by stores without an override.' },
                            { label: 'Tenant override', description: 'A draft and published homepage that applies to one tenant only.' },
                            { label: 'Visible / Hidden', description: 'Whether the section is included in the draft preview and published homepage.' },
                            { label: 'New / Persisted', description: 'A local addition that is not saved yet, or a section that already exists in the draft.' },
                            { label: 'Invalid', description: 'The section has blocking validation issues and must be corrected.' },
                            { label: 'Draft in sync / Unsaved changes', description: 'Whether local edits differ from the last saved draft.' },
                            { label: 'Published snapshot', description: 'What customers currently receive; it changes only after Publish succeeds.' },
                        ],
                        safety: [
                            'Saving, previewing, or reordering never changes live storefront traffic; only Publish does.',
                            'Publish remains disabled while the draft is unsaved or has validation issues.',
                            'Switching scope, refreshing, or leaving with unsaved edits asks for confirmation before discarding them.',
                            'Workspace conflicts reload the latest authoritative draft instead of overwriting another session.',
                            'Auto Translate fills the Arabic draft field; review the translation before saving or publishing.',
                            'Tenant overrides affect only the selected tenant after publish; the global homepage stays unchanged.',
                        ],
                    },
                    ar: {
                        title: 'دليل منشئ الصفحة الرئيسية',
                        purpose:
                            'يبني الصفحة الرئيسية للمتجر للنطاق العام أو لتجاوز مستأجر واحد: أضف أقسامًا مرتبة، وحرّر حقولها المبنية على المخطط بالإنجليزية والعربية، وتحقق من المسودة، وعاينها عبر عارض المتجر الحقيقي، ثم احفظ وانشر.',
                        when:
                            'استخدمه عند إطلاق متجر، أو تغيير محتوى موسمي، أو اختبار تخطيط خاص بمستأجر، أو تصحيح محتوى الصفحة الرئيسية قبل أن يراه العملاء.',
                        steps: [
                            'اختر نطاق التحرير: عام للصفحة الرئيسية المشتركة، أو تجاوز مستأجر لمستأجر واحد في السوق.',
                            'أضف قسمًا، وأعد ترتيب القائمة اليسرى بالسحب، واختر الكتلة التي تريد تحريرها.',
                            'حرّر الحقول المبنية على المخطط في تبويبي المحتوى الإنجليزي والعربي؛ واستخدم الترجمة التلقائية لصياغة العربية من الإنجليزية.',
                            'عالج كل مشكلة تحقق تظهر في القائمة أو لوحة المحرر.',
                            'احفظ المسودة، وتأكد من أن النسخة المنشورة لم تتغير، ثم عاين شكل المتجر الفعلي.',
                            'انشر فقط بعد حفظ المسودة والتحقق منها ومراجعتها؛ فالنشر يستبدل الصفحة الرئيسية المباشرة للنطاق المحدد.',
                        ],
                        statuses: [
                            { label: 'النطاق العام', description: 'الصفحة الرئيسية المشتركة للمتاجر التي لا تملك تجاوزًا.' },
                            { label: 'تجاوز مستأجر', description: 'مسودة وصفحة رئيسية منشورة تخص مستأجرًا واحدًا فقط.' },
                            { label: 'ظاهر / مخفي', description: 'هل يُضمَّن القسم في معاينة المسودة والصفحة المنشورة.' },
                            { label: 'جديد / محفوظ', description: 'إضافة محلية لم تُحفظ بعد، أو قسم موجود بالفعل في المسودة.' },
                            { label: 'غير صالح', description: 'يحتوي القسم على مشكلات تحقق مانعة ويجب تصحيحها.' },
                            { label: 'المسودة متزامنة / تغييرات غير محفوظة', description: 'هل تختلف التعديلات المحلية عن آخر مسودة محفوظة.' },
                            { label: 'النسخة المنشورة', description: 'ما يستلمه العملاء حاليًا؛ ولا يتغير إلا بعد نجاح النشر.' },
                        ],
                        safety: [
                            'الحفظ أو المعاينة أو إعادة الترتيب لا تغيّر حركة المتجر المباشرة؛ النشر وحده يفعل ذلك.',
                            'يبقى زر النشر معطّلًا ما دامت المسودة غير محفوظة أو بها مشكلات تحقق.',
                            'تبديل النطاق أو التحديث أو المغادرة مع تعديلات غير محفوظة يطلب تأكيدًا قبل تجاهلها.',
                            'تعارضات مساحة العمل تعيد تحميل أحدث مسودة معتمدة بدل الكتابة فوق جلسة أخرى.',
                            'الترجمة التلقائية تملأ حقل المسودة العربي؛ راجع الترجمة قبل الحفظ أو النشر.',
                            'تجاوزات المستأجر تؤثر فقط على المستأجر المحدد بعد النشر؛ وتبقى الصفحة الرئيسية العامة دون تغيير.',
                        ],
                    },
                }}
            />

            <section className="grid gap-3 md:grid-cols-2">
                <ScopeButton
                    disabled={isBusy}
                    isActive={scopeMode === 'global'}
                    label={tr('Global', 'عام')}
                    description={tr(
                        'Fallback homepage shared by stores without an override.',
                        'الصفحة الرئيسية الاحتياطية المشتركة للمتاجر التي لا تملك تجاوزًا.',
                    )}
                    onClick={() => handleScopeModeChange('global')}
                />
                <ScopeButton
                    disabled={isBusy}
                    isActive={scopeMode === 'tenant'}
                    label={tr('Tenant override', 'تجاوز مستأجر')}
                    description={tr(
                        'Edit a draft homepage for a specific marketplace tenant.',
                        'حرّر مسودة صفحة رئيسية لمستأجر محدد في السوق.',
                    )}
                    onClick={() => handleScopeModeChange('tenant')}
                />
            </section>

            {scopeMode === 'tenant' ? (
                <section className="rounded-2xl border border-slate-200 bg-white p-3 shadow-sm">
                    <label className="block max-w-md space-y-1">
                        <span className="text-[10px] font-semibold uppercase tracking-[0.16em] text-slate-500">
                            {tr('Tenant', 'المستأجر')}
                        </span>
                        <select
                            value={selectedTenantId}
                            onChange={(event) => handleTenantChange(event.target.value)}
                            disabled={isBusy}
                            className={saInput}
                        >
                            <option value="">{tr('Select a tenant override', 'اختر تجاوز مستأجر')}</option>
                            {tenants.map((tenant) => (
                                <option key={tenant.id} value={tenant.id}>
                                    {tenant.name}
                                </option>
                            ))}
                        </select>
                    </label>
                </section>
            ) : null}

            {!canLoadScopedSections ? (
                <section className="rounded-2xl border border-dashed border-slate-300 bg-white p-4 shadow-sm">
                    <EmptyState
                        title={tr('Choose a tenant override', 'اختر تجاوز مستأجر')}
                        description={tr(
                            'Tenant-scoped homepage drafts are ready, but you need to select a tenant before sections can be loaded or saved.',
                            'مسودات الصفحة الرئيسية الخاصة بالمستأجر جاهزة، لكن يجب اختيار مستأجر قبل تحميل الأقسام أو حفظها.',
                        )}
                    />
                </section>
            ) : previewSectionsQuery.isError ? (
                <ErrorState
                    title={tr('Draft homepage unavailable', 'مسودة الصفحة الرئيسية غير متاحة')}
                    description={getErrorMessage(
                        previewSectionsQuery.error,
                        tr(
                            'Unable to load homepage draft sections for this scope.',
                            'تعذّر تحميل أقسام مسودة الصفحة الرئيسية لهذا النطاق.',
                        ),
                    )}
                    onRetry={() => previewSectionsQuery.refetch()}
                    retryLabel={tr('Retry', 'إعادة المحاولة')}
                />
            ) : !isAuthoringReady ? (
                <LoadingState label={tr('Loading authoritative Homepage draft', 'جارٍ تحميل مسودة الصفحة الرئيسية المعتمدة…')} />
            ) : (
                <DndContext
                    sensors={sensors}
                    collisionDetection={closestCenter}
                    onDragStart={handleDragStart}
                    onDragEnd={handleDragEnd}
                >
                    <section className="grid grid-cols-1 gap-3 xl:grid-cols-[300px_minmax(0,1fr)_340px] [&>*]:min-w-0">
                        <aside className="rounded-2xl border border-slate-200 bg-white p-4 shadow-sm">
                            <div>
                                <p className="text-[10px] font-semibold uppercase tracking-[0.16em] text-sky-700">
                                    {tr('Sections', 'الأقسام')}
                                </p>
                                <h2 className="mt-1 text-base font-semibold text-slate-950">
                                    {tr('Draft structure', 'هيكل المسودة')}
                                </h2>
                                <p className="mt-1 text-xs leading-5 text-slate-500">
                                    {tr(
                                        'Reorder on the left, select a block to edit, and keep published traffic isolated while you shape the draft.',
                                        'أعد الترتيب من اليسار، واختر كتلة للتحرير، وابقِ حركة النشر معزولة أثناء بناء المسودة.',
                                    )}
                                </p>
                            </div>

                            <div className="mt-3 rounded-xl border border-slate-200 bg-slate-50/80 p-3">
                                <label className="block space-y-1">
                                    <span className="text-[10px] font-semibold uppercase tracking-[0.16em] text-slate-500">
                                        {tr('Add section type', 'نوع القسم المضاف')}
                                    </span>
                                    <select
                                        value={pendingAddType}
                                        onChange={(event) => setPendingAddType(event.target.value as HomepageSectionType)}
                                        disabled={!isAuthoringReady || isBusy}
                                        className={saInput}
                                    >
                                        {schemas.map((schema) => (
                                            <option key={schema.type} value={schema.type}>
                                                {schema.displayName}
                                            </option>
                                        ))}
                                    </select>
                                </label>

                                <button
                                    type="button"
                                    onClick={handleAddSection}
                                    disabled={!isAuthoringReady || isBusy}
                                    className={`${saButton.primary} mt-3 w-full`}
                                >
                                    <Plus className="h-4 w-4" />
                                    {tr('Add section', 'إضافة قسم')}
                                </button>
                            </div>

                            <div className="mt-3">
                                {sections.length === 0 ? (
                                    <EmptyState
                                        title={tr('No sections in this draft', 'لا أقسام في هذه المسودة')}
                                        description={tr(
                                            'Start with a hero, product shelf, banner, or categories section, or generate the Shopixy starter template.',
                                            'ابدأ بقسم رئيسي أو رف منتجات أو بانر أو أقسام فئات، أو أنشئ قالب البداية من Shopixy.',
                                        )}
                                        action={
                                            <button
                                                type="button"
                                                onClick={() => void handleGenerateStarterTemplate(false)}
                                                disabled={!isAuthoringReady || isBusy}
                                                className={saButton.primary}
                                            >
                                                {starterTemplateMutation.isPending ? (
                                                    <Loader2 className="h-4 w-4 animate-spin" />
                                                ) : (
                                                    <Sparkles className="h-4 w-4" />
                                                )}
                                                {tr('Generate starter template', 'إنشاء قالب البداية')}
                                            </button>
                                        }
                                    />
                                ) : (
                                    <SortableContext
                                        items={sections.map((section) => section.clientId)}
                                        strategy={verticalListSortingStrategy}
                                    >
                                        <div className="space-y-2">
                                            {sections.map((section) => (
                                                <SortableSectionItem
                                                    key={section.clientId}
                                                    section={section}
                                                    schema={getSectionSchema(section.type, schemas)}
                                                    isSelected={section.clientId === selectedSectionId}
                                                    hasIssues={Boolean(validationMap[section.clientId]?.length)}
                                                    onSelect={() => handleSelectSection(section.clientId)}
                                                    onRemove={() => handleRemoveSection(section.clientId)}
                                                />
                                            ))}
                                        </div>
                                    </SortableContext>
                                )}
                            </div>
                        </aside>

                        <div className="rounded-2xl border border-slate-200 bg-[linear-gradient(180deg,#f8fafc_0%,#eef2ff_100%)] p-4 shadow-sm">
                            <div className="flex flex-wrap items-center justify-between gap-3 border-b border-slate-200 pb-3">
                                <div className="min-w-0">
                                    <p className="text-[10px] font-semibold uppercase tracking-[0.16em] text-sky-700">
                                        {tr('Live preview', 'المعاينة الحية')}
                                    </p>
                                    <h2 className="mt-1 text-base font-semibold text-slate-950">
                                        {tr('Unified storefront canvas', 'لوحة المتجر الموحدة')}
                                    </h2>
                                    <p className="mt-1 text-xs leading-5 text-slate-500">
                                        {tr(
                                            'Uses the same storefront section renderer customers receive after publish. Local draft edits stay isolated until you save and publish.',
                                            'يستخدم نفس عارض أقسام المتجر الذي يستلمه العملاء بعد النشر. تبقى تعديلات المسودة المحلية معزولة حتى تحفظ وتنشر.',
                                        )}
                                    </p>
                                </div>

                                <div className="flex flex-wrap items-center gap-2">
                                    {scopeMode === 'global' ? (
                                        <label className="w-full min-w-0 max-w-full rounded-xl border border-slate-200 bg-white px-3 py-2 shadow-sm sm:w-auto">
                                            <span className="text-[10px] font-semibold uppercase tracking-[0.16em] text-slate-500">
                                                {tr('Preview tenant', 'مستأجر المعاينة')}
                                            </span>
                                            <select
                                                value={previewTenantId}
                                                onChange={(event) => setPreviewTenantId(event.target.value)}
                                                disabled={isBusy}
                                                className="mt-1 block w-full min-w-0 max-w-full rounded-lg border border-slate-200 bg-white px-2.5 py-1.5 text-sm text-slate-900 shadow-sm outline-none transition focus:border-sky-300 focus:ring-4 focus:ring-sky-100"
                                            >
                                                <option value="">{tr('Choose storefront theme', 'اختر قالب المتجر')}</option>
                                                {tenants.map((tenant) => (
                                                    <option key={tenant.id} value={tenant.id}>
                                                        {tenant.name}
                                                    </option>
                                                ))}
                                            </select>
                                        </label>
                                    ) : null}

                                    <div className="rounded-xl border border-slate-200 bg-white px-3 py-2 shadow-sm">
                                        <p className="text-[10px] font-semibold uppercase tracking-[0.16em] text-slate-500">
                                            {tr('Published snapshot', 'النسخة المنشورة')}
                                        </p>
                                        <p className="mt-0.5 text-sm font-semibold text-slate-950">
                                            {tr(
                                                `${formatNumber(publishedSectionsQuery.data?.sections.length ?? 0)} live sections`,
                                                `${formatNumber(publishedSectionsQuery.data?.sections.length ?? 0)} أقسام مباشرة`,
                                            )}
                                        </p>
                                    </div>
                                </div>
                            </div>

                            <div className="mt-4">
                                {previewSectionsQuery.isLoading && !hydratedScopeKey ? (
                                    <div className="space-y-3">
                                        {Array.from({ length: 3 }).map((_, index) => (
                                            <div
                                                key={`builder-preview-skeleton-${index}`}
                                                className="h-56 animate-pulse rounded-xl border border-slate-200 bg-white"
                                            />
                                        ))}
                                    </div>
                                ) : (
                                    <HomepageBuilderPreview
                                        sections={sections}
                                        selectedSectionClientId={selectedSectionId}
                                        onSelectSection={handleSelectSection}
                                        storeConfig={previewConfigQuery.data ?? null}
                                        previewTenantId={effectivePreviewTenantId}
                                        previewTenantName={effectivePreviewTenant?.name ?? null}
                                        isLoadingConfig={Boolean(effectivePreviewTenantId) && previewConfigQuery.isLoading && !previewConfigQuery.data}
                                        language={activeLanguage}
                                        schemas={schemas}
                                    />
                                )}

                                {previewConfigQuery.isError ? (
                                    <div className="mt-3">
                                        <InlineNotice tone="amber">
                                            {getErrorMessage(
                                                previewConfigQuery.error,
                                                tr(
                                                    'Unable to load the storefront theme configuration for preview.',
                                                    'تعذّر تحميل إعداد قالب المتجر للمعاينة.',
                                                ),
                                            )}
                                        </InlineNotice>
                                    </div>
                                ) : null}
                            </div>
                        </div>

                        <aside className="rounded-2xl border border-slate-200 bg-white p-4 shadow-sm">
                            {selectedSection && selectedSchema ? (
                                <>
                                    <div className="flex items-start justify-between gap-3">
                                        <div className="min-w-0">
                                            <p className="text-[10px] font-semibold uppercase tracking-[0.16em] text-sky-700">
                                                {tr('Editor', 'المحرر')}
                                            </p>
                                            <h2 className="mt-1 truncate text-base font-semibold text-slate-950" dir="auto">
                                                {getSectionTitleForLanguage(selectedSection, schemas, activeLanguage)}
                                            </h2>
                                            <p className="mt-1 text-xs leading-5 text-slate-500">
                                                {tr(
                                                    'Inline edits update the draft preview instantly and stay local until you save.',
                                                    'تحدّث التعديلات المباشرة معاينة المسودة فورًا وتبقى محلية حتى تحفظ.',
                                                )}
                                            </p>
                                        </div>

                                        <button
                                            type="button"
                                            onClick={handleVisibilityToggle}
                                            disabled={isBusy}
                                            className={[
                                                'inline-flex shrink-0 items-center gap-1.5 rounded-lg border px-2.5 py-1.5 text-xs font-semibold transition',
                                                selectedSection.isActive
                                                    ? 'border-emerald-200 bg-emerald-50 text-emerald-700'
                                                    : 'border-slate-200 bg-slate-100 text-slate-500',
                                            ].join(' ')}
                                        >
                                            {selectedSection.isActive ? <Eye className="h-3.5 w-3.5" /> : <EyeOff className="h-3.5 w-3.5" />}
                                            {selectedSection.isActive ? tr('Visible', 'ظاهر') : tr('Hidden', 'مخفي')}
                                        </button>
                                    </div>

                                    <div className="mt-3 grid gap-2 md:grid-cols-2">
                                        <label className="block space-y-1">
                                            <span className="text-[10px] font-semibold uppercase tracking-[0.16em] text-slate-500">
                                                {tr('Section type', 'نوع القسم')}
                                            </span>
                                            <select
                                                value={selectedSection.type}
                                                onChange={(event) => handleSectionTypeChange(event.target.value as HomepageSectionType)}
                                                disabled={isBusy}
                                                className={saInput}
                                            >
                                                {schemas.map((schema) => (
                                                    <option key={schema.type} value={schema.type}>
                                                        {schema.displayName}
                                                    </option>
                                                ))}
                                            </select>
                                        </label>

                                        <div className="rounded-xl border border-slate-200 bg-slate-50 px-3 py-2">
                                            <p className="text-[10px] font-semibold uppercase tracking-[0.16em] text-slate-500">
                                                {tr('Draft state', 'حالة المسودة')}
                                            </p>
                                            <p className="mt-1 text-sm font-semibold text-slate-950">
                                                {selectedSection.persistedId
                                                    ? tr('Persisted draft section', 'قسم محفوظ في المسودة')
                                                    : tr('New local section', 'قسم محلي جديد')}
                                            </p>
                                            <p className="mt-0.5 text-xs leading-5 text-slate-500">
                                                {tr(
                                                    `Schema v${selectedSection.schemaVersion} · order slot ${selectedSection.order + 1}`,
                                                    `مخطط v${selectedSection.schemaVersion} · ترتيب ${selectedSection.order + 1}`,
                                                )}
                                            </p>
                                        </div>
                                    </div>

                                    {selectedSectionIssues.length > 0 ? (
                                        <div className="mt-3">
                                            <InlineNotice tone="rose" title={tr('Validation needs attention', 'التحقق يحتاج انتباهًا')}>
                                                <ul className="mt-1 space-y-1">
                                                    {selectedSectionIssues.map((issue) => (
                                                        <li key={`${issue.fieldKey}-${issue.message}`}>{issue.message}</li>
                                                    ))}
                                                </ul>
                                            </InlineNotice>
                                        </div>
                                    ) : null}

                                    <div className="mt-4 space-y-4">
                                        <div className="inline-flex rounded-xl border border-slate-200 bg-slate-50 p-1">
                                            {(['en', 'ar'] as const).map((language) => (
                                                <button
                                                    key={language}
                                                    type="button"
                                                    onClick={() => setActiveLanguage(language)}
                                                    className={[
                                                        'rounded-lg px-3 py-1.5 text-xs font-semibold transition',
                                                        activeLanguage === language
                                                            ? 'bg-slate-900 text-white'
                                                            : 'text-slate-600 hover:bg-white',
                                                    ].join(' ')}
                                                >
                                                    {language.toUpperCase()}
                                                </button>
                                            ))}
                                        </div>

                                        {selectedSchema.fields
                                            .filter((field) => shouldDisplaySchemaField(field, selectedSection.data))
                                            .map((field) => {
                                            const fieldIssues = selectedSectionIssues.filter(
                                                (issue) => issue.fieldKey === field.key,
                                            )

                                            return (
                                                <div key={field.key} className="space-y-1.5">
                                                    <div className="flex flex-wrap items-center justify-between gap-2">
                                                        <div className="flex flex-wrap items-center gap-2">
                                                            <label className="text-sm font-semibold text-slate-900" dir="auto">
                                                                {field.label}
                                                                {field.isLocalized ? ` (${activeLanguage.toUpperCase()})` : ''}
                                                            </label>
                                                            {field.required ? (
                                                                <span className="rounded-full border border-rose-200 bg-rose-50 px-2 py-0.5 text-[10px] font-semibold uppercase tracking-[0.14em] text-rose-600">
                                                                    {tr('Required', 'مطلوب')}
                                                                </span>
                                                            ) : (
                                                                <span className="rounded-full border border-slate-200 bg-white px-2 py-0.5 text-[10px] font-semibold uppercase tracking-[0.14em] text-slate-400">
                                                                    {tr('Optional', 'اختياري')}
                                                                </span>
                                                            )}
                                                        </div>

                                                        {field.isLocalized ? (
                                                            <button
                                                                type="button"
                                                                onClick={() => void handleTranslateField(field)}
                                                                disabled={isBusy || activeLanguage !== 'ar' || translatingFieldKey === field.key}
                                                                className="inline-flex items-center gap-1.5 rounded-lg border border-slate-200 bg-white px-2.5 py-1.5 text-[11px] font-semibold text-slate-600 transition hover:border-slate-300 hover:bg-slate-50 disabled:opacity-50"
                                                            >
                                                                {translatingFieldKey === field.key ? (
                                                                    <Loader2 className="h-3.5 w-3.5 animate-spin" />
                                                                ) : (
                                                                    <Sparkles className="h-3.5 w-3.5" />
                                                                )}
                                                                {tr('Auto translate', 'ترجمة تلقائية')}
                                                            </button>
                                                        ) : null}
                                                    </div>

                                                    <SchemaFieldInput
                                                        field={field}
                                                        disabled={isBusy}
                                                        value={field.isLocalized
                                                            ? getLocalizedFieldDisplayValue(selectedSection.data, field, activeLanguage)
                                                            : getFieldDisplayValue(field, selectedSection.data[field.key])}
                                                        onChange={(value) => handleFieldChange(field, value)}
                                                    />

                                                    {field.helpText ? (
                                                        <p className="text-xs leading-5 text-slate-500" dir="auto">{field.helpText}</p>
                                                    ) : null}

                                                    {fieldIssues.map((issue) => (
                                                        <p key={issue.message} className="text-xs font-medium text-rose-600">
                                                            {issue.message}
                                                        </p>
                                                    ))}
                                                </div>
                                            )
                                        })}
                                    </div>

                                    <div className="mt-5 border-t border-slate-200 pt-4">
                                        <button
                                            type="button"
                                            onClick={() => handleRemoveSection(selectedSection.clientId)}
                                            className={`${saButton.secondary} w-full border-rose-200 bg-rose-50 text-rose-700 hover:border-rose-300 hover:bg-rose-100`}
                                        >
                                            <Trash2 className="h-4 w-4" />
                                            {tr('Remove section', 'إزالة القسم')}
                                        </button>
                                    </div>
                                </>
                            ) : (
                                <EmptyState
                                    title={tr('Select a section', 'اختر قسمًا')}
                                    description={tr(
                                        'Pick a section from the left list or click a block in the live preview to start editing its schema-backed fields.',
                                        'اختر قسمًا من القائمة اليسرى أو انقر على كتلة في المعاينة الحية لبدء تحرير حقولها المبنية على المخطط.',
                                    )}
                                />
                            )}
                        </aside>
                    </section>

                    <DragOverlay>
                        {activeDragSection ? (
                            <div className="w-72 rounded-xl border border-sky-300 bg-white p-3 shadow-2xl">
                                <div className="flex items-center gap-2.5">
                                    <div className="flex h-9 w-9 items-center justify-center rounded-lg bg-sky-100 text-sky-700">
                                        <LayoutTemplate className="h-4 w-4" />
                                    </div>
                                    <div className="min-w-0">
                                        <p className="truncate text-sm font-semibold text-slate-950" dir="auto">
                                            {getSectionTitleForLanguage(activeDragSection, schemas, activeLanguage)}
                                        </p>
                                        <p className="text-xs text-slate-500">{formatSectionType(activeDragSection.type)}</p>
                                    </div>
                                </div>
                            </div>
                        ) : null}
                    </DragOverlay>
                </DndContext>
            )}
        </SuperAdminPage>
    )
}
