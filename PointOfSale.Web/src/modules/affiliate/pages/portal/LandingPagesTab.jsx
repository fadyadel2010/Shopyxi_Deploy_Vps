/**
 * AF-27B §3 / AF-28: My Landing Pages Tab
 * 
 * Displays all affiliate-eligible products with pre-built landing page URLs.
 * Allows affiliates to:
 * - See all available landing pages
 * - Choose landing page template (Classic, Storytelling, Minimal Fast Checkout)
 * - Save an affiliate-specific design and share its durable public URL
 * - Preview landing page in new tab
 * - See product info (price, commission, stock status)
 * 
 * DATA SOURCE: SQL-admitted product data and persisted design preferences
 * 
 * ARCHITECTURAL BOUNDARIES:
 * - Presentation preference writes only
 * - No cart/checkout logic
 * - No commission calculations (displayed from projection)
 */
import { useState, useEffect, useCallback, useRef } from 'react'
import { motion } from 'framer-motion'
import { affiliateAxios } from '../../../../services/affiliateAuthApi'
import { Copy, Check, ExternalLink, AlertCircle, Globe, Palette } from 'lucide-react'
import { useLanguage } from '../../i18n/LanguageContext'
import { affiliateProductMediaUrl } from '../../lib/affiliateProductMedia'
import { PortalProductImage } from './PortalUi'

const TEMPLATE_OPTIONS = [
    { value: 'Classic:v1', key: 'classic' },
    { value: 'Storytelling:v1', key: 'storytelling' },
    { value: 'MinimalFastCheckout:v1', key: 'minimal' }
]

