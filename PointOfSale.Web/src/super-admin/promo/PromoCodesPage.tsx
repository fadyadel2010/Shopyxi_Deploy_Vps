import { useEffect, useMemo, useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import {
    Calculator,
    RefreshCw,
    ShieldCheck,
    Sparkles,
    TicketPercent,
} from 'lucide-react'
import { useToast } from '../../components/Toast'
import { useSuperAdminLocale } from '../i18n/useSuperAdminLocale'
import {
    EmptyState,
    GuidePanel,
    InlineNotice,
    KpiCard,
    LoadingState,
    PageHeader,
    SectionCard,
    StatusBadge,
    SuperAdminPage,
    saButton,
    saInput,
    saTable,
} from '../ui/SuperAdminUI'
import {
    activatePromoCode,
    createPromoCode,
    deactivatePromoCode,
    getPromoCategories,
    getPromoCodes,
    getPromoVendors,
    previewPromoCode,
    updatePromoCode,
    type PromoCategoryOption,
    type PromoCodeFormInput,
    type PromoDiscountType,
    type PromoScopeType,
    type PromoVendorOption,
    type SuperAdminPromoCode,
} from './promoService'

const promoCodesQueryKey = ['super-admin', 'promo-codes'] as const
const promoCategoriesQueryKey = ['super-admin', 'promo-codes', 'categories'] as const
const promoVendorsQueryKey = ['super-admin', 'promo-codes', 'vendors'] as const

type PromoFormState = {
    code: string
    discountType: PromoDiscountType
    discountValue: string
    maxDiscountAmount: string
    usageLimit: string
    maxUsagePerCustomer: string
    expiryDate: string
    isActive: boolean
    scopeType: PromoScopeType
    scopeId: string
}

function getErrorMessage(error: unknown, fallback: string): string {
    return error instanceof Error && error.message ? error.message : fallback
}

function getPromoScopeType(promo: SuperAdminPromoCode): PromoScopeType {
    if (promo.vendorId) {
        return 'Vendor'
    }

    if (promo.categoryId) {
        return 'Category'
    }

    return 'Global'
}

function getPromoScopeLabel(
    promo: SuperAdminPromoCode,
    categoriesById: Map<string, string>,
    vendorsById: Map<string, string>,
): string {
    if (promo.vendorId) {
        return `Vendor - ${vendorsById.get(promo.vendorId) ?? promo.vendorId}`
    }

    if (promo.categoryId) {
        return `Category - ${categoriesById.get(promo.categoryId) ?? promo.categoryId}`
    }

    return 'Global'
}

type PromoHealthKey = 'Active' | 'Inactive' | 'Expired' | 'LimitReached'

function getPromoHealth(promo: SuperAdminPromoCode): { key: PromoHealthKey; tone: 'emerald' | 'slate' | 'rose' | 'amber' } {
    const expiryTime = promo.expiryDate ? new Date(promo.expiryDate).getTime() : Number.NaN
    const isExpired = Number.isFinite(expiryTime) && expiryTime <= Date.now()
    const isUsageExhausted =
        typeof promo.usageLimit === 'number' && promo.usageLimit > 0 && promo.usedCount >= promo.usageLimit

    if (!promo.isActive) {
        return { key: 'Inactive', tone: 'slate' }
    }

    if (isExpired) {
        return { key: 'Expired', tone: 'rose' }
    }

    if (isUsageExhausted) {
        return { key: 'LimitReached', tone: 'amber' }
    }

    return { key: 'Active', tone: 'emerald' }
}

function toDateTimeLocalInput(value?: string | null): string {
    if (!value) {
        return ''
    }

    const date = new Date(value)
    if (Number.isNaN(date.getTime())) {
        return ''
    }

    const offsetMinutes = date.getTimezoneOffset()
    const local = new Date(date.getTime() - offsetMinutes * 60_000)
    return local.toISOString().slice(0, 16)
}

function toIsoStringOrNull(value: string): string | null {
    if (!value.trim()) {
        return null
    }

    const date = new Date(value)
    if (Number.isNaN(date.getTime())) {
        return null
    }

    return date.toISOString()
}

function buildFormState(promo: SuperAdminPromoCode | null): PromoFormState {
    if (!promo) {
        return {
            code: '',
            discountType: 'Percentage',
            discountValue: '',
            maxDiscountAmount: '',
            usageLimit: '',
            maxUsagePerCustomer: '',
            expiryDate: '',
            isActive: true,
            scopeType: 'Global',
            scopeId: '',
        }
    }

    return {
        code: promo.code,
        discountType: promo.discountType,
        discountValue: String(promo.discountValue),
        maxDiscountAmount: typeof promo.maxDiscountAmount === 'number' ? String(promo.maxDiscountAmount) : '',
        usageLimit: typeof promo.usageLimit === 'number' ? String(promo.usageLimit) : '',
        maxUsagePerCustomer: typeof promo.maxUsagePerCustomer === 'number' ? String(promo.maxUsagePerCustomer) : '',
        expiryDate: toDateTimeLocalInput(promo.expiryDate),
        isActive: promo.isActive,
        scopeType: getPromoScopeType(promo),
        scopeId: promo.vendorId ?? promo.categoryId ?? '',
    }
}

function PromoCodeFormModal({
    isOpen,
    isSubmitting,
    promo,
    categories,
    vendors,
    onClose,
    onSubmit,
}: {
    isOpen: boolean
    isSubmitting: boolean
    promo: SuperAdminPromoCode | null
    categories: PromoCategoryOption[]
    vendors: PromoVendorOption[]
    onClose: () => void
    onSubmit: (input: PromoCodeFormInput) => Promise<void>
}) {
    const [form, setForm] = useState<PromoFormState>(() => buildFormState(promo))
    const [formError, setFormError] = useState<string | null>(null)
    const { tr } = useSuperAdminLocale()

    useEffect(() => {
        if (!isOpen) {
            return
        }

        setForm(buildFormState(promo))
        setFormError(null)
    }, [isOpen, promo])

    if (!isOpen) {
        return null
    }

    const scopeOptions = form.scopeType === 'Vendor' ? vendors : categories

    async function handleSubmit() {
        const normalizedCode = form.code.trim().toUpperCase()
        const discountValue = Number(form.discountValue)
        const maxDiscountAmount = form.maxDiscountAmount.trim() ? Number(form.maxDiscountAmount) : null
        const usageLimit = form.usageLimit.trim() ? Number(form.usageLimit) : null
        const maxUsagePerCustomer = form.maxUsagePerCustomer.trim() ? Number(form.maxUsagePerCustomer) : null

        if (!normalizedCode) {
            setFormError(tr('Promo code is required.', 'رمز الخصم مطلوب.'))
            return
        }

        if (!Number.isFinite(discountValue) || discountValue <= 0) {
            setFormError(tr('Discount value must be greater than zero.', 'يجب أن تكون قيمة الخصم أكبر من صفر.'))
            return
        }

        if (form.discountType === 'Percentage' && discountValue > 100) {
            setFormError(tr('Percentage discount cannot exceed 100%.', 'لا يمكن أن يتجاوز خصم النسبة 100%.'))
            return
        }

        if (maxDiscountAmount !== null && (!Number.isFinite(maxDiscountAmount) || maxDiscountAmount <= 0)) {
            setFormError(
                tr(
                    'Max discount must be greater than zero when provided.',
                    'يجب أن يكون الحد الأقصى للخصم أكبر من صفر عند إدخاله.',
                ),
            )
            return
        }

        if (usageLimit !== null && (!Number.isFinite(usageLimit) || usageLimit <= 0)) {
            setFormError(
                tr(
                    'Usage limit must be greater than zero when provided.',
                    'يجب أن يكون حد الاستخدام أكبر من صفر عند إدخاله.',
                ),
            )
            return
        }

        if (maxUsagePerCustomer !== null && (!Number.isFinite(maxUsagePerCustomer) || maxUsagePerCustomer <= 0)) {
            setFormError(
                tr(
                    'Per-customer usage limit must be greater than zero when provided.',
                    'يجب أن يكون حد الاستخدام لكل عميل أكبر من صفر عند إدخاله.',
                ),
            )
            return
        }

        if (usageLimit !== null && maxUsagePerCustomer !== null && maxUsagePerCustomer > usageLimit) {
            setFormError(
                tr(
                    'Per-customer usage limit cannot exceed the total usage limit.',
                    'لا يمكن أن يتجاوز حد الاستخدام لكل عميل حد الاستخدام الإجمالي.',
                ),
            )
            return
        }

        if (form.scopeType !== 'Global' && !form.scopeId) {
            setFormError(
                form.scopeType === 'Vendor'
                    ? tr('Select a vendor target.', 'اختر بائعًا مستهدفًا.')
                    : tr('Select a category target.', 'اختر فئة مستهدفة.'),
            )
            return
        }

        setFormError(null)

        await onSubmit({
            code: normalizedCode,
            discountType: form.discountType,
            discountValue,
            maxDiscountAmount,
            usageLimit: usageLimit === null ? null : Math.floor(usageLimit),
            maxUsagePerCustomer: maxUsagePerCustomer === null ? null : Math.floor(maxUsagePerCustomer),
            expiryDate: toIsoStringOrNull(form.expiryDate),
            isActive: form.isActive,
            scopeType: form.scopeType,
            scopeId: form.scopeType === 'Global' ? null : form.scopeId,
        })
    }

    return (
        <div className="fixed inset-0 z-50 flex items-center justify-center bg-slate-950/55 px-4 py-6 backdrop-blur-sm">
            <div className="max-h-[92vh] w-full max-w-2xl overflow-y-auto rounded-2xl border border-slate-200 bg-white shadow-2xl">
                <div className="border-b border-slate-100 px-4 py-4 sm:px-5">
                    <p className="text-[11px] font-semibold uppercase tracking-[0.18em] text-emerald-700">
                        {promo ? tr('Edit promo code', 'تعديل رمز الخصم') : tr('Create promo code', 'إنشاء رمز خصم')}
                    </p>
                    <h2 className="mt-1.5 text-xl font-semibold text-slate-950">
                        {promo
                            ? tr(`Update ${promo.code}`, `تحديث ${promo.code}`)
                            : tr('Configure a commission-only promo', 'تهيئة خصم على العمولة فقط')}
                    </h2>
                    <p className="mt-1 text-sm leading-6 text-slate-500">
                        {tr(
                            'Promos reduce platform commission only. Customer totals and vendor payouts do not change.',
                            'تخفّض العروض عمولة المنصة فقط. لا تتغير إجماليات العميل أو مستحقات البائع.',
                        )}
                    </p>
                </div>

                <div className="space-y-4 px-4 py-4 sm:px-5">
                    <div className="grid gap-3 md:grid-cols-2">
                        <label className="space-y-1.5">
                            <span className="text-[10px] font-semibold uppercase tracking-[0.16em] text-slate-500">
                                {tr('Promo code', 'رمز الخصم')}
                            </span>
                            <input
                                value={form.code}
                                onChange={(event) =>
                                    setForm((current) => ({ ...current, code: event.target.value.toUpperCase() }))
                                }
                                placeholder={tr('e.g. SUMMER25', 'مثال: SUMMER25')}
                                className={`${saInput} uppercase`}
                                dir="ltr"
                            />
                        </label>

                        <label className="space-y-1.5">
                            <span className="text-[10px] font-semibold uppercase tracking-[0.16em] text-slate-500">
                                {tr('Discount type', 'نوع الخصم')}
                            </span>
                            <select
                                value={form.discountType}
                                onChange={(event) =>
                                    setForm((current) => ({
                                        ...current,
                                        discountType: event.target.value as PromoDiscountType,
                                    }))
                                }
                                className={saInput}
                            >
                                <option value="Percentage">{tr('Percentage', 'نسبة مئوية')}</option>
                                <option value="Fixed">{tr('Fixed amount', 'مبلغ ثابت')}</option>
                            </select>
                        </label>

                        <label className="space-y-1.5">
                            <span className="text-[10px] font-semibold uppercase tracking-[0.16em] text-slate-500">
                                {form.discountType === 'Percentage'
                                    ? tr('Discount percent', 'نسبة الخصم')
                                    : tr('Discount amount', 'مبلغ الخصم')}
                            </span>
                            <input
                                value={form.discountValue}
                                onChange={(event) =>
                                    setForm((current) => ({ ...current, discountValue: event.target.value }))
                                }
                                type="number"
                                min="0"
                                step="0.01"
                                dir="ltr"
                                placeholder={form.discountType === 'Percentage' ? '10' : '50'}
                                className={saInput}
                            />
                        </label>

                        <label className="space-y-1.5">
                            <span className="text-[10px] font-semibold uppercase tracking-[0.16em] text-slate-500">
                                {tr('Max discount amount', 'الحد الأقصى لمبلغ الخصم')}
                            </span>
                            <input
                                value={form.maxDiscountAmount}
                                onChange={(event) =>
                                    setForm((current) => ({ ...current, maxDiscountAmount: event.target.value }))
                                }
                                type="number"
                                min="0"
                                step="0.01"
                                dir="ltr"
                                placeholder={tr('Optional cap', 'حد اختياري')}
                                className={saInput}
                            />
                        </label>

                        <label className="space-y-1.5">
                            <span className="text-[10px] font-semibold uppercase tracking-[0.16em] text-slate-500">
                                {tr('Usage limit', 'حد الاستخدام')}
                            </span>
                            <input
                                value={form.usageLimit}
                                onChange={(event) =>
                                    setForm((current) => ({ ...current, usageLimit: event.target.value }))
                                }
                                type="number"
                                min="1"
                                step="1"
                                dir="ltr"
                                placeholder={tr('Optional total uses', 'إجمالي استخدامات اختياري')}
                                className={saInput}
                            />
                        </label>

                        <label className="space-y-1.5">
                            <span className="text-[10px] font-semibold uppercase tracking-[0.16em] text-slate-500">
                                {tr('Per-customer limit', 'الحد لكل عميل')}
                            </span>
                            <input
                                value={form.maxUsagePerCustomer}
                                onChange={(event) =>
                                    setForm((current) => ({ ...current, maxUsagePerCustomer: event.target.value }))
                                }
                                type="number"
                                min="1"
                                step="1"
                                dir="ltr"
                                placeholder={tr('Optional per-customer cap', 'حد اختياري لكل عميل')}
                                className={saInput}
                            />
                        </label>

                        <label className="space-y-1.5 md:col-span-2">
                            <span className="text-[10px] font-semibold uppercase tracking-[0.16em] text-slate-500">
                                {tr('Expiry date', 'تاريخ الانتهاء')}
                            </span>
                            <input
                                value={form.expiryDate}
                                onChange={(event) =>
                                    setForm((current) => ({ ...current, expiryDate: event.target.value }))
                                }
                                type="datetime-local"
                                className={saInput}
                            />
                        </label>
                    </div>

                    <div className="grid gap-3 md:grid-cols-[0.45fr_1fr]">
                        <label className="space-y-1.5">
                            <span className="text-[10px] font-semibold uppercase tracking-[0.16em] text-slate-500">
                                {tr('Scope', 'النطاق')}
                            </span>
                            <select
                                value={form.scopeType}
                                onChange={(event) =>
                                    setForm((current) => ({
                                        ...current,
                                        scopeType: event.target.value as PromoScopeType,
                                        scopeId: '',
                                    }))
                                }
                                className={saInput}
                            >
                                <option value="Global">{tr('Global', 'عام')}</option>
                                <option value="Vendor">{tr('Vendor', 'بائع')}</option>
                                <option value="Category">{tr('Category', 'فئة')}</option>
                            </select>
                        </label>

                        {form.scopeType === 'Global' ? (
                            <div className="flex items-center rounded-xl border border-slate-200 bg-slate-50 px-3.5 py-2.5 text-sm leading-6 text-slate-500">
                                {tr(
                                    'Applies to every eligible commission line in CommerceHub.',
                                    'تُطبَّق على كل بند عمولة مؤهل في CommerceHub.',
                                )}
                            </div>
                        ) : (
                            <label className="space-y-1.5">
                                <span className="text-[10px] font-semibold uppercase tracking-[0.16em] text-slate-500">
                                    {form.scopeType === 'Vendor' ? tr('Vendor target', 'البائع المستهدف') : tr('Category target', 'الفئة المستهدفة')}
                                </span>
                                <select
                                    value={form.scopeId}
                                    onChange={(event) =>
                                        setForm((current) => ({ ...current, scopeId: event.target.value }))
                                    }
                                    className={saInput}
                                >
                                    <option value="">
                                        {form.scopeType === 'Vendor'
                                            ? tr('Select vendor', 'اختر البائع')
                                            : tr('Select category', 'اختر الفئة')}
                                    </option>
                                    {scopeOptions.map((option) => (
                                        <option key={option.id} value={option.id}>
                                            {option.name}
                                        </option>
                                    ))}
                                </select>
                            </label>
                        )}
                    </div>

                    <label className="inline-flex items-center gap-2.5 rounded-xl border border-slate-200 bg-white px-3.5 py-2.5 text-sm font-semibold text-slate-700 shadow-sm">
                        <input
                            type="checkbox"
                            checked={form.isActive}
                            onChange={(event) =>
                                setForm((current) => ({ ...current, isActive: event.target.checked }))
                            }
                            className="h-4 w-4 rounded border-slate-300"
                        />
                        {tr('Active immediately', 'نشط فورًا')}
                    </label>

                    {formError ? (
                        <InlineNotice tone="rose" title={tr('Check the promo details', 'راجع تفاصيل الخصم')}>
                            {formError}
                        </InlineNotice>
                    ) : null}
                </div>

                <div className="flex flex-col-reverse gap-2 border-t border-slate-100 px-4 py-4 sm:flex-row sm:justify-end sm:px-5">
                    <button type="button" className={saButton.secondary} onClick={onClose}>
                        {tr('Cancel', 'إلغاء')}
                    </button>
                    <button
                        type="button"
                        className={saButton.primary}
                        disabled={isSubmitting}
                        onClick={() => void handleSubmit()}
                    >
                        {isSubmitting
                            ? tr('Saving…', 'جارٍ الحفظ…')
                            : promo
                              ? tr('Save changes', 'حفظ التغييرات')
                              : tr('Create promo', 'إنشاء الخصم')}
                    </button>
                </div>
            </div>
        </div>
    )
}

export default function PromoCodesPage() {
    const queryClient = useQueryClient()
    const { showError, showSuccess } = useToast()
    const { tr, formatNumber, formatCurrency, formatDateTime } = useSuperAdminLocale()
    const [isFormOpen, setIsFormOpen] = useState(false)
    const [editingPromo, setEditingPromo] = useState<SuperAdminPromoCode | null>(null)
    const [previewPromoId, setPreviewPromoId] = useState('')
    const [previewBaseCommission, setPreviewBaseCommission] = useState('100')
    const [previewVendorId, setPreviewVendorId] = useState('')
    const [previewCategoryId, setPreviewCategoryId] = useState('')

    const promoCodesQuery = useQuery({
        queryKey: promoCodesQueryKey,
        queryFn: getPromoCodes,
        staleTime: 15_000,
        refetchOnWindowFocus: true,
    })

    const categoriesQuery = useQuery({
        queryKey: promoCategoriesQueryKey,
        queryFn: getPromoCategories,
        staleTime: 5 * 60_000,
    })

    const vendorsQuery = useQuery({
        queryKey: promoVendorsQueryKey,
        queryFn: getPromoVendors,
        staleTime: 5 * 60_000,
    })

    const upsertMutation = useMutation({
        mutationFn: async ({
            promoId,
            input,
        }: {
            promoId?: string
            input: PromoCodeFormInput
        }) => (promoId ? updatePromoCode(promoId, input) : createPromoCode(input)),
    })

    const toggleMutation = useMutation({
        mutationFn: async (promo: SuperAdminPromoCode) =>
            promo.isActive ? deactivatePromoCode(promo.id) : activatePromoCode(promo.id),
    })

    const previewMutation = useMutation({
        mutationFn: () =>
            previewPromoCode({
                promoId: previewPromoId || null,
                baseCommissionAmount: Number(previewBaseCommission),
                previewVendorId: previewVendorId || null,
                previewCategoryId: previewCategoryId || null,
            }),
    })

    const promoCodes = useMemo(() => promoCodesQuery.data?.promoCodes ?? [], [promoCodesQuery.data?.promoCodes])
    const categories = useMemo(() => categoriesQuery.data ?? [], [categoriesQuery.data])
    const vendors = useMemo(() => vendorsQuery.data ?? [], [vendorsQuery.data])

    const categoriesById = useMemo(
        () =>
            new Map(
                categories.map((category) => [
                    category.id,
                    `${category.iconEmoji ? `${category.iconEmoji} ` : ''}${category.name}`,
                ]),
            ),
        [categories],
    )

    const vendorsById = useMemo(
        () => new Map(vendors.map((vendor) => [vendor.id, vendor.name])),
        [vendors],
    )

    const selectedPreviewPromo = useMemo(
        () => promoCodes.find((promo) => promo.id === previewPromoId) ?? null,
        [promoCodes, previewPromoId],
    )

    const activeCount = promoCodes.filter((promo) => getPromoHealth(promo).key === 'Active').length
    const totalUsageCount = promoCodes.reduce((sum, promo) => sum + promo.usedCount, 0)
    const scopedCount = promoCodes.filter((promo) => getPromoScopeType(promo) !== 'Global').length
    const inactiveCount = promoCodes.filter((promo) => getPromoHealth(promo).key !== 'Active').length
    const isInitialLoading = promoCodesQuery.isLoading && !promoCodesQuery.data
    const isRefreshing =
        promoCodesQuery.isFetching || categoriesQuery.isFetching || vendorsQuery.isFetching

    function healthLabel(key: PromoHealthKey): string {
        if (key === 'Expired') return tr('Expired', 'منتهي')
        if (key === 'LimitReached') return tr('Limit reached', 'بلغ الحد')
        if (key === 'Inactive') return tr('Inactive', 'غير نشط')
        return tr('Active', 'نشط')
    }

    function promoScopeLabel(promo: SuperAdminPromoCode): string {
        const label = getPromoScopeLabel(promo, categoriesById, vendorsById)

        if (label.startsWith('Vendor - ')) {
            return tr(label, `بائع — ${label.slice('Vendor - '.length)}`)
        }

        if (label.startsWith('Category - ')) {
            return tr(label, `فئة — ${label.slice('Category - '.length)}`)
        }

        return tr('Global', 'عام')
    }

    async function refreshAll() {
        await Promise.all([
            promoCodesQuery.refetch(),
            categoriesQuery.refetch(),
            vendorsQuery.refetch(),
        ])
    }

    async function handleSubmitPromo(input: PromoCodeFormInput) {
        try {
            await upsertMutation.mutateAsync({
                promoId: editingPromo?.id,
                input,
            })

            await queryClient.invalidateQueries({ queryKey: promoCodesQueryKey })
            showSuccess(
                editingPromo
                    ? tr('Promo code updated successfully.', 'تم تحديث رمز الخصم بنجاح.')
                    : tr('Promo code created successfully.', 'تم إنشاء رمز الخصم بنجاح.'),
            )
            setIsFormOpen(false)
            setEditingPromo(null)
        } catch (error) {
            showError(
                getErrorMessage(error, tr('Unable to save the promo code.', 'تعذّر حفظ رمز الخصم.')),
            )
        }
    }

    async function handleTogglePromo(promo: SuperAdminPromoCode) {
        try {
            await toggleMutation.mutateAsync(promo)
            await queryClient.invalidateQueries({ queryKey: promoCodesQueryKey })
            showSuccess(
                promo.isActive
                    ? tr('Promo code deactivated.', 'تم تعطيل رمز الخصم.')
                    : tr('Promo code activated.', 'تم تفعيل رمز الخصم.'),
            )
        } catch (error) {
            showError(
                getErrorMessage(error, tr('Unable to update promo status.', 'تعذّر تحديث حالة الخصم.')),
            )
        }
    }

    async function handlePreview() {
        if (!previewPromoId) {
            showError(tr('Select a promo code to preview.', 'اختر رمز خصم للمعاينة.'))
            return
        }

        const baseCommissionAmount = Number(previewBaseCommission)
        if (!Number.isFinite(baseCommissionAmount) || baseCommissionAmount < 0) {
            showError(
                tr('Base commission must be zero or greater.', 'يجب أن تكون العمولة الأساسية صفرًا أو أكثر.'),
            )
            return
        }

        try {
            await previewMutation.mutateAsync()
        } catch (error) {
            showError(
                getErrorMessage(
                    error,
                    tr('Unable to preview the promo calculation.', 'تعذّر معاينة حساب الخصم.'),
                ),
            )
        }
    }

    const guide = {
        en: {
            title: 'Promo codes guide',
            purpose:
                'Creates commission-only promotions that reduce what CommerceHub earns without changing order totals, customer payments, or vendor payouts.',
            when:
                'Use it for commercial campaigns, vendor acquisition incentives, seasonal commission relief, or testing a commission reduction before announcing it.',
            steps: [
                'Choose a scope: global, one vendor, or one category.',
                'Pick a percentage or fixed discount and optionally cap the maximum amount.',
                'Set total and per-customer usage limits and an expiry date when the promo is time-bound.',
                'Run the simulator against a base commission to confirm the final platform commission.',
                'Create or activate the code, then monitor usage and deactivate it when the campaign ends.',
            ],
            statuses: [
                { label: 'Active', description: 'Within its dates and limits; eligible for new commission calculations.' },
                { label: 'Inactive', description: 'Turned off manually; ignored by new calculations.' },
                { label: 'Expired', description: 'Past its expiry date; no longer applies.' },
                { label: 'Limit reached', description: 'Total usage limit consumed; no further uses allowed.' },
                { label: 'Global', description: 'Applies to every eligible commission line.' },
                { label: 'Vendor / Category', description: 'Restricts the commission discount to that scope.' },
            ],
            safety: [
                'Promos never change product totals or vendor earnings; they only reduce platform commission.',
                'Fixed discounts are commission amounts, not customer-facing order discounts.',
                'Usage limits and expiry are enforced by the backend; the UI cannot override them.',
                'Deactivating a promo stops future use but keeps historical usage auditable.',
                'Simulation uses a base commission you enter; it does not create or modify orders.',
            ],
        },
        ar: {
            title: 'دليل رموز الخصم',
            purpose:
                'ينشئ عروضًا على العمولة فقط تخفّض ما تكسبه CommerceHub دون تغيير إجماليات الطلب أو مدفوعات العميل أو مستحقات البائع.',
            when:
                'استخدمه للحملات التجارية، أو حوافز جذب البائعين، أو تخفيف العمولة الموسمي، أو اختبار تخفيض العمولة قبل الإعلان عنه.',
            steps: [
                'اختر النطاق: عام أو بائع واحد أو فئة واحدة.',
                'حدد خصمًا بنسبة أو مبلغ ثابت، ويمكنك وضع حد أقصى للمبلغ.',
                'اضبط حدود الاستخدام الإجمالية ولكل عميل وتاريخ الانتهاء عند ارتباط العرض بفترة.',
                'شغّل المحاكي على عمولة أساسية للتأكد من العمولة النهائية للمنصة.',
                'أنشئ الرمز أو فعّله، ثم راقب الاستخدام وعطّله عند انتهاء الحملة.',
            ],
            statuses: [
                { label: 'نشط', description: 'داخل تواريخه وحدوده؛ مؤهل لاحتساب العمولة الجديدة.' },
                { label: 'غير نشط', description: 'أُوقف يدويًا؛ يُتجاهل في الحسابات الجديدة.' },
                { label: 'منتهي', description: 'تجاوز تاريخ انتهائه؛ لم يعد يُطبَّق.' },
                { label: 'بلغ الحد', description: 'استُهلك حد الاستخدام الإجمالي؛ لا يُسمح بمزيد من الاستخدام.' },
                { label: 'عام', description: 'يُطبَّق على كل بند عمولة مؤهل.' },
                { label: 'بائع / فئة', description: 'يحصر خصم العمولة في ذلك النطاق.' },
            ],
            safety: [
                'لا تغيّر العروض إجماليات المنتجات أو أرباح البائعين؛ بل تخفّض عمولة المنصة فقط.',
                'الخصومات الثابتة هي مبالغ عمولة وليست خصومات على طلب العميل.',
                'حدود الاستخدام والانتهاء يفرضها الخادم، ولا يمكن للواجهة تجاوزها.',
                'تعطيل العرض يوقف الاستخدام المستقبلي ويُبقي الاستخدام السابق قابلًا للتدقيق.',
                'المحاكاة تستخدم عمولة أساسية تُدخلها؛ ولا تنشئ طلبات أو تعدّلها.',
            ],
        },
    }

    if (promoCodesQuery.isError && !promoCodesQuery.data) {
        return (
            <SuperAdminPage>
                <PageHeader
                    eyebrow={tr('Super Admin · Finance', 'الإدارة العليا · المالية')}
                    title={tr('Promo codes', 'رموز الخصم')}
                    description={tr(
                        'Commission-only promotions scoped by platform, vendor, or category.',
                        'عروض على العمولة فقط بنطاق المنصة أو البائع أو الفئة.',
                    )}
                />
                <InlineNotice tone="rose" title={tr('Promo controls unavailable', 'أدوات الخصم غير متاحة')}>
                    <p>
                        {getErrorMessage(
                            promoCodesQuery.error,
                            tr('Unable to load CommerceHub promo codes.', 'تعذّر تحميل رموز خصم CommerceHub.'),
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
                    title={tr('Promo codes', 'رموز الخصم')}
                    description={tr(
                        'Reduce platform commission without touching order totals or vendor earnings.',
                        'خفّض عمولة المنصة دون المساس بإجماليات الطلب أو أرباح البائع.',
                    )}
                    actions={
                        <>
                            <button
                                type="button"
                                className={saButton.primary}
                                onClick={() => {
                                    setEditingPromo(null)
                                    setIsFormOpen(true)
                                }}
                            >
                                <Sparkles className="h-4 w-4" />
                                {tr('New promo code', 'رمز خصم جديد')}
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
                        label={tr('Active promos', 'العروض النشطة')}
                        value={formatNumber(activeCount)}
                        hint={tr('Currently eligible', 'مؤهلة حاليًا')}
                        icon={<TicketPercent className="h-4 w-4 text-emerald-700" />}
                        tone="emerald"
                        isLoading={isInitialLoading}
                    />
                    <KpiCard
                        label={tr('Total uses', 'إجمالي الاستخدامات')}
                        value={formatNumber(totalUsageCount)}
                        hint={tr('Across all promo codes', 'عبر كل رموز الخصم')}
                        icon={<Calculator className="h-4 w-4 text-sky-700" />}
                        tone="sky"
                        isLoading={isInitialLoading}
                    />
                    <KpiCard
                        label={tr('Scoped promos', 'العروض محددة النطاق')}
                        value={formatNumber(scopedCount)}
                        hint={tr('Vendor or category scoped', 'بنطاق بائع أو فئة')}
                        icon={<ShieldCheck className="h-4 w-4 text-violet-700" />}
                        tone="violet"
                        isLoading={isInitialLoading}
                    />
                    <KpiCard
                        label={tr('Inactive or expired', 'غير نشطة أو منتهية')}
                        value={formatNumber(inactiveCount)}
                        hint={tr('Excluded from new calculations', 'مستبعدة من الحسابات الجديدة')}
                        icon={<RefreshCw className="h-4 w-4 text-amber-700" />}
                        tone="amber"
                        isLoading={isInitialLoading}
                    />
                </div>

                <div className="grid gap-3 sm:grid-cols-2 xl:grid-cols-4">
                    <div className="rounded-xl border border-slate-200 bg-white px-3.5 py-3 shadow-sm">
                        <p className="text-[10px] font-semibold uppercase tracking-[0.16em] text-slate-500">
                            {tr('API timestamp', 'وقت واجهة البرمجة')}
                        </p>
                        <p className="mt-1 text-sm font-semibold text-slate-900" dir="auto">
                            {formatDateTime(promoCodesQuery.data?.meta?.timestamp, tr('Pending', 'قيد الانتظار'))}
                        </p>
                    </div>
                    <div className="rounded-xl border border-slate-200 bg-white px-3.5 py-3 shadow-sm">
                        <p className="text-[10px] font-semibold uppercase tracking-[0.16em] text-slate-500">
                            {tr('Configured codes', 'الرموز المهيأة')}
                        </p>
                        <p className="mt-1 text-sm font-semibold text-slate-900" dir="auto">
                            {formatNumber(promoCodes.length)}
                        </p>
                    </div>
                    <div className="rounded-xl border border-slate-200 bg-white px-3.5 py-3 shadow-sm">
                        <p className="text-[10px] font-semibold uppercase tracking-[0.16em] text-slate-500">
                            {tr('Scope coverage', 'تغطية النطاق')}
                        </p>
                        <p className="mt-1 text-sm font-semibold text-slate-900" dir="auto">
                            {tr(
                                `${formatNumber(scopedCount)} scoped · ${formatNumber(
                                    promoCodes.length - scopedCount,
                                )} global`,
                                `${formatNumber(scopedCount)} محدد · ${formatNumber(
                                    promoCodes.length - scopedCount,
                                )} عام`,
                            )}
                        </p>
                    </div>
                    <div className="rounded-xl border border-slate-200 bg-white px-3.5 py-3 shadow-sm">
                        <p className="text-[10px] font-semibold uppercase tracking-[0.16em] text-slate-500">
                            {tr('Active rate', 'نسبة النشاط')}
                        </p>
                        <p className="mt-1 text-sm font-semibold text-slate-900" dir="auto">
                            {promoCodes.length === 0
                                ? tr('No codes yet', 'لا توجد رموز بعد')
                                : tr(
                                      `${formatNumber(activeCount)} of ${formatNumber(promoCodes.length)}`,
                                      `${formatNumber(activeCount)} من ${formatNumber(promoCodes.length)}`,
                                  )}
                        </p>
                    </div>
                </div>

                <GuidePanel guide={guide} />

                <div className="grid gap-4 xl:grid-cols-[0.95fr_1.05fr]">
                    <SectionCard
                        title={tr('Simulate commission impact', 'محاكاة أثر العمولة')}
                        description={tr(
                            'Choose a promo, a base commission, and optional order context to preview the final platform commission.',
                            'اختر عرضًا وعمولة أساسية وسياق طلب اختياريًا لمعاينة عمولة المنصة النهائية.',
                        )}
                    >
                        <div className="space-y-3">
                            <label className="space-y-1.5">
                                <span className="text-[10px] font-semibold uppercase tracking-[0.16em] text-slate-500">
                                    {tr('Promo code', 'رمز الخصم')}
                                </span>
                                <select
                                    value={previewPromoId}
                                    onChange={(event) => setPreviewPromoId(event.target.value)}
                                    className={saInput}
                                >
                                    <option value="">{tr('Select a promo code', 'اختر رمز خصم')}</option>
                                    {promoCodes.map((promo) => (
                                        <option key={promo.id} value={promo.id}>
                                            {promo.code} — {promoScopeLabel(promo)}
                                        </option>
                                    ))}
                                </select>
                            </label>

                            <div className="grid gap-3 md:grid-cols-3">
                                <label className="space-y-1.5">
                                    <span className="text-[10px] font-semibold uppercase tracking-[0.16em] text-slate-500">
                                        {tr('Base commission', 'العمولة الأساسية')}
                                    </span>
                                    <input
                                        value={previewBaseCommission}
                                        onChange={(event) => setPreviewBaseCommission(event.target.value)}
                                        type="number"
                                        min="0"
                                        step="0.01"
                                        dir="ltr"
                                        className={saInput}
                                    />
                                </label>

                                <label className="space-y-1.5">
                                    <span className="text-[10px] font-semibold uppercase tracking-[0.16em] text-slate-500">
                                        {tr('Vendor context', 'سياق البائع')}
                                    </span>
                                    <select
                                        value={previewVendorId}
                                        onChange={(event) => setPreviewVendorId(event.target.value)}
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
                                        {tr('Category context', 'سياق الفئة')}
                                    </span>
                                    <select
                                        value={previewCategoryId}
                                        onChange={(event) => setPreviewCategoryId(event.target.value)}
                                        className={saInput}
                                    >
                                        <option value="">{tr('Any category', 'أي فئة')}</option>
                                        {categories.map((category) => (
                                            <option key={category.id} value={category.id}>
                                                {`${category.iconEmoji ? `${category.iconEmoji} ` : ''}${category.name}`}
                                            </option>
                                        ))}
                                    </select>
                                </label>
                            </div>

                            <button
                                type="button"
                                className={saButton.primary}
                                disabled={previewMutation.isPending}
                                onClick={() => void handlePreview()}
                            >
                                {previewMutation.isPending ? (
                                    <RefreshCw className="h-4 w-4 animate-spin" />
                                ) : (
                                    <Calculator className="h-4 w-4" />
                                )}
                                {previewMutation.isPending
                                    ? tr('Calculating…', 'جارٍ الحساب…')
                                    : tr('Preview discount', 'معاينة الخصم')}
                            </button>

                            {selectedPreviewPromo ? (
                                <p className="rounded-xl border border-slate-200 bg-slate-50 px-3.5 py-2.5 text-sm text-slate-600">
                                    {tr(
                                        `${selectedPreviewPromo.code} applies to ${promoScopeLabel(selectedPreviewPromo)}.`,
                                        `${selectedPreviewPromo.code} يُطبَّق على ${promoScopeLabel(selectedPreviewPromo)}.`,
                                    )}
                                </p>
                            ) : null}

                            {previewMutation.data ? (
                                <div className="grid gap-2 rounded-xl border border-emerald-200 bg-emerald-50 p-3 sm:grid-cols-3">
                                    <div className="rounded-lg border border-white/80 bg-white px-3 py-2.5 shadow-sm">
                                        <p className="text-[10px] font-semibold uppercase tracking-[0.14em] text-slate-500">
                                            {tr('Base', 'الأساس')}
                                        </p>
                                        <p className="mt-1 text-base font-semibold text-slate-950" dir="auto">
                                            {formatCurrency(previewMutation.data.baseCommissionAmount)}
                                        </p>
                                    </div>
                                    <div className="rounded-lg border border-white/80 bg-white px-3 py-2.5 shadow-sm">
                                        <p className="text-[10px] font-semibold uppercase tracking-[0.14em] text-slate-500">
                                            {tr('Discount', 'الخصم')}
                                        </p>
                                        <p className="mt-1 text-base font-semibold text-emerald-700" dir="auto">
                                            {formatCurrency(previewMutation.data.discountAmount)}
                                        </p>
                                    </div>
                                    <div className="rounded-lg border border-white/80 bg-white px-3 py-2.5 shadow-sm">
                                        <p className="text-[10px] font-semibold uppercase tracking-[0.14em] text-slate-500">
                                            {tr('Final', 'النهائي')}
                                        </p>
                                        <p className="mt-1 text-base font-semibold text-slate-950" dir="auto">
                                            {formatCurrency(previewMutation.data.finalCommissionAmount)}
                                        </p>
                                    </div>
                                </div>
                            ) : null}
                        </div>
                    </SectionCard>

                    <SectionCard
                        title={tr('Platform-side protection', 'الحماية من جهة المنصة')}
                        description={tr(
                            'Guardrails that keep promo reductions financially safe.',
                            'ضوابط تحافظ على السلامة المالية لتخفيضات العروض.',
                        )}
                        headerAside={
                            <span className="inline-flex items-center gap-1.5 rounded-full border border-emerald-200 bg-emerald-50 px-2.5 py-1 text-xs font-semibold text-emerald-800">
                                <ShieldCheck className="h-3.5 w-3.5" />
                                {tr('Commission only', 'العمولة فقط')}
                            </span>
                        }
                    >
                        <div className="space-y-3">
                            <div className="rounded-xl border border-emerald-200 bg-emerald-50 px-3.5 py-3 text-sm text-emerald-950">
                                <p className="font-semibold">{tr('Product totals never change.', 'إجماليات المنتجات لا تتغير أبدًا.')}</p>
                                <p className="mt-0.5 leading-6">
                                    {tr(
                                        'Promos reduce only platform commission, so customer totals and vendor payouts stay intact.',
                                        'تخفّض العروض عمولة المنصة فقط، لذا تبقى إجماليات العميل ومستحقات البائع كما هي.',
                                    )}
                                </p>
                            </div>
                            <div className="rounded-xl border border-slate-200 bg-slate-50 px-3.5 py-3 text-sm text-slate-700">
                                <p className="font-semibold text-slate-950">{tr('Scope-aware application.', 'تطبيق واعٍ بالنطاق.')}</p>
                                <p className="mt-0.5 leading-6">
                                    {tr(
                                        'Global, vendor, and category scope let CommerceHub target commission reductions precisely.',
                                        'تتيح نطاقات العام والبائع والفئة لـ CommerceHub استهداف تخفيضات العمولة بدقة.',
                                    )}
                                </p>
                            </div>
                            <div className="rounded-xl border border-slate-200 bg-slate-50 px-3.5 py-3 text-sm text-slate-700">
                                <p className="font-semibold text-slate-950">{tr('Audit-ready controls.', 'أدوات جاهزة للتدقيق.')}</p>
                                <p className="mt-0.5 leading-6">
                                    {tr(
                                        'Usage counts, expiry, and activation state remain visible for financial review and operational tracing.',
                                        'تبقى أعداد الاستخدام والانتهاء وحالة التفعيل ظاهرة للمراجعة المالية والتتبع التشغيلي.',
                                    )}
                                </p>
                            </div>
                        </div>
                    </SectionCard>
                </div>

                <SectionCard
                    title={tr('Promo inventory', 'سجل رموز الخصم')}
                    description={tr(
                        'Every configured promo code with scope, discount, usage, expiry, and status.',
                        'كل رمز خصم مهيأ مع النطاق والخصم والاستخدام والانتهاء والحالة.',
                    )}
                    headerAside={
                        <span className="inline-flex items-center gap-1.5 rounded-full border border-slate-200 bg-slate-50 px-2.5 py-1 text-xs font-semibold text-slate-700">
                            <TicketPercent className="h-3.5 w-3.5" />
                            {tr(`${formatNumber(promoCodes.length)} codes`, `${formatNumber(promoCodes.length)} رمز`)}
                        </span>
                    }
                    bodyClassName="p-0"
                >
                    {isInitialLoading ? (
                        <div className="p-4">
                            <LoadingState label={tr('Loading promo codes…', 'جارٍ تحميل رموز الخصم…')} />
                        </div>
                    ) : promoCodes.length === 0 ? (
                        <div className="p-4">
                            <EmptyState
                                title={tr('No promo codes yet', 'لا توجد رموز خصم بعد')}
                                description={tr(
                                    'Create a commission-only promo to start a campaign or pilot a commission reduction.',
                                    'أنشئ عرضًا على العمولة فقط لبدء حملة أو تجربة تخفيض العمولة.',
                                )}
                                action={
                                    <button
                                        type="button"
                                        className={saButton.primary}
                                        onClick={() => {
                                            setEditingPromo(null)
                                            setIsFormOpen(true)
                                        }}
                                    >
                                        <Sparkles className="h-4 w-4" />
                                        {tr('Create promo code', 'إنشاء رمز خصم')}
                                    </button>
                                }
                            />
                        </div>
                    ) : (
                        <div className={saTable.shell}>
                            <table className={saTable.table}>
                                <thead className={saTable.head}>
                                    <tr>
                                        <th className={saTable.headerCell}>{tr('Code', 'الرمز')}</th>
                                        <th className={saTable.headerCell}>{tr('Scope', 'النطاق')}</th>
                                        <th className={saTable.headerCell}>{tr('Discount', 'الخصم')}</th>
                                        <th className={saTable.headerCell}>{tr('Usage', 'الاستخدام')}</th>
                                        <th className={saTable.headerCell}>{tr('Expiry', 'الانتهاء')}</th>
                                        <th className={saTable.headerCell}>{tr('Status', 'الحالة')}</th>
                                        <th className={`${saTable.headerCell} text-end`}>{tr('Actions', 'إجراءات')}</th>
                                    </tr>
                                </thead>
                                <tbody className="divide-y divide-slate-100 bg-white">
                                    {promoCodes.map((promo) => {
                                        const health = getPromoHealth(promo)

                                        return (
                                            <tr key={promo.id} className="align-top">
                                                <td className={saTable.cell}>
                                                    <p className="font-semibold text-slate-950" dir="ltr">
                                                        {promo.code}
                                                    </p>
                                                    <p className="mt-0.5 text-xs text-slate-500" dir="auto">
                                                        {tr('Created', 'أُنشئ')}{' '}
                                                        {formatDateTime(promo.createdAt, tr('Unavailable', 'غير متاح'))}
                                                    </p>
                                                </td>
                                                <td className={`${saTable.cell} text-sm text-slate-700`}>
                                                    {promoScopeLabel(promo)}
                                                </td>
                                                <td className={saTable.cell}>
                                                    <p className="text-sm font-semibold text-slate-950" dir="auto">
                                                        {promo.discountType === 'Percentage'
                                                            ? `${formatNumber(promo.discountValue, {
                                                                  maximumFractionDigits: 2,
                                                              })}%`
                                                            : formatCurrency(promo.discountValue)}
                                                    </p>
                                                    <p className="mt-0.5 text-xs text-slate-500" dir="auto">
                                                        {typeof promo.maxDiscountAmount === 'number'
                                                            ? tr(
                                                                  `Max ${formatCurrency(promo.maxDiscountAmount)}`,
                                                                  `الحد ${formatCurrency(promo.maxDiscountAmount)}`,
                                                              )
                                                            : tr('No cap', 'بلا حد')}
                                                    </p>
                                                </td>
                                                <td className={saTable.cell}>
                                                    <p className="text-sm font-semibold text-slate-900" dir="auto">
                                                        {formatNumber(promo.usedCount)}
                                                        <span className="ms-1 text-xs font-normal text-slate-500">
                                                            {typeof promo.usageLimit === 'number'
                                                                ? tr(`of ${formatNumber(promo.usageLimit)}`, `من ${formatNumber(promo.usageLimit)}`)
                                                                : tr('unlimited', 'غير محدود')}
                                                        </span>
                                                    </p>
                                                    <p className="mt-0.5 text-xs text-slate-500">
                                                        {typeof promo.maxUsagePerCustomer === 'number'
                                                            ? tr(
                                                                  `${formatNumber(promo.maxUsagePerCustomer)} per customer`,
                                                                  `${formatNumber(promo.maxUsagePerCustomer)} لكل عميل`,
                                                              )
                                                            : tr('No per-customer cap', 'بلا حد لكل عميل')}
                                                    </p>
                                                </td>
                                                <td className={`${saTable.cell} text-sm text-slate-700`} dir="auto">
                                                    {formatDateTime(promo.expiryDate, tr('No expiry', 'لا انتهاء'))}
                                                </td>
                                                <td className={saTable.cell}>
                                                    <StatusBadge tone={health.tone}>{healthLabel(health.key)}</StatusBadge>
                                                </td>
                                                <td className={`${saTable.cell} text-end`}>
                                                    <div className="flex flex-wrap items-center justify-end gap-1.5">
                                                        <button
                                                            type="button"
                                                            className={`${saButton.secondary} px-2.5 py-1.5 text-xs`}
                                                            onClick={() => {
                                                                setEditingPromo(promo)
                                                                setIsFormOpen(true)
                                                            }}
                                                        >
                                                            {tr('Edit', 'تعديل')}
                                                        </button>
                                                        <button
                                                            type="button"
                                                            className={`${
                                                                promo.isActive ? saButton.danger : saButton.success
                                                            } px-2.5 py-1.5 text-xs`}
                                                            disabled={toggleMutation.isPending}
                                                            onClick={() => void handleTogglePromo(promo)}
                                                        >
                                                            {promo.isActive
                                                                ? tr('Deactivate', 'تعطيل')
                                                                : tr('Activate', 'تفعيل')}
                                                        </button>
                                                    </div>
                                                </td>
                                            </tr>
                                        )
                                    })}
                                </tbody>
                            </table>
                        </div>
                    )}
                </SectionCard>

                {promoCodesQuery.isError && promoCodesQuery.data ? (
                    <InlineNotice tone="amber" title={tr('Cached promo codes shown', 'تُعرض رموز الخصم المخزنة')}>
                        {tr(
                            'The latest promo refresh failed, so the most recent cached promo codes are shown.',
                            'فشل تحديث رموز الخصم الأخير، لذا تُعرض أحدث رموز مخزنة.',
                        )}
                    </InlineNotice>
                ) : null}

                {categoriesQuery.isError || vendorsQuery.isError ? (
                    <InlineNotice tone="amber" title={tr('Scope lookups incomplete', 'قوائم النطاق غير مكتملة')}>
                        {tr(
                            'Some scope names could not be loaded, so stored identifiers are shown where necessary.',
                            'تعذّر تحميل بعض أسماء النطاقات، لذا تُعرض المعرّفات المخزنة عند الحاجة.',
                        )}
                    </InlineNotice>
                ) : null}
            </SuperAdminPage>

            <PromoCodeFormModal
                isOpen={isFormOpen}
                isSubmitting={upsertMutation.isPending}
                promo={editingPromo}
                categories={categories}
                vendors={vendors}
                onClose={() => {
                    setIsFormOpen(false)
                    setEditingPromo(null)
                }}
                onSubmit={handleSubmitPromo}
            />
        </>
    )
}
