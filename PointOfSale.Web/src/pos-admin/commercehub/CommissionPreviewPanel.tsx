import { useEffect, useMemo, useState } from 'react'
import { useTranslation } from 'react-i18next'
import { Loader2, TrendingUp } from 'lucide-react'
import { useFormatting } from '../../hooks/useFormatting'
import { calculateRequiredPriceForTargetEarnings, roundCurrencyAmount } from './commissionCalculator'
import { useCommissionPreview } from './useCommissionPreview'

interface CommissionPreviewPanelProps {
    vendorId?: string | null
    variantId: string
    parentCategoryId?: string | null
    categoryId?: string | null
    categoryName?: string | null
    pricingMode: 'source' | 'custom'
    marketplacePrice?: number | null
    onApplySuggestedPrice?: (price: number) => void
    onResolutionChange?: (state: { ready: boolean; ruleId: string | null }) => void
}

export default function CommissionPreviewPanel({
    vendorId,
    variantId,
    parentCategoryId,
    categoryId,
    categoryName,
    pricingMode,
    marketplacePrice,
    onApplySuggestedPrice,
    onResolutionChange,
}: CommissionPreviewPanelProps) {
    const { t } = useTranslation()
    const { formatCurrency, formatNumber, isRTL } = useFormatting()
    const preview = useCommissionPreview({
        vendorId,
        variantId,
        parentCategoryId,
        categoryId,
        pricingMode,
        marketplacePrice,
    })
    const [showTargetEarnings, setShowTargetEarnings] = useState(false)
    const [targetEarningsInput, setTargetEarningsInput] = useState('')

    useEffect(() => {
        onResolutionChange?.({ ready: preview.isResolved && !preview.loading, ruleId: preview.ruleId })
    }, [onResolutionChange, preview.isResolved, preview.loading, preview.ruleId])

    const targetEarnings = useMemo(() => {
        const parsed = Number(targetEarningsInput)
        return targetEarningsInput.trim() && Number.isFinite(parsed) && parsed > 0
            ? roundCurrencyAmount(parsed)
            : null
    }, [targetEarningsInput])
    const targetPreview = useMemo(() => (
        showTargetEarnings && preview.rate !== null && targetEarnings !== null
            ? calculateRequiredPriceForTargetEarnings(targetEarnings, preview.rate)
            : null
    ), [preview.rate, showTargetEarnings, targetEarnings])

    const errorKey = preview.errorCode === 'COMMISSION_AUTHORITY_MISSING'
        ? 'commerceHub.commission.errors.missingRule'
        : preview.errorCode === 'COMMISSION_AUTHORITY_AMBIGUOUS'
            ? 'commerceHub.commission.errors.ambiguousRule'
            : preview.errorCode === 'INVALID_CUSTOM_PRICE'
                ? 'commerceHub.commission.errors.invalidPrice'
                : 'commerceHub.commission.errors.unavailable'

    const sourceLabel = preview.source
        ? t(`commerceHub.commission.sources.${preview.source}`, { defaultValue: preview.source })
        : t('commerceHub.commission.pending')
    const ruleLabel = preview.source === 'Category'
        ? t('commerceHub.commission.appliedCategory', { category: categoryName || t('commerceHub.subcategory') })
        : preview.source
            ? t('commerceHub.commission.appliedRule', { source: sourceLabel })
            : t('commerceHub.commission.awaitingResolution')

    if (!categoryId) {
        return <div className="rounded-xl border border-dashed border-slate-300 bg-slate-50 p-4 text-sm text-slate-600">{t('commerceHub.commission.selectCategory')}</div>
    }

    const money = (value: number | null) => value === null ? '—' : formatCurrency(value, 'EGP')
    const rate = preview.rate === null ? '—' : `${formatNumber(preview.rate * 100, { maximumFractionDigits: 2 })}%`

    return (
        <section className="overflow-hidden rounded-2xl border border-emerald-200 bg-emerald-50/30" dir={isRTL ? 'rtl' : 'ltr'}>
            <div className="flex flex-wrap items-start justify-between gap-3 border-b border-emerald-100 bg-white px-4 py-4">
                <div>
                    <h4 className="font-bold text-slate-950">{t('commerceHub.commission.title')}</h4>
                    <p className="mt-1 text-sm text-slate-600">{t('commerceHub.commission.help')}</p>
                </div>
                {preview.loading && <span className="inline-flex items-center gap-2 rounded-full bg-sky-50 px-3 py-1.5 text-xs font-semibold text-sky-800"><Loader2 className="h-4 w-4 animate-spin" />{t('commerceHub.commission.loading')}</span>}
            </div>

            <div className="grid gap-4 p-4 lg:grid-cols-[1.1fr_0.9fr]">
                <div className="space-y-4">
                    {preview.errorCode && <div role="alert" className="rounded-xl border border-red-200 bg-red-50 px-4 py-3 text-sm text-red-800">{t(errorKey)}</div>}
                    <div className="rounded-xl border border-slate-200 bg-white p-4">
                        <dl className="space-y-3 text-sm">
                            <div className="flex justify-between gap-4"><dt className="text-slate-600">{t('commerceHub.commission.sourcePrice')}</dt><dd className="font-semibold text-slate-950">{money(preview.sourceProductPrice)}</dd></div>
                            <div className="flex justify-between gap-4"><dt className="text-slate-600">{t('commerceHub.commission.marketplacePrice')}</dt><dd className="font-semibold text-slate-950">{money(preview.marketplacePrice)}</dd></div>
                            <div className="flex justify-between gap-4"><dt className="text-slate-600">{t('commerceHub.commission.rate')}</dt><dd className="font-semibold text-slate-950">{rate}</dd></div>
                            <div className="flex justify-between gap-4 text-red-700"><dt>{t('commerceHub.commission.deduction')}</dt><dd className="font-semibold">{preview.commissionAmount === null ? '—' : `− ${money(preview.commissionAmount)}`}</dd></div>
                            <div className="flex justify-between gap-4 border-t border-dashed border-slate-200 pt-3 text-base"><dt className="font-bold text-slate-950">{t('commerceHub.commission.net')}</dt><dd className="text-lg font-bold text-emerald-700">{money(preview.vendorReceives)}</dd></div>
                        </dl>
                    </div>
                    <div className="rounded-xl border border-slate-200 bg-white p-4">
                        <p className="text-xs font-semibold uppercase tracking-wide text-slate-500">{t('commerceHub.commission.ruleResolution')}</p>
                        <p className="mt-2 text-sm font-semibold text-slate-950">{ruleLabel}</p>
                        <div className="mt-3 grid grid-cols-2 gap-3 text-sm"><div><span className="text-slate-500">{t('commerceHub.commission.source')}</span><p className="font-medium">{sourceLabel}</p></div><div><span className="text-slate-500">{t('commerceHub.commission.priority')}</span><p className="font-medium">{preview.priority ?? '—'}</p></div></div>
                    </div>
                </div>

                <div className="rounded-xl border border-slate-200 bg-white p-4">
                    <label className="flex items-start gap-3"><input type="checkbox" checked={showTargetEarnings} onChange={(event) => setShowTargetEarnings(event.target.checked)} className="mt-1" disabled={!preview.isResolved} /><span><span className="block text-sm font-semibold text-slate-950">{t('commerceHub.commission.target.title')}</span><span className="mt-1 block text-xs leading-5 text-slate-500">{t('commerceHub.commission.target.help')}</span></span></label>
                    {showTargetEarnings && <div className="mt-4 space-y-3"><label className="block text-xs font-semibold text-slate-600">{t('commerceHub.commission.target.desired')}</label><input type="number" min="0.01" step="0.01" value={targetEarningsInput} onChange={(event) => setTargetEarningsInput(event.target.value)} className="w-full rounded-xl border border-slate-300 px-3 py-2 text-sm" />
                        {targetPreview ? <div className="rounded-xl border border-emerald-200 bg-emerald-50 p-3"><div className="flex items-center gap-2 text-sm font-semibold text-emerald-900"><TrendingUp className="h-4 w-4" />{t('commerceHub.commission.target.required')}</div><p className="mt-2 text-xl font-bold text-emerald-700">{money(targetPreview.requiredPrice)}</p>{onApplySuggestedPrice && <button type="button" onClick={() => onApplySuggestedPrice(targetPreview.requiredPrice)} className="mt-3 rounded-lg border border-emerald-300 bg-white px-3 py-2 text-xs font-semibold text-emerald-800">{t('commerceHub.commission.target.apply')}</button>}</div> : <p className="text-xs text-slate-500">{t('commerceHub.commission.target.prompt')}</p>}
                    </div>}
                </div>
            </div>
        </section>
    )
}