export default function LandingPagesTab({ formatCurrency }) {
    const { t, formatNumber } = useLanguage()
    const [pages, setPages] = useState([])
    const [affiliateCode, setAffiliateCode] = useState('')
    const [loading, setLoading] = useState(true)
    const [error, setError] = useState(null)
    const [copiedId, setCopiedId] = useState(null)
    const [page, setPage] = useState(1)
    const [totalPages, setTotalPages] = useState(1)
    // AF-28: Track selected template per product (key: productId, value: templateType)
    const [selectedTemplates, setSelectedTemplates] = useState({})
    const [savingId, setSavingId] = useState(null)
    const [feedback, setFeedback] = useState(null)
    const requestRef = useRef(0)

    const fetchLandingPages = useCallback(async () => {
        const request = ++requestRef.current
        setLoading(true)
        setError(null)
        try {
            const res = await affiliateAxios.get('/affiliate/landing-pages', {
                params: { page, pageSize: 20 }
            })
            if (request !== requestRef.current) return
            setPages(res.data.landingPages || [])
            setAffiliateCode(res.data.affiliateCode || '')
            setTotalPages(res.data.totalPages || 1)

            // Initialize selected templates from backend defaults
            const defaults = {}
            for (const lp of (res.data.landingPages || [])) {
                defaults[lp.productId] = lp.templateType || 'Classic:v1'
            }
            setSelectedTemplates(prev => ({ ...prev, ...defaults }))
        } catch (err) {
            if (request !== requestRef.current) return
            console.warn('[LandingPagesTab] Failed to fetch landing pages:', err?.response?.status || err.message)
            setPages([])
            if (err?.response?.status === 401) {
                setError('session')
            } else {
                setError('unavailable')
            }
        } finally {
            if (request === requestRef.current) setLoading(false)
        }
    }, [page])

    const invalidateRequests = useCallback(() => { requestRef.current++ }, [])

    useEffect(() => {
        void fetchLandingPages()
        return invalidateRequests
    }, [fetchLandingPages, invalidateRequests])

    // Durable public URL: the API, not a preview query parameter, resolves the saved design.
    const getEffectiveUrl = lp => lp.absoluteLandingUrl

    const saveDesign = async lp => {
        setSavingId(lp.productId)
        setFeedback(null)
        try {
            const res = await affiliateAxios.put(`/affiliate/landing-pages/${lp.productId}/design`, { templateType: selectedTemplates[lp.productId] })
            setPages(prev => prev.map(p => p.productId === lp.productId ? { ...p, templateType: res.data.templateType, hasSavedDesign: true } : p))
            setFeedback({ id: lp.productId, key: 'saved' })
        } catch { setFeedback({ id: lp.productId, key: 'saveError', error: true }) }
        finally { setSavingId(null) }
    }

    const copyUrl = async (lp) => {
        const url = getEffectiveUrl(lp)
        try {
            await navigator.clipboard.writeText(url)
            setCopiedId(lp.productId)
            setTimeout(() => setCopiedId(null), 2000)
        } catch { setFeedback({ id: lp.productId, key: 'copyError', error: true }) }
    }

    const previewPage = (lp) => {
        const url = getEffectiveUrl(lp)
        window.open(url, '_blank', 'noopener,noreferrer')
    }

    const handleTemplateChange = (productId, templateType) => {
        setSelectedTemplates(prev => ({ ...prev, [productId]: templateType }))
    }

    if (loading) {
        return (
            <div role="status" aria-label={t('ui.loading')} className="space-y-3">
                {[...Array(4)].map((_, i) => (
                    <div key={i} className="h-24 rounded-xl border border-gray-200 dark:border-white/[0.06] bg-white dark:bg-white/[0.02] animate-pulse" />
                ))}
            </div>
        )
    }

    if (error === 'unavailable') {
        return (
            <div className="text-center py-20">
                <AlertCircle className="w-14 h-14 text-gray-700 mx-auto mb-4" />
                <h3 role="alert" className="text-lg font-semibold text-gray-900 dark:text-white mb-2">{t('landingPagesTab.unavailable')}</h3>
                <p className="text-gray-500 text-sm max-w-sm mx-auto">
                    {t('landingPagesTab.unavailableDescription')}
                </p>
                <button onClick={fetchLandingPages}>{t('ui.retry')}</button>
            </div>
        )
    }

    if (error === 'session') {
        return (
            <div className="text-center py-20">
                <Globe className="w-14 h-14 text-gray-700 mx-auto mb-4" />
                <h3 className="text-lg font-semibold text-gray-900 dark:text-white mb-2">{t('landingPagesTab.session')}</h3>
                <p className="text-gray-500 text-sm">{t('landingPagesTab.sessionDescription')}</p>
            </div>
        )
    }

    if (!pages.length) {
        return (
            <div className="text-center py-20">
                <Globe className="w-14 h-14 text-gray-400 mx-auto mb-4" />
                <h3 className="text-lg font-semibold text-gray-900 dark:text-white mb-2">{t('landingPagesTab.empty')}</h3>
                <p className="text-gray-500 text-sm max-w-sm mx-auto">
                    {t('landingPagesTab.emptyDescription')}
                </p>
            </div>
        )
    }

    return (
        <div className="space-y-4">
            <p className="text-sm text-gray-600 dark:text-gray-400">{t('ux.designHint')}</p>
            {/* Header */}
            <div className="flex items-center justify-between">
                <div>
                    <p className="text-sm text-gray-500">
                        {formatNumber(pages.length)} {t('landingPagesTab.count')}
                    </p>
                    {affiliateCode && (
                        <p className="text-xs text-gray-400 mt-0.5">
                            {t('landingPagesTab.code')}: <code dir="ltr" className="text-[#6C63FF] bg-[#6C63FF]/5 px-1.5 py-0.5 rounded font-mono">{affiliateCode}</code>
                        </p>
                    )}
                </div>
            </div>

            {/* Landing Pages Grid */}
            <div className="grid grid-cols-1 md:grid-cols-2 xl:grid-cols-3 gap-4">
                {pages.map((lp, i) => {
                    const currentTemplate = selectedTemplates[lp.productId] || 'Classic:v1'
                    const effectiveUrl = getEffectiveUrl(lp)
                    const dirty = currentTemplate !== lp.templateType

                    return (
                        <motion.div
                            key={lp.productId}
                            initial={{ opacity: 0, y: 10 }}
                            animate={{ opacity: 1, y: 0 }}
                            transition={{ delay: i * 0.05 }}
                            className="rounded-xl border border-gray-200 dark:border-white/[0.06] bg-white dark:bg-white/[0.02] overflow-hidden hover:border-[#6C63FF]/30 transition-all duration-200"
                        >
                            {/* Product Image */}
                            <div className="aspect-[16/9] bg-gray-100 dark:bg-white/[0.03] relative overflow-hidden">
                                <PortalProductImage src={affiliateProductMediaUrl(lp.mainImage, lp.absoluteLandingUrl)} alt={lp.productName} />

                                {/* Stock Badge */}
                                {lp.isOutOfStock && (
                                    <span className="absolute top-2 right-2 px-2 py-0.5 rounded-full text-xs font-medium bg-red-700/95 text-white">
                                        {t('portal.badges.outOfStock')}
                                    </span>
                                )}

                                {/* Commission Badge */}
                                <span className="absolute top-2 left-2 px-2 py-0.5 rounded-full text-xs font-medium bg-emerald-700/95 text-white">
                                    {formatNumber(lp.commissionRatePercentage / 100, { style: 'percent' })} {t('landingPagesTab.commission')}
                                </span>
                            </div>

                            {/* Product Details */}
                            <div className="p-4 space-y-3">
                                <div>
                                    <h3 className="font-semibold text-sm text-gray-900 dark:text-white line-clamp-1">
                                        {lp.productName}
                                    </h3>
                                    <p className="text-xs text-gray-500 mt-0.5">
                                        {lp.storeName} · {formatCurrency(lp.price, lp.currency)}
                                    </p>
                                </div>

                                {/* Est. Commission */}
                                <div className="flex items-center justify-between text-xs">
                                    <span className="text-gray-500">{t('landingPagesTab.estimate')}</span>
                                    <span className="text-emerald-700 dark:text-emerald-300 font-medium">
                                        {formatCurrency(lp.estimatedCommission, lp.currency)}
                                    </span>
                                </div>

                                {/* AF-28: Template Selector */}
                                <div className="bg-gray-50 dark:bg-white/[0.03] rounded-lg p-2.5">
                                    <div className="flex items-center gap-1.5 mb-1.5">
                                        <Palette className="w-3 h-3 text-[#6C63FF]" />
                                        <span className="text-[10px] font-semibold text-gray-600 dark:text-gray-400 uppercase tracking-wider">
                                            {t('landingPagesTab.design')}
                                        </span>
                                    </div>
                                    <div className="flex gap-1">
                                        {TEMPLATE_OPTIONS.map(opt => (
                                            <button
                                                key={opt.value}
                                                aria-pressed={currentTemplate === opt.value}
                                                disabled={savingId === lp.productId}
                                                onClick={() => handleTemplateChange(lp.productId, opt.value)}
                                                className={`flex-1 py-1.5 px-1 rounded-md text-[10px] font-medium transition-all duration-150 ${currentTemplate === opt.value
                                                    ? 'bg-[#6C63FF] text-white shadow-sm shadow-[#6C63FF]/25'
                                                    : 'bg-white dark:bg-white/[0.05] text-gray-500 dark:text-gray-400 border border-gray-200 dark:border-white/[0.08] hover:border-[#6C63FF]/40 hover:text-[#6C63FF]'
                                                    }`}
                                                title={t(`landingPagesTab.${opt.key}Description`)}
                                            >
                                                {t(`landingPagesTab.${opt.key}`)}
                                            </button>
                                        ))}
                                    </div>
                                </div>

                                <p className="text-xs text-gray-500">{t(`ux.${currentTemplate === 'Classic:v1' ? 'classicDesc' : currentTemplate === 'Storytelling:v1' ? 'storyDesc' : 'minimalDesc'}`)}</p>
                                <div className="flex flex-wrap items-center justify-between gap-2">
                                    <span className="text-xs text-gray-500">{t(dirty ? 'ux.unsaved' : lp.hasSavedDesign ? 'ux.saved' : 'ux.defaultDesign')}</span>
                                    <button onClick={() => saveDesign(lp)} disabled={savingId != null || (!dirty && lp.hasSavedDesign)} className="rounded-lg bg-[#6C63FF] px-3 py-2 text-xs font-medium text-white disabled:opacity-50">{t(savingId === lp.productId ? 'ux.saving' : 'ux.save')}</button>
                                </div>
                                {dirty && <p className="text-xs text-amber-700 dark:text-amber-300">{t('ux.saveFirst')}</p>}
                                {feedback?.id === lp.productId && <p role={feedback.error ? 'alert' : 'status'} className={`text-xs ${feedback.error ? 'text-red-600 dark:text-red-300' : 'text-emerald-700 dark:text-emerald-300'}`}>{t(`ux.${feedback.key}`)}</p>}
                                {/* URL Display */}
                                <div className="bg-gray-50 dark:bg-white/[0.03] rounded-lg px-3 py-2">
                                    <code dir="ltr" className="text-[11px] text-gray-600 dark:text-gray-400 font-mono break-all">
                                        {effectiveUrl}
                                    </code>
                                </div>

                                {/* Actions */}
                                <div className="flex gap-2">
                                    <button
                                        onClick={() => copyUrl(lp)}
                                        disabled={dirty || savingId === lp.productId}
                                        className={`flex-1 flex items-center justify-center gap-1.5 py-2 rounded-lg text-xs font-medium transition-all ${copiedId === lp.productId
                                            ? 'bg-emerald-500/10 text-emerald-400 border border-emerald-500/20'
                                            : 'bg-[#6C63FF]/10 text-[#6C63FF] border border-[#6C63FF]/20 hover:bg-[#6C63FF]/20'
                                            }`}
                                    >
                                        {copiedId === lp.productId ? (
                                            <><Check className="w-3.5 h-3.5" /> {t('landingPagesTab.copied')}</>
                                        ) : (
                                            <><Copy className="w-3.5 h-3.5" /> {t('landingPagesTab.copy')}</>
                                        )}
                                    </button>
                                    <button
                                        onClick={() => previewPage(lp)}
                                        disabled={dirty || savingId === lp.productId}
                                        aria-label={t('ux.open')}
                                        className="flex items-center justify-center gap-1.5 px-3 py-2 rounded-lg text-xs font-medium bg-gray-100 dark:bg-white/[0.03] text-gray-600 dark:text-gray-400 border border-gray-200 dark:border-white/[0.06] hover:border-[#6C63FF]/30 transition-all"
                                        title={t('ux.open')}
                                    >
                                        <ExternalLink className="w-3.5 h-3.5" />
                                    </button>
                                </div>
                            </div>
                        </motion.div>
                    )
                })}
            </div>

            {/* Pagination */}
            {totalPages > 1 && (
                <div className="flex items-center justify-center gap-2 pt-4">
                    <button
                        onClick={() => setPage(p => Math.max(1, p - 1))}
                        disabled={page <= 1}
                        className="px-3 py-1.5 rounded-lg text-xs font-medium bg-gray-100 dark:bg-white/[0.03] text-gray-600 dark:text-gray-400 disabled:opacity-40 hover:bg-gray-200 dark:hover:bg-white/[0.06] transition-all"
                    >
                        {t('ui.previous')}
                    </button>
                    <span className="text-xs text-gray-500">
                        {t('ui.page')} {formatNumber(page)} {t('portal.ordersTab.of')} {formatNumber(totalPages)}
                    </span>
                    <button
                        onClick={() => setPage(p => Math.min(totalPages, p + 1))}
                        disabled={page >= totalPages}
                        className="px-3 py-1.5 rounded-lg text-xs font-medium bg-gray-100 dark:bg-white/[0.03] text-gray-600 dark:text-gray-400 disabled:opacity-40 hover:bg-gray-200 dark:hover:bg-white/[0.06] transition-all"
                    >
                        {t('ui.next')}
                    </button>
                </div>
            )}
        </div>
    )
}
