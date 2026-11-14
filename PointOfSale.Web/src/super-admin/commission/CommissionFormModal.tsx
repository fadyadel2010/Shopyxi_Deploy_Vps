import { useEffect, useState, type FormEvent } from 'react'
import { Building2, FolderTree, Globe2, Loader2, Save, X } from 'lucide-react'
import { useSuperAdminLocale } from '../i18n/useSuperAdminLocale'
import { InlineNotice, saButton, saInput } from '../ui/SuperAdminUI'
import type {
    CommissionCategoryOption,
    CommissionRule,
    CommissionRuleFormInput,
    CommissionScopeType,
    CommissionVendorOption,
} from './commissionService'

interface CommissionFormModalProps {
    isOpen: boolean
    isSubmitting: boolean
    rule?: CommissionRule | null
    defaultScopeType?: CommissionScopeType
    categories: CommissionCategoryOption[]
    vendors: CommissionVendorOption[]
    onClose: () => void
    onSubmit: (input: CommissionRuleFormInput) => Promise<void> | void
}

type FormErrors = Partial<Record<'scopeId' | 'ratePercent' | 'priority', string>>

function formatCategoryLabel(category: CommissionCategoryOption): string {
    const prefix = category.level > 0 ? `${'-- '.repeat(category.level)}` : ''
    const emoji = category.iconEmoji ? `${category.iconEmoji} ` : ''
    return `${prefix}${emoji}${category.name}`
}

function parseRatePercent(rate: number): string {
    return Number.isInteger(rate * 100)
        ? String(rate * 100)
        : String(Number((rate * 100).toFixed(2)))
}

