import { useMemo, useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import {
    BadgePercent,
    Layers3,
    RefreshCw,
    ShieldAlert,
    ShieldCheck,
    Sparkles,
} from 'lucide-react'
import { useToast } from '../../components/Toast'
import { useSuperAdminLocale } from '../i18n/useSuperAdminLocale'
import {
    GuidePanel,
    InlineNotice,
    KpiCard,
    LoadingState,
    PageHeader,
    SectionCard,
    SuperAdminPage,
    saButton,
} from '../ui/SuperAdminUI'
import CommissionFormModal from './CommissionFormModal'
import CommissionPreviewPanel from './CommissionPreviewPanel'
import CommissionTable from './CommissionTable'
import {
    createCommissionRule,
    deactivateCommissionRule,
    getCommissionCategories,
    getCommissionRules,
    getCommissionVendors,
    resolveCommissionPreview,
    updateCommissionRule,
    type CommissionRule,
    type CommissionRuleFormInput,
    type CommissionScopeType,
} from './commissionService'

const commissionRulesQueryKey = ['super-admin', 'commission', 'rules'] as const
const commissionCategoriesQueryKey = ['super-admin', 'commission', 'categories'] as const
const commissionVendorsQueryKey = ['super-admin', 'commission', 'vendors'] as const

function getErrorMessage(error: unknown, fallback: string): string {
    if (error instanceof Error && error.message) {
        return error.message
    }

    return fallback
}

function truncateId(value?: string | null): string {
    if (!value) {
        return '—'
    }

    if (value.length <= 14) {
        return value
    }

    return `${value.slice(0, 8)}...${value.slice(-4)}`
}

export default function CommissionManagementPage() {
    const queryClient = useQueryClient()
    const { showError, showSuccess } = useToast()
    const { tr, formatNumber, formatDateTime } = useSuperAdminLocale()
    const [isFormOpen, setIsFormOpen] = useState(false)
    const [editingRule, setEditingRule] = useState<CommissionRule | null>(null)
    const [defaultScopeType, setDefaultScopeType] = useState<CommissionScopeType>('Category')
    const [pendingDeactivation, setPendingDeactivation] = useState<CommissionRule | null>(null)

    const rulesQuery = useQuery({
        queryKey: commissionRulesQueryKey,
        queryFn: getCommissionRules,
        staleTime: 15_000,
        refetchOnWindowFocus: true,
    })

    const categoriesQuery = useQuery({
        queryKey: commissionCategoriesQueryKey,
        queryFn: getCommissionCategories,
        staleTime: 5 * 60_000,
    })

    const vendorsQuery = useQuery({
        queryKey: commissionVendorsQueryKey,
        queryFn: getCommissionVendors,
        staleTime: 5 * 60_000,
    })

    const upsertMutation = useMutation({
        mutationFn: async ({
            ruleId,
            input,
        }: {
            ruleId?: string
            input: CommissionRuleFormInput
        }) => {
            return ruleId
                ? updateCommissionRule(ruleId, input)
                : createCommissionRule(input)
        },
    })

    const deactivateMutation = useMutation({
        mutationFn: (ruleId: string) => deactivateCommissionRule(ruleId),
    })

    const previewMutation = useMutation({
        mutationFn: ({
            vendorId,
            categoryId,
        }: {
            vendorId?: string | null
            categoryId?: string | null
        }) => resolveCommissionPreview(vendorId, categoryId),
    })

    const rules = rulesQuery.data?.rules ?? []
    const categories = useMemo(() => categoriesQuery.data ?? [], [categoriesQuery.data])
    const vendors = useMemo(() => vendorsQuery.data ?? [], [vendorsQuery.data])
    const activeGlobalRule =
        rules.find((rule) => rule.scopeType === 'Global' && rule.isActive) ?? null
    const activeGlobalRuleCount = rules.filter((rule) => rule.scopeType === 'Global' && rule.isActive).length
    const activeRulesCount = rules.filter((rule) => rule.isActive).length
    const activeCategoryOverrides = rules.filter(
        (rule) => rule.scopeType === 'Category' && rule.isActive,
    ).length
    const activeVendorOverrides = rules.filter(
        (rule) => rule.scopeType === 'Vendor' && rule.isActive,
    ).length

    const categoryNamesById = useMemo(
        () =>
            new Map(
                categories.map((category) => [
                    category.id,
                    `${category.iconEmoji ? `${category.iconEmoji} ` : ''}${category.name}`,
                ]),
            ),
        [categories],
    )

    const vendorNamesById = useMemo(
        () => new Map(vendors.map((vendor) => [vendor.id, vendor.name])),
        [vendors],
    )

    const isInitialLoading = rulesQuery.isLoading && !rulesQuery.data
    const isRefreshing =
        rulesQuery.isFetching || categoriesQuery.isFetching || vendorsQuery.isFetching

    const formatRate = (rate: number) =>
        `${formatNumber(rate * 100, { maximumFractionDigits: 2 })}%`

    const scopeTypeLabel = (scopeType: CommissionScopeType): string => {
        if (scopeType === 'Global') return tr('Global', 'عام')
        if (scopeType === 'Category') return tr('Category', 'فئة')
        return tr('Vendor', 'بائع')
    }

    function formatAppliedRuleLabel(
        scopeType: CommissionScopeType | 'Legacy',
        scopeName?: string | null,
    ): string {
        if (scopeType === 'Legacy') {
            return tr('Applied rule: legacy fallback', 'القاعدة المطبقة: احتياطي قديم')
        }

        const label = scopeTypeLabel(scopeType)

        if (scopeType === 'Global' || !scopeName) {
            return tr(`Applied rule: ${label}`, `القاعدة المطبقة: ${label}`)
        }

        return tr(
            `Applied rule: ${label} (${scopeName})`,
            `القاعدة المطبقة: ${label} (${scopeName})`,
        )
    }

    function resolveScopeName(rule: CommissionRule): string {
        if (rule.scopeType === 'Global') {
            return tr('Platform default', 'الافتراضي للمنصة')
        }

        if (rule.scopeType === 'Category') {
            return (
                categoryNamesById.get(rule.scopeId ?? '') ??
                tr(
                    `Unknown category (${truncateId(rule.scopeId)})`,
                    `فئة غير معروفة (${truncateId(rule.scopeId)})`,
                )
            )
        }

        return (
            vendorNamesById.get(rule.scopeId ?? '') ??
            tr(
                `Unknown vendor (${truncateId(rule.scopeId)})`,
                `بائع غير معروف (${truncateId(rule.scopeId)})`,
            )
        )
    }

    function getRuleLabel(rule: CommissionRule): string {
        return formatAppliedRuleLabel(
            rule.scopeType,
            rule.scopeType === 'Global' ? null : resolveScopeName(rule),
        )
    }

    function getDeactivationState(rule: CommissionRule): { disabled: boolean; reason?: string } {
        const isProtectedGlobalRule =
            rule.isActive &&
            rule.scopeType === 'Global' &&
            activeGlobalRuleCount === 1

        if (!isProtectedGlobalRule) {
            return { disabled: false }
        }

        return {
            disabled: true,
            reason: tr(
                'You must always keep one active global commission rule.',
                'يجب الإبقاء دائمًا على قاعدة عمولة عامة نشطة واحدة.',
            ),
        }
    }

    function openCreateModal(scopeType: CommissionScopeType) {
        setEditingRule(null)
        setDefaultScopeType(scopeType)
        setIsFormOpen(true)
    }

    function openEditModal(rule: CommissionRule) {
        setEditingRule(rule)
        setDefaultScopeType(rule.scopeType)
        setIsFormOpen(true)
    }

    function closeFormModal() {
        setIsFormOpen(false)
        setEditingRule(null)
    }

    async function refreshAll() {
        await Promise.all([
            rulesQuery.refetch(),
            categoriesQuery.refetch(),
            vendorsQuery.refetch(),
        ])
    }

    async function handleSubmitRule(input: CommissionRuleFormInput) {
        try {
            await upsertMutation.mutateAsync({
                ruleId: editingRule?.id,
                input,
            })

            await queryClient.invalidateQueries({ queryKey: commissionRulesQueryKey })
            previewMutation.reset()
            showSuccess(
                editingRule
                    ? tr('Commission rule updated successfully.', 'تم تحديث قاعدة العمولة بنجاح.')
                    : tr('Commission rule created successfully.', 'تم إنشاء قاعدة العمولة بنجاح.'),
            )
            closeFormModal()
        } catch (error) {
            showError(
                getErrorMessage(
                    error,
                    tr('Unable to save commission rule.', 'تعذّر حفظ قاعدة العمولة.'),
                ),
            )
        }
    }

    async function handleConfirmDeactivate() {
        if (!pendingDeactivation) {
            return
        }

        const deactivationState = getDeactivationState(pendingDeactivation)
        if (deactivationState.disabled) {
            showError(
                deactivationState.reason ??
                    tr(
                        'This commission rule cannot be deactivated.',
                        'لا يمكن تعطيل قاعدة العمولة هذه.',
                    ),
            )
            setPendingDeactivation(null)
            return
        }

        try {
            await deactivateMutation.mutateAsync(pendingDeactivation.id)
            await queryClient.invalidateQueries({ queryKey: commissionRulesQueryKey })
            previewMutation.reset()
            showSuccess(
                tr('Commission rule deactivated successfully.', 'تم تعطيل قاعدة العمولة بنجاح.'),
            )
            setPendingDeactivation(null)
        } catch (error) {
            showError(
                getErrorMessage(
                    error,
                    tr('Unable to deactivate commission rule.', 'تعذّر تعطيل قاعدة العمولة.'),
                ),
            )
        }
    }

    async function handlePreview(vendorId?: string | null, categoryId?: string | null) {
        try {
            await previewMutation.mutateAsync({ vendorId, categoryId })
        } catch (error) {
            showError(
                getErrorMessage(
                    error,
                    tr('Unable to preview commission resolution.', 'تعذّر معاينة احتساب العمولة.'),
                ),
            )
        }
    }

    const guide = {
        en: {
            title: 'Commission control guide',
            purpose:
                'Defines the platform commission authority: the global default rate plus category and vendor overrides that decide how much of each product subtotal CommerceHub keeps.',
            when:
                'Use it when launching a category or vendor, renegotiating commercial terms, auditing why an order earned a particular commission, or checking which rule a checkout would resolve before it happens.',
            steps: [
                'Confirm there is exactly one active global rule; it is the platform fallback.',
                'Create a category override when every vendor in that category shares the same terms.',
                'Create a vendor override for negotiated rates that must beat category and global rules.',
                'Use the resolution preview with a vendor and category to verify the winning rule before relying on it.',
                'Edit or deactivate rules instead of deleting them; inactive rules stay auditable.',
            ],
            statuses: [
                { label: 'Active', description: 'Participates in commission resolution for new orders.' },
                { label: 'Inactive', description: 'Retained for audit and ignored by new checkouts.' },
                { label: 'Global', description: 'Platform default when nothing more specific matches.' },
                { label: 'Category', description: 'Applies to all vendors in one category.' },
                { label: 'Vendor', description: 'Highest-precedence override for one vendor.' },
                { label: 'Priority', description: 'Breaks ties only within the same scope and target.' },
            ],
            safety: [
                'At least one active global rule must remain; the last one cannot be deactivated.',
                'Changes apply to new orders only. Historical commission snapshots are never recalculated.',
                'Commission applies to the product subtotal, not to shipping.',
                'The preview is read-only; it does not create orders or change rules.',
                'Every edit affects live checkout immediately, so confirm commercial terms before saving.',
            ],
        },
        ar: {
            title: 'دليل التحكم في العمولة',
            purpose:
                'يحدد صلاحية عمولة المنصة: النسبة العامة الافتراضية إضافة إلى تجاوزات الفئات والبائعين التي تحدد نصيب CommerceHub من مجموع منتجات كل طلب.',
            when:
                'استخدمه عند إطلاق فئة أو بائع، أو إعادة التفاوض على الشروط التجارية، أو تدقيق سبب عمولة طلب معين، أو التحقق من القاعدة التي سيطبّقها الدفع قبل حدوثه.',
            steps: [
                'تأكد من وجود قاعدة عامة نشطة واحدة فقط؛ فهي احتياطي المنصة.',
                'أنشئ تجاوزًا للفئة عندما تشترك كل بائعي تلك الفئة في الشروط نفسها.',
                'أنشئ تجاوزًا للبائع للأسعار المتفاوض عليها التي يجب أن تتجاوز قواعد الفئة والعام.',
                'استخدم معاينة الاحتساب مع بائع وفئة للتحقق من القاعدة الفائزة قبل الاعتماد عليها.',
                'عدّل القواعد أو عطّلها بدل حذفها؛ تبقى القواعد غير النشطة قابلة للتدقيق.',
            ],
            statuses: [
                { label: 'نشطة', description: 'تشارك في احتساب العمولة للطلبات الجديدة.' },
                { label: 'غير نشطة', description: 'محفوظة للتدقيق ومتجاهلة في عمليات الدفع الجديدة.' },
                { label: 'عامة', description: 'الافتراضي للمنصة عند عدم تطابق قاعدة أكثر تخصصًا.' },
                { label: 'فئة', description: 'تُطبَّق على كل بائعي فئة واحدة.' },
                { label: 'بائع', description: 'تجاوز بأعلى أسبقية لبائع واحد.' },
                { label: 'الأولوية', description: 'تحسم التعادل فقط داخل النطاق والهدف نفسيهما.' },
            ],
            safety: [
                'يجب الإبقاء على قاعدة عامة نشطة واحدة على الأقل؛ ولا يمكن تعطيل الأخيرة.',
                'تُطبَّق التغييرات على الطلبات الجديدة فقط، ولا تُعاد حسابات لقطات العمولة السابقة.',
                'تُطبَّق العمولة على مجموع المنتجات الفرعي وليس على الشحن.',
                'المعاينة للقراءة فقط؛ لا تنشئ طلبات ولا تغيّر القواعد.',
                'كل تعديل يؤثر في الدفع المباشر فورًا، لذا تأكد من الشروط التجارية قبل الحفظ.',
            ],
        },
    }

    if (rulesQuery.isError && !rulesQuery.data) {
        return (
            <SuperAdminPage>
                <PageHeader
                    eyebrow={tr('Super Admin · Finance', 'الإدارة العليا · المالية')}
                    title={tr('Commission control', 'التحكم في العمولة')}
                    description={tr(
                        'Global default, category and vendor overrides, and resolution preview.',
                        'النسبة العامة وتجاوزات الفئات والبائعين ومعاينة الاحتساب.',
                    )}
                />
                <InlineNotice tone="rose" title={tr('Commission control unavailable', 'التحكم في العمولة غير متاح')}>
                    <p>
                        {getErrorMessage(
                            rulesQuery.error,
                            tr('Unable to load commission rules.', 'تعذّر تحميل قواعد العمولة.'),
                        )}
                    </p>
                    <button
                        type="button"
                        className={`${saButton.secondary} mt-2`}
                        onClick={() => void refreshAll()}
                    >
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
                    eyebrow={tr('Super Admin · Finance', 'الإدارة العليا · المالية')}
                    title={tr('Commission control', 'التحكم في العمولة')}
                    description={tr(
                        'Control the global default, category and vendor overrides, and verify which rule a checkout resolves.',
                        'تحكم في النسبة العامة وتجاوزات الفئات والبائعين، وتحقق من القاعدة التي يحتسبها الدفع.',
                    )}
                    actions={
                        <>
                            <button
                                type="button"
                                className={saButton.primary}
                                onClick={() => openCreateModal('Category')}
                            >
                                <Sparkles className="h-4 w-4" />
                                {tr('New override rule', 'قاعدة تجاوز جديدة')}
                            </button>
                            <button
                                type="button"
                                className={saButton.secondary}
                                onClick={() => {
                                    if (activeGlobalRule) {
                                        openEditModal(activeGlobalRule)
                                        return
                                    }

                                    openCreateModal('Global')
                                }}
                            >
                                <ShieldCheck className="h-4 w-4" />
                                {activeGlobalRule
                                    ? tr('Edit global rule', 'تعديل القاعدة العامة')
                                    : tr('Create global rule', 'إنشاء قاعدة عامة')}
                            </button>
                            <button
                                type="button"
                                className={saButton.secondary}
                                disabled={isRefreshing}
                                onClick={() => void refreshAll()}
                            >
                                <RefreshCw className={`h-4 w-4 ${isRefreshing ? 'animate-spin' : ''}`} />
                                {isRefreshing ? tr('Refreshing', 'جارٍ التحديث') : tr('Refresh', 'تحديث')}
                            </button>
                        </>
                    }
                />

                <div className="grid gap-3 sm:grid-cols-2 xl:grid-cols-4">
                    <KpiCard
                        label={tr('Active rules', 'القواعد النشطة')}
                        value={formatNumber(activeRulesCount)}
                        hint={tr('Used by new checkouts', 'تُستخدم في عمليات الدفع الجديدة')}
                        icon={<BadgePercent className="h-4 w-4 text-emerald-700" />}
                        tone="emerald"
                        isLoading={isInitialLoading}
                    />
                    <KpiCard
                        label={tr('Category overrides', 'تجاوزات الفئات')}
                        value={formatNumber(activeCategoryOverrides)}
                        hint={tr('Active category-scoped rules', 'قواعد نشطة على مستوى الفئة')}
                        icon={<Layers3 className="h-4 w-4 text-violet-700" />}
                        tone="violet"
                        isLoading={isInitialLoading}
                    />
                    <KpiCard
                        label={tr('Vendor overrides', 'تجاوزات البائعين')}
                        value={formatNumber(activeVendorOverrides)}
                        hint={tr('Active vendor-scoped rules', 'قواعد نشطة على مستوى البائع')}
                        icon={<ShieldCheck className="h-4 w-4 text-sky-700" />}
                        tone="sky"
                        isLoading={isInitialLoading}
                    />
                    <KpiCard
                        label={tr('Global rate', 'النسبة العامة')}
                        value={activeGlobalRule ? formatRate(activeGlobalRule.rate) : tr('None', 'لا يوجد')}
                        hint={
                            activeGlobalRule
                                ? tr('Active platform default', 'الافتراضي النشط للمنصة')
                                : tr('Legacy fallback in effect', 'الاحتياطي القديم مطبّق')
                        }
                        icon={<ShieldAlert className="h-4 w-4 text-amber-700" />}
                        tone={activeGlobalRule ? 'slate' : 'amber'}
                        isLoading={isInitialLoading}
                    />
                </div>

                <div className="grid gap-3 sm:grid-cols-2 xl:grid-cols-4">
                    <div className="rounded-xl border border-slate-200 bg-white px-3.5 py-3 shadow-sm">
                        <p className="text-[10px] font-semibold uppercase tracking-[0.16em] text-slate-500">
                            {tr('API timestamp', 'وقت واجهة البرمجة')}
                        </p>
                        <p className="mt-1 text-sm font-semibold text-slate-900" dir="auto">
                            {formatDateTime(rulesQuery.data?.meta?.timestamp, tr('Pending', 'قيد الانتظار'))}
                        </p>
                    </div>
                    <div className="rounded-xl border border-slate-200 bg-white px-3.5 py-3 shadow-sm">
                        <p className="text-[10px] font-semibold uppercase tracking-[0.16em] text-slate-500">
                            {tr('Request ID', 'معرّف الطلب')}
                        </p>
                        <p className="mt-1 truncate font-mono text-xs font-semibold text-slate-900" dir="ltr">
                            {rulesQuery.data?.meta?.requestId ?? tr('Pending', 'قيد الانتظار')}
                        </p>
                    </div>
                    <div className="rounded-xl border border-slate-200 bg-white px-3.5 py-3 shadow-sm">
                        <p className="text-[10px] font-semibold uppercase tracking-[0.16em] text-slate-500">
                            {tr('Client sync', 'مزامنة العميل')}
                        </p>
                        <p className="mt-1 text-sm font-semibold text-slate-900" dir="auto">
                            {rulesQuery.dataUpdatedAt
                                ? new Date(rulesQuery.dataUpdatedAt).toLocaleTimeString()
                                : tr('Not synced yet', 'لم تتم المزامنة بعد')}
                        </p>
                    </div>
                    <div className="rounded-xl border border-slate-200 bg-white px-3.5 py-3 shadow-sm">
                        <p className="text-[10px] font-semibold uppercase tracking-[0.16em] text-slate-500">
                            {tr('Total configured rules', 'إجمالي القواعد المهيأة')}
                        </p>
                        <p className="mt-1 text-sm font-semibold text-slate-900" dir="auto">
                            {formatNumber(rules.length)}
                        </p>
                    </div>
                </div>

                <GuidePanel guide={guide} />

                <div className="grid gap-4 xl:grid-cols-[0.85fr_1.15fr]">
                    <SectionCard
                        title={tr('Global default rate', 'النسبة العامة الافتراضية')}
                        description={tr(
                            'Applies when no vendor or category override matches the order context.',
                            'تُطبَّق عندما لا يتطابق أي تجاوز بائع أو فئة مع سياق الطلب.',
                        )}
                        headerAside={
                            <span className="inline-flex items-center gap-1.5 rounded-full border border-slate-200 bg-slate-50 px-2.5 py-1 text-xs font-semibold text-slate-700">
                                <ShieldCheck className="h-3.5 w-3.5" />
                                {tr('Required', 'مطلوبة')}
                            </span>
                        }
                    >
                        {isInitialLoading ? (
                            <LoadingState label={tr('Loading global rule…', 'جارٍ تحميل القاعدة العامة…')} />
                        ) : activeGlobalRule ? (
                            <div className="space-y-3">
                                <div className="rounded-xl border border-emerald-200 bg-emerald-50 p-4">
                                    <p className="text-[10px] font-semibold uppercase tracking-[0.14em] text-emerald-700">
                                        {tr('Active global rate', 'النسبة العامة النشطة')}
                                    </p>
                                    <p className="mt-1.5 text-4xl font-semibold text-emerald-950" dir="auto">
                                        {formatRate(activeGlobalRule.rate)}
                                    </p>
                                    <div className="mt-3 grid gap-2 sm:grid-cols-2">
                                        <div className="rounded-lg border border-white/80 bg-white px-3 py-2 shadow-sm">
                                            <p className="text-[10px] font-semibold uppercase tracking-[0.14em] text-slate-500">
                                                {tr('Last updated', 'آخر تحديث')}
                                            </p>
                                            <p className="mt-0.5 text-sm font-semibold text-slate-900" dir="auto">
                                                {formatDateTime(
                                                    activeGlobalRule.updatedAt ?? activeGlobalRule.createdAt,
                                                    tr('Unavailable', 'غير متاح'),
                                                )}
                                            </p>
                                        </div>
                                        <div className="rounded-lg border border-white/80 bg-white px-3 py-2 shadow-sm">
                                            <p className="text-[10px] font-semibold uppercase tracking-[0.14em] text-slate-500">
                                                {tr('Priority', 'الأولوية')}
                                            </p>
                                            <p className="mt-0.5 text-sm font-semibold text-slate-900" dir="auto">
                                                {formatNumber(activeGlobalRule.priority)}
                                            </p>
                                        </div>
                                    </div>
                                </div>

                                <div className="rounded-xl border border-slate-200 bg-slate-50 px-3.5 py-2.5">
                                    <p className="text-[10px] font-semibold uppercase tracking-[0.14em] text-slate-500">
                                        {tr('Active rule ID', 'معرّف القاعدة النشطة')}
                                    </p>
                                    <p className="mt-1 break-all font-mono text-xs text-slate-700" dir="ltr">
                                        {activeGlobalRule.id}
                                    </p>
                                </div>

                                <button
                                    type="button"
                                    className={saButton.primary}
                                    onClick={() => openEditModal(activeGlobalRule)}
                                >
                                    <Layers3 className="h-4 w-4" />
                                    {tr('Edit global rate', 'تعديل النسبة العامة')}
                                </button>
                            </div>
                        ) : (
                            <div className="space-y-3">
                                <InlineNotice tone="amber" title={tr('No active global rule', 'لا توجد قاعدة عامة نشطة')}>
                                    {tr(
                                        'The backend falls back to the legacy static commission configuration until a global rule is created.',
                                        'يعود الخادم إلى إعداد العمولة الثابت القديم حتى يتم إنشاء قاعدة عامة.',
                                    )}
                                </InlineNotice>

                                <button
                                    type="button"
                                    className={saButton.primary}
                                    onClick={() => openCreateModal('Global')}
                                >
                                    <ShieldCheck className="h-4 w-4" />
                                    {tr('Create global rule', 'إنشاء قاعدة عامة')}
                                </button>
                            </div>
                        )}
                    </SectionCard>

                    <CommissionPreviewPanel
                        categories={categories}
                        vendors={vendors}
                        errorMessage={
                            previewMutation.isError
                                ? getErrorMessage(
                                      previewMutation.error,
                                      tr(
                                          'Unable to preview commission resolution.',
                                          'تعذّر معاينة احتساب العمولة.',
                                      ),
                                  )
                                : null
                        }
                        getAppliedRuleLabel={(result) => {
                            if (result.usedLegacyFallback) {
                                return formatAppliedRuleLabel('Legacy')
                            }

                            const scopeType =
                                result.scopeType ??
                                (result.source === 'Vendor' || result.source === 'Category' || result.source === 'Global'
                                    ? result.source
                                    : null)

                            if (!scopeType) {
                                return tr('Applied rule: unavailable', 'القاعدة المطبقة: غير متاحة')
                            }

                            const scopeName =
                                scopeType === 'Category'
                                    ? categoryNamesById.get(result.scopeId ?? '')
                                    : scopeType === 'Vendor'
                                      ? vendorNamesById.get(result.scopeId ?? '')
                                      : null

                            return formatAppliedRuleLabel(scopeType, scopeName)
                        }}
                        isLoadingLookups={categoriesQuery.isLoading || vendorsQuery.isLoading}
                        isSubmitting={previewMutation.isPending}
                        result={previewMutation.data}
                        onPreview={handlePreview}
                    />
                </div>

                {categoriesQuery.isError || vendorsQuery.isError ? (
                    <InlineNotice tone="amber" title={tr('Lookup names unavailable', 'أسماء القوائم غير متاحة')}>
                        {tr(
                            'Scope names could not be fully hydrated from lookup endpoints, so the table falls back to stored IDs where necessary.',
                            'تعذّر تحميل أسماء النطاقات بالكامل من نقاط البحث، لذا يعود الجدول إلى المعرّفات المخزنة عند الحاجة.',
                        )}
                    </InlineNotice>
                ) : null}

                {rulesQuery.isError && rulesQuery.data ? (
                    <InlineNotice tone="amber" title={tr('Cached rules shown', 'تُعرض القواعد المخزنة')}>
                        {tr(
                            'The latest commission authority refresh failed, so the most recent cached rules are shown.',
                            'فشل تحديث صلاحية العمولة الأخير، لذا تُعرض أحدث القواعد المخزنة.',
                        )}
                    </InlineNotice>
                ) : null}

                <CommissionTable
                    rules={rules}
                    isLoading={rulesQuery.isLoading}
                    activeActionRuleId={deactivateMutation.isPending ? pendingDeactivation?.id ?? null : null}
                    getAppliedRuleLabel={getRuleLabel}
                    getDeactivationState={getDeactivationState}
                    onEdit={openEditModal}
                    onDeactivate={setPendingDeactivation}
                    onCreateFirstRule={() => openCreateModal('Global')}
                    resolveScopeName={resolveScopeName}
                />
            </SuperAdminPage>

            <CommissionFormModal
                isOpen={isFormOpen}
                isSubmitting={upsertMutation.isPending}
                rule={editingRule}
                defaultScopeType={defaultScopeType}
                categories={categories}
                vendors={vendors}
                onClose={closeFormModal}
                onSubmit={handleSubmitRule}
            />

            {pendingDeactivation ? (
                <div className="fixed inset-0 z-50 flex items-center justify-center bg-slate-950/55 px-4 py-6 backdrop-blur-sm">
                    <div className="w-full max-w-lg overflow-y-auto rounded-2xl border border-white/10 bg-white shadow-2xl">
                        <div className="border-b border-slate-100 px-4 py-4 sm:px-5">
                            <p className="text-[11px] font-semibold uppercase tracking-[0.18em] text-rose-600">
                                {tr('Confirm deactivation', 'تأكيد التعطيل')}
                            </p>
                            <h2 className="mt-1.5 text-xl font-semibold text-slate-950">
                                {tr('Deactivate this commission rule?', 'هل تريد تعطيل قاعدة العمولة هذه؟')}
                            </h2>
                            <p className="mt-1 text-sm leading-6 text-slate-500">
                                {tr(
                                    'The rule stays visible for audit, but it will no longer participate in future commission resolution.',
                                    'تبقى القاعدة ظاهرة للتدقيق، لكنها لن تشارك في احتساب العمولة مستقبلًا.',
                                )}
                            </p>
                        </div>

                        <div className="space-y-3 px-4 py-4 sm:px-5">
                            <div className="rounded-xl border border-slate-200 bg-slate-50 px-3.5 py-3">
                                <p className="text-[10px] font-semibold uppercase tracking-[0.14em] text-slate-500">
                                    {tr('Target', 'الهدف')}
                                </p>
                                <p className="mt-1 text-sm font-semibold text-slate-900">
                                    {scopeTypeLabel(pendingDeactivation.scopeType)} —{' '}
                                    {resolveScopeName(pendingDeactivation)}
                                </p>
                                <p className="mt-0.5 text-sm text-slate-600" dir="auto">
                                    {tr(
                                        `${formatRate(pendingDeactivation.rate)} at priority ${formatNumber(
                                            pendingDeactivation.priority,
                                        )}`,
                                        `${formatRate(pendingDeactivation.rate)} بأولوية ${formatNumber(
                                            pendingDeactivation.priority,
                                        )}`,
                                    )}
                                </p>
                                {pendingDeactivation.scopeType === 'Global' && activeGlobalRuleCount === 1 ? (
                                    <p className="mt-2 rounded-lg border border-amber-200 bg-amber-50 px-3 py-2 text-xs font-medium text-amber-900">
                                        {tr(
                                            'You must always keep one active global commission rule. This action will be blocked.',
                                            'يجب الإبقاء دائمًا على قاعدة عمولة عامة نشطة واحدة. سيُحجب هذا الإجراء.',
                                        )}
                                    </p>
                                ) : null}
                            </div>

                            <div className="flex flex-col-reverse gap-2 sm:flex-row sm:justify-end">
                                <button
                                    type="button"
                                    className={saButton.secondary}
                                    onClick={() => setPendingDeactivation(null)}
                                >
                                    {tr('Cancel', 'إلغاء')}
                                </button>
                                <button
                                    type="button"
                                    className={saButton.danger}
                                    disabled={deactivateMutation.isPending}
                                    onClick={() => void handleConfirmDeactivate()}
                                >
                                    {deactivateMutation.isPending
                                        ? tr('Deactivating…', 'جارٍ التعطيل…')
                                        : tr('Deactivate rule', 'تعطيل القاعدة')}
                                </button>
                            </div>
                        </div>
                    </div>
                </div>
            ) : null}
        </>
    )
}