export default function CommissionFormModal({
    isOpen,
    isSubmitting,
    rule,
    defaultScopeType = 'Category',
    categories,
    vendors,
    onClose,
    onSubmit,
}: CommissionFormModalProps) {
    const [scopeType, setScopeType] = useState<CommissionScopeType>(defaultScopeType)
    const [scopeId, setScopeId] = useState<string>('')
    const [ratePercent, setRatePercent] = useState<string>('')
    const [priority, setPriority] = useState<string>('0')
    const [errors, setErrors] = useState<FormErrors>({})
    const { tr } = useSuperAdminLocale()

    useEffect(() => {
        if (!isOpen) {
            return
        }

        setScopeType(rule?.scopeType ?? defaultScopeType)
        setScopeId(rule?.scopeId ?? '')
        setRatePercent(rule ? parseRatePercent(rule.rate) : '')
        setPriority(rule ? String(rule.priority) : '0')
        setErrors({})
    }, [defaultScopeType, isOpen, rule])

    useEffect(() => {
        if (!isOpen) {
            return
        }

        function handleEscape(event: KeyboardEvent) {
            if (event.key === 'Escape' && !isSubmitting) {
                onClose()
            }
        }

        window.addEventListener('keydown', handleEscape)
        return () => window.removeEventListener('keydown', handleEscape)
    }, [isOpen, isSubmitting, onClose])

    if (!isOpen) {
        return null
    }

    const isGlobal = scopeType === 'Global'
    const scopeLabel = scopeType === 'Category' ? tr('Category', 'الفئة') : tr('Vendor', 'البائع')
    const isEditing = Boolean(rule)

    const scopeTypeLabel = (value: CommissionScopeType) => {
        if (value === 'Global') return tr('Global', 'عام')
        if (value === 'Category') return tr('Category', 'فئة')
        return tr('Vendor', 'بائع')
    }

    async function handleSubmit(event: FormEvent<HTMLFormElement>) {
        event.preventDefault()

        const nextErrors: FormErrors = {}
        const parsedRate = Number(ratePercent)
        const parsedPriority = Number(priority)

        if (Number.isNaN(parsedRate) || parsedRate < 0 || parsedRate > 100) {
            nextErrors.ratePercent = tr(
                'Rate must be between 0 and 100.',
                'يجب أن تكون النسبة بين 0 و100.',
            )
        }

        if (!Number.isInteger(parsedPriority)) {
            nextErrors.priority = tr('Priority must be a whole number.', 'يجب أن تكون الأولوية عددًا صحيحًا.')
        }

        if (!isGlobal && !scopeId) {
            nextErrors.scopeId = tr(
                `${scopeLabel} is required for this rule.`,
                `${scopeLabel} مطلوبة لهذه القاعدة.`,
            )
        }

        setErrors(nextErrors)

        if (Object.keys(nextErrors).length > 0) {
            return
        }

        await onSubmit({
            scopeType,
            scopeId: isGlobal ? null : scopeId,
            ratePercent: parsedRate,
            priority: parsedPriority,
        })
    }

    return (
        <div className="fixed inset-0 z-50 flex items-center justify-center bg-slate-950/55 px-4 py-6 backdrop-blur-sm">
            <div className="relative max-h-[92vh] w-full max-w-2xl overflow-y-auto rounded-2xl border border-white/10 bg-white shadow-2xl">
                <div className="flex items-start justify-between gap-3 border-b border-slate-100 px-4 py-4 sm:px-5">
                    <div className="min-w-0">
                        <p className="text-[11px] font-semibold uppercase tracking-[0.18em] text-emerald-700">
                            {isEditing ? tr('Edit rule', 'تعديل قاعدة') : tr('Create rule', 'إنشاء قاعدة')}
                        </p>
                        <h2 className="mt-1.5 text-xl font-semibold text-slate-950">
                            {isEditing
                                ? tr('Update commission rule', 'تحديث قاعدة العمولة')
                                : tr('Create a commission rule', 'إنشاء قاعدة عمولة')}
                        </h2>
                        <p className="mt-1 text-sm leading-6 text-slate-500">
                            {tr(
                                'Vendor overrides category, category overrides global, and priority only breaks ties inside the same scope.',
                                'تتقدم قاعدة البائع على الفئة، وتتقدم الفئة على العام، وتُستخدم الأولوية فقط لحسم التعادل داخل النطاق نفسه.',
                            )}
                        </p>
                    </div>

                    <button
                        type="button"
                        aria-label={tr('Close commission rule form', 'إغلاق نموذج قاعدة العمولة')}
                        className="flex h-9 w-9 shrink-0 items-center justify-center rounded-lg border border-slate-200 bg-white text-slate-600 transition hover:bg-slate-50 disabled:cursor-not-allowed disabled:opacity-60"
                        disabled={isSubmitting}
                        onClick={onClose}
                    >
                        <X className="h-4 w-4" />
                    </button>
                </div>

                <form className="space-y-4 px-4 py-4 sm:px-5" onSubmit={handleSubmit}>
                    <div className="grid gap-4 md:grid-cols-2">
                        <div className="space-y-1.5">
                            <label className="text-[10px] font-semibold uppercase tracking-[0.16em] text-slate-500">
                                {tr('Scope type', 'نوع النطاق')}
                            </label>
                            <select
                                value={scopeType}
                                onChange={(event) => {
                                    const nextScopeType = event.target.value as CommissionScopeType
                                    setScopeType(nextScopeType)
                                    setScopeId('')
                                }}
                                className={saInput}
                            >
                                <option value="Global">{scopeTypeLabel('Global')}</option>
                                <option value="Category">{scopeTypeLabel('Category')}</option>
                                <option value="Vendor">{scopeTypeLabel('Vendor')}</option>
                            </select>
                            <div className="flex items-start gap-1.5 text-xs leading-5 text-slate-500">
                                {scopeType === 'Global' ? <Globe2 className="mt-0.5 h-3.5 w-3.5 shrink-0" /> : null}
                                {scopeType === 'Category' ? <FolderTree className="mt-0.5 h-3.5 w-3.5 shrink-0" /> : null}
                                {scopeType === 'Vendor' ? <Building2 className="mt-0.5 h-3.5 w-3.5 shrink-0" /> : null}
                                <span>
                                    {scopeType === 'Global'
                                        ? tr(
                                              'Default platform rate when no override matches.',
                                              'نسبة المنصة الافتراضية عند عدم تطابق أي تجاوز.',
                                          )
                                        : scopeType === 'Category'
                                          ? tr(
                                                'Applies to all vendors in the selected CommerceHub category.',
                                                'تُطبَّق على كل بائعي فئة CommerceHub المحددة.',
                                            )
                                          : tr(
                                                'Applies to one vendor regardless of category priority.',
                                                'تُطبَّق على بائع واحد بصرف النظر عن أولوية الفئة.',
                                            )}
                                </span>
                            </div>
                        </div>

                        <div className="space-y-1.5">
                            <label className="text-[10px] font-semibold uppercase tracking-[0.16em] text-slate-500">
                                {tr('Scope target', 'هدف النطاق')}
                            </label>
                            <select
                                value={scopeId}
                                disabled={isGlobal}
                                onChange={(event) => setScopeId(event.target.value)}
                                className={saInput}
                            >
                                <option value="">
                                    {isGlobal
                                        ? tr('Global rules do not use a scope target', 'القواعد العامة لا تستخدم هدف نطاق')
                                        : tr(`Select a ${scopeLabel.toLowerCase()}`, `اختر ${scopeLabel}`)}
                                </option>
                                {(scopeType === 'Category' ? categories : vendors).map((option) => (
                                    <option key={option.id} value={option.id}>
                                        {'level' in option ? formatCategoryLabel(option) : option.name}
                                    </option>
                                ))}
                            </select>
                            {errors.scopeId ? (
                                <p className="text-sm text-rose-600">{errors.scopeId}</p>
                            ) : (
                                <p className="text-xs leading-5 text-slate-500">
                                    {isGlobal
                                        ? tr('No target required.', 'لا يلزم هدف.')
                                        : tr(
                                              'Priority ties are evaluated only against rules in this same scope.',
                                              'تُقيَّم تعادلات الأولوية فقط مقابل القواعد في النطاق نفسه.',
                                          )}
                                </p>
                            )}
                        </div>
                    </div>

                    <div className="grid gap-4 md:grid-cols-2">
                        <div className="space-y-1.5">
                            <label className="text-[10px] font-semibold uppercase tracking-[0.16em] text-slate-500">
                                {tr('Rate (%)', 'النسبة (%)')}
                            </label>
                            <div className="relative">
                                <input
                                    type="number"
                                    min="0"
                                    max="100"
                                    step="0.01"
                                    inputMode="decimal"
                                    dir="ltr"
                                    value={ratePercent}
                                    onChange={(event) => setRatePercent(event.target.value)}
                                    placeholder="5"
                                    className={`${saInput} pe-10`}
                                />
                                <span className="pointer-events-none absolute inset-y-0 end-3.5 flex items-center text-sm font-semibold text-slate-500">
                                    %
                                </span>
                            </div>
                            {errors.ratePercent ? (
                                <p className="text-sm text-rose-600">{errors.ratePercent}</p>
                            ) : (
                                <p className="text-xs leading-5 text-slate-500">
                                    {tr(
                                        'Enter a percentage value between 0% and 100%.',
                                        'أدخل قيمة نسبة بين 0% و100%.',
                                    )}
                                </p>
                            )}
                        </div>

                        <div className="space-y-1.5">
                            <label className="text-[10px] font-semibold uppercase tracking-[0.16em] text-slate-500">
                                {tr('Priority', 'الأولوية')}
                            </label>
                            <input
                                type="number"
                                step="1"
                                dir="ltr"
                                value={priority}
                                onChange={(event) => setPriority(event.target.value)}
                                placeholder="0"
                                className={saInput}
                            />
                            {errors.priority ? (
                                <p className="text-sm text-rose-600">{errors.priority}</p>
                            ) : (
                                <p className="text-xs leading-5 text-slate-500">
                                    {tr(
                                        'Higher priority wins only among rules with the same scope and scope target.',
                                        'تفوز الأولوية الأعلى فقط بين القواعد ذات النطاق وهدفه نفسيهما.',
                                    )}
                                </p>
                            )}
                        </div>
                    </div>

                    <InlineNotice tone="slate" title={tr('Rule behavior', 'سلوك القاعدة')}>
                        {tr(
                            'This rule will not modify historical orders. New checkouts snapshot the resolved rate and source at order time.',
                            'لن تعدّل هذه القاعدة الطلبات السابقة. تحتفظ عمليات الدفع الجديدة بلقطة للنسبة والمصدر المحتسبين وقت الطلب.',
                        )}
                    </InlineNotice>

                    <div className="flex flex-col-reverse gap-2 border-t border-slate-100 pt-4 sm:flex-row sm:justify-end">
                        <button
                            type="button"
                            className={saButton.secondary}
                            disabled={isSubmitting}
                            onClick={onClose}
                        >
                            {tr('Cancel', 'إلغاء')}
                        </button>
                        <button type="submit" className={saButton.primary} disabled={isSubmitting}>
                            {isSubmitting ? (
                                <Loader2 className="h-4 w-4 animate-spin" />
                            ) : (
                                <Save className="h-4 w-4" />
                            )}
                            {isSubmitting
                                ? tr('Saving…', 'جارٍ الحفظ…')
                                : isEditing
                                  ? tr('Save changes', 'حفظ التغييرات')
                                  : tr('Create rule', 'إنشاء القاعدة')}
                        </button>
                    </div>
                </form>
            </div>
        </div>
    )
}
