import { useState, useEffect, useCallback, useRef } from 'react'
import { useTranslation } from 'react-i18next'
import api from '../services/api'
import { fetchAllPagedArray } from '../services/pagedArrayLoader'
import { useToast } from '../components/Toast'
import CommissionPreviewPanel from '../pos-admin/commercehub/CommissionPreviewPanel'
import { useFormatting } from '../hooks/useFormatting'
import { useAuthStore } from '../store/authStore'
import {
    deriveCurrentVendorScopedArray,
    deriveCurrentVendorScopedDto,
} from '../auth/commerceHubVendorIdentityAuthority'
import { deriveCommerceHubVendorEligibilityPresentation } from '../auth/posVendorSessionAuthority'

const normalizeAttributeKey = (value) =>
    String(value || '')
        .trim()
        .toLowerCase()
        .replace(/[\s.-]+/g, '_')
        .replace(/[^a-z0-9_]/g, '')
        .replace(/_{2,}/g, '_')
        .replace(/^_+|_+$/g, '')

const hasAttributeValue = (value) => {
    if (value === null || value === undefined) return false
    if (typeof value === 'string') return value.trim() !== ''
    return true
}

const safeParseAttributes = (attributesJson) => {
    if (!attributesJson) return {}

    try {
        const parsed = typeof attributesJson === 'string'
            ? JSON.parse(attributesJson)
            : attributesJson

        if (!parsed || typeof parsed !== 'object' || Array.isArray(parsed)) {
            return {}
        }

        return Object.entries(parsed).reduce((acc, [key, value]) => {
            const normalizedKey = normalizeAttributeKey(key)
            if (!normalizedKey) return acc
            acc[normalizedKey] = value
            return acc
        }, {})
    } catch (error) {
        console.warn('Failed to parse CommerceHub attributes JSON:', error)
        return {}
    }
}

const safeParseOptions = (optionsJson) => {
    if (!optionsJson) return []

    try {
        const parsed = JSON.parse(optionsJson)
        return Array.isArray(parsed) ? parsed : []
    } catch (error) {
        console.warn('Failed to parse CommerceHub attribute options JSON:', error)
        return []
    }
}

const getCommerceHubErrorMessage = (error, fallbackMessage) =>
    error?.response?.data?.message || error?.message || fallbackMessage

const getCommerceHubCurrencyPolicyMessage = (variant, fallbackMessage) =>
    variant?.commerceHubCurrencyPolicyMessage ||
    fallbackMessage

const formatProductCurrencyPrice = (formatCurrency, amount, currency, unavailableLabel) => {
    const normalizedCurrency = typeof currency === 'string' ? currency.trim() : ''
    if (!normalizedCurrency) {
        return unavailableLabel
    }

    return formatCurrency(amount, normalizedCurrency)
}

/**
 * CommerceHub Settings — Admin Control Panel
 * 
 * Features:
 * - Product publishing table with publish/unpublish toggles
 * - Global category cascade selector (parent → child)
 * - Dynamic attribute form based on selected category
 * - Validation before publish
 */
export default function CommerceHubSettings() {
    const { t } = useTranslation()
    const { isRTL, formatCurrency, formatNumber } = useFormatting()
    const { showSuccess, showError } = useToast()
    const currentUser = useAuthStore((state) => state.user)
    const currentVendorId = currentUser?.tenantId || ''
    const vendorEligibility = deriveCommerceHubVendorEligibilityPresentation(currentUser)
    const canManageCatalog = vendorEligibility?.canManageCatalog === true

    const [loading, setLoading] = useState(true)
    const [products, setProducts] = useState([])
    const [catalogSearch, setCatalogSearch] = useState('')
    const [listingSearch, setListingSearch] = useState('')
    const [listingFilters, setListingFilters] = useState({
        status: '',
        categoryId: '',
        inventoryStatus: '',
        overrideEnabled: '',
    })
    const [listingRows, setListingRows] = useState([])
    const [listingKpis, setListingKpis] = useState(null)
    const [listingAnalytics, setListingAnalytics] = useState(null)
    const [listingNotifications, setListingNotifications] = useState([])
    const [selectedListingIds, setSelectedListingIds] = useState({})
    const [bulkSaving, setBulkSaving] = useState(false)
    const [categories, setCategories] = useState([])
    const [saving, setSaving] = useState({}) // { [variantId]: true }
    const [previewState, setPreviewState] = useState({})
    
    // Expanded product rows
    const [expanded, setExpanded] = useState({})
    
    // Per-variant editing state: { [variantId]: { categoryId, commerceHubPrice, attributesJson, attributes: {} } }
    const [editState, setEditState] = useState({})
    
    // Loaded attribute definitions keyed by categoryId
    const [categoryAttributes, setCategoryAttributes] = useState({})
    const categoryAttributesRef = useRef({})
    const pendingAttributeLoadsRef = useRef({})
    const catalogSearchRef = useRef(null)

    useEffect(() => {
        categoryAttributesRef.current = categoryAttributes
    }, [categoryAttributes])

    // ─── Data Loading ────────────────────────────────────────
    const fetchData = useCallback(async () => {
        setLoading(true)
        setProducts([])
        setCategories([])
        setListingRows([])
        setListingKpis(null)
        setListingAnalytics(null)
        setListingNotifications([])
        setSelectedListingIds({})
        try {
            const listingParams = {
                page: 1,
                pageSize: 50,
            }
            if (listingSearch.trim()) listingParams.q = listingSearch.trim()
            if (listingFilters.status) listingParams.status = listingFilters.status
            if (listingFilters.categoryId) listingParams.categoryId = listingFilters.categoryId
            if (listingFilters.inventoryStatus) listingParams.inventoryStatus = listingFilters.inventoryStatus
            if (listingFilters.overrideEnabled) listingParams.overrideEnabled = listingFilters.overrideEnabled === 'true'

            const [productsRes, categoriesRes, listingsRes, kpisRes, analyticsRes, notificationsRes] = await Promise.all([
                fetchAllPagedArray('/admin/commercehub/products', {}, { pageSize: 50 }),
                api.get('/commercehub/categories/tree'),
                api.get('/admin/commercehub/listings', { params: listingParams }),
                api.get('/admin/commercehub/listings/kpis'),
                api.get('/admin/commercehub/listings/analytics'),
                api.get('/admin/commercehub/listings/notifications')
            ])
            const scopedProducts = deriveCurrentVendorScopedArray(productsRes || [], currentUser)
            const scopedListings = deriveCurrentVendorScopedArray(listingsRes.data || [], currentUser)
            const scopedKpis = deriveCurrentVendorScopedDto(kpisRes.data || null, currentUser)
            const scopedAnalytics = deriveCurrentVendorScopedDto(analyticsRes.data || null, currentUser)
            const scopedNotifications = deriveCurrentVendorScopedArray(notificationsRes.data || [], currentUser)

            setProducts(scopedProducts)
            setCategories(categoriesRes.data || [])
            setListingRows(scopedListings)
            setListingKpis(scopedKpis)
            setListingAnalytics(scopedAnalytics)
            setListingNotifications(scopedNotifications)
            
            // Initialize edit state from existing listings
            const state = {}
            for (const product of scopedProducts) {
                for (const variant of (product.variants || [])) {
                    if (variant.listing) {
                        state[variant.variantId] = {
                            categoryId: variant.listing.categoryId,
                            parentCategoryId: variant.listing.parentCategoryId,
                            commerceHubPrice: variant.listing.commerceHubPrice,
                            pricingMode: variant.listing.commerceHubPrice != null ? 'custom' : 'source',
                            priceOverrideMutationVersion: variant.listing.priceOverrideMutationVersion ?? 0,
                            attributesJson: variant.listing.attributesJson,
                            attributes: safeParseAttributes(variant.listing.attributesJson)
                        }
                    }
                }
            }
            setEditState(state)
        } catch (error) {
            console.error('Failed to load CommerceHub data:', error)
            showError(getCommerceHubErrorMessage(error, t('commerceHub.loadFailed')))
        } finally {
            setLoading(false)
        }
    }, [currentUser, listingFilters, listingSearch, showError, t])

    useEffect(() => { fetchData() }, [fetchData])

    // ─── Category Tree Helpers ───────────────────────────────
    const rootCategories = categories.filter(c => !c.parentId)
    const getChildren = (parentId) => categories.filter(c => c.parentId === parentId)
    const getCategoryName = (categoryId) => {
        if (!categoryId) return ''

        const category = categories.find(item => item.id === categoryId)
        if (!category) return ''

        return isRTL ? (category.nameAr || category.name) : category.name
    }
    const getInventoryStatusLabel = (status) => {
        if (status === 'in_stock') return t('commerceHub.publishing.inStock')
        if (status === 'out_of_stock') return t('commerceHub.publishing.outOfStock')
        return t('commerceHub.publishing.unavailable')
    }

    // ─── Load attributes for a category ─────────────────────
    const loadAttributes = useCallback(async (categoryId) => {
        if (!categoryId || categoryAttributesRef.current[categoryId]) return categoryAttributesRef.current[categoryId]
        if (pendingAttributeLoadsRef.current[categoryId]) return pendingAttributeLoadsRef.current[categoryId]

        const request = (async () => {
            try {
                const res = await api.get(`/commercehub/categories/${categoryId}/attributes`)
                const nextAttributes = res.data || []
                setCategoryAttributes(prev => {
                    if (prev[categoryId]) return prev
                    return { ...prev, [categoryId]: nextAttributes }
                })
                return nextAttributes
            } catch (error) {
                console.error('Failed to load attributes:', error)
                showError(getCommerceHubErrorMessage(error, t('commerceHub.loadFailed')))
                return []
            } finally {
                delete pendingAttributeLoadsRef.current[categoryId]
            }
        })()

        pendingAttributeLoadsRef.current[categoryId] = request

        return request
    }, [showError, t])

    useEffect(() => {
        const categoryIds = [...new Set(
            Object.values(editState)
                .map(state => state?.categoryId)
                .filter(Boolean)
        )]

        if (categoryIds.length === 0) return

        let isCancelled = false

        ;(async () => {
            const uncachedCategoryIds = categoryIds.filter(categoryId =>
                !categoryAttributesRef.current[categoryId]
            )

            if (uncachedCategoryIds.length === 0) return

            await Promise.all(uncachedCategoryIds.map(categoryId => loadAttributes(categoryId)))

            if (isCancelled) return
        })()

        return () => {
            isCancelled = true
        }
    }, [editState, loadAttributes])

    // ─── Edit State Helpers ─────────────────────────────────
    const getEditState = (variantId) => editState[variantId] || { parentCategoryId: '', categoryId: '', pricingMode: 'source', commerceHubPrice: null, attributes: {} }
    
    const updateEditState = (variantId, updates) => {
        setEditState(prev => ({
            ...prev,
            [variantId]: { ...getEditState(variantId), ...updates }
        }))
    }

    const handleCategoryChange = async (variantId, categoryId) => {
        updateEditState(variantId, { categoryId, attributes: {} })
        if (categoryId) {
            await loadAttributes(categoryId)
        }
    }

    const handleAttributeChange = (variantId, attrKey, value) => {
        const current = getEditState(variantId)
        updateEditState(variantId, {
            attributes: { ...current.attributes, [attrKey]: value }
        })
    }

    // ─── Local State Update Helper ──────────────────────────
    // ─── Publish / Update / Unpublish ───────────────────────
    const handlePublish = async (variant) => {
        if (!canManageCatalog) {
            showError(t('commerceHub.publishing.notAuthorized'))
            return
        }

        if (variant.isCommerceHubCurrencyEligible !== true) {
            showError(getCommerceHubCurrencyPolicyMessage(variant, t('commerceHub.publishing.currencyBlocked', { currency: variant?.productCurrency || t('common.unavailable') })))
            return
        }

        if (variant.isCommerceHubPublicationEligible === false) {
            showError(variant.commerceHubPublicationEligibilityMessage || t('commerceHub.publicationBlocked'))
            return
        }

        const state = getEditState(variant.variantId)
        if (!state.categoryId) {
            showError(t('commerceHub.categoryRequired'))
            return
        }
        if (!state.parentCategoryId) {
            showError(t('commerceHub.mainCategoryRequired'))
            return
        }
        if (state.pricingMode === 'custom' && (!Number.isFinite(state.commerceHubPrice) || state.commerceHubPrice <= 0)) {
            showError(t('commerceHub.customPriceRequired'))
            return
        }
        if (!previewState[variant.variantId]?.ready || !previewState[variant.variantId]?.ruleId) {
            showError(t('commerceHub.commission.publishBlocked'))
            return
        }

        // Validate required attributes
        const attrs = categoryAttributes[state.categoryId] || []
        for (const attr of attrs) {
            if (attr.isRequired && !hasAttributeValue(state.attributes[attr.key])) {
                showError(t('commerceHub.attributeRequired', {
                    attribute: isRTL ? (attr.nameAr || attr.name) : attr.name,
                }))
                return
            }
        }

        setSaving(prev => ({ ...prev, [variant.variantId]: true }))
        try {
            const normalizedAttributes = Object.entries(state.attributes || {}).reduce((acc, [key, value]) => {
                const normalizedKey = normalizeAttributeKey(key)
                if (!normalizedKey || !hasAttributeValue(value)) return acc
                acc[normalizedKey] = value
                return acc
            }, {})

            const payload = {
                variantId: variant.variantId,
                commerceHubCategoryId: state.categoryId,
                parentCategoryId: state.parentCategoryId,
                pricingMode: state.pricingMode,
                commerceHubPrice: state.pricingMode === 'custom' ? state.commerceHubPrice : null,
                expectedCommissionRuleId: previewState[variant.variantId].ruleId,
                expectedPriceOverrideMutationVersion: state.priceOverrideMutationVersion ?? 0,
                attributesJson: Object.keys(normalizedAttributes).length > 0
                    ? normalizedAttributes
                    : null
            }

            if (variant.listing?.isPublished) {
                await api.put('/admin/commercehub/update', payload)
                showSuccess(t('commerceHub.messages.updated'))
            } else {
                await api.post('/admin/commercehub/publish', payload)
                showSuccess(t('commerceHub.messages.published'))
            }
            
            await fetchData()
        } catch (error) {
            showError(getCommerceHubErrorMessage(error, t('commerceHub.messages.publishFailed')))
        } finally {
            setSaving(prev => ({ ...prev, [variant.variantId]: false }))
        }
    }

    const handleUnpublish = async (variant) => {
        if (!canManageCatalog) {
            showError(t('commerceHub.publishing.notAuthorized'))
            return
        }

        setSaving(prev => ({ ...prev, [variant.variantId]: true }))
        try {
            await api.delete('/admin/commercehub/unpublish', {
                data: { variantId: variant.variantId }
            })
            showSuccess(t('commerceHub.messages.unpublished'))
            
            await fetchData()
        } catch (error) {
            showError(getCommerceHubErrorMessage(error, t('commerceHub.messages.unpublishFailed')))
        } finally {
            setSaving(prev => ({ ...prev, [variant.variantId]: false }))
        }
    }

    const handleParentCategoryChange = async (variantId, parentCategoryId) => {
        const children = getChildren(parentCategoryId)
        const categoryId = parentCategoryId && children.length === 0 ? parentCategoryId : ''
        updateEditState(variantId, { parentCategoryId, categoryId, attributes: {} })
        if (categoryId) await loadAttributes(categoryId)
    }

    const handlePreviewResolution = useCallback((variantId, resolution) => {
        setPreviewState(prev => {
            const current = prev[variantId]
            if (current?.ready === resolution.ready && current?.ruleId === resolution.ruleId) return prev
            return { ...prev, [variantId]: resolution }
        })
    }, [])

    const toggleListingSelection = (listingId) => {
        setSelectedListingIds(prev => ({
            ...prev,
            [listingId]: !prev[listingId]
        }))
    }

    const selectedListingIdList = Object.entries(selectedListingIds)
        .filter(([, selected]) => selected)
        .map(([listingId]) => listingId)

    const handleBulkOperation = async (operation) => {
        if (!canManageCatalog) {
            showError(t('commerceHub.publishing.notAuthorized'))
            return
        }

        if (selectedListingIdList.length === 0) {
            showError(t('commerceHub.publishing.selectListing'))
            return
        }

        setBulkSaving(true)
        try {
            const response = await api.post('/admin/commercehub/listings/bulk', {
                operation,
                listingIds: selectedListingIdList
            })
            const failed = response.data?.failed ?? 0
            if (failed > 0) {
                showError(t('commerceHub.publishing.bulkFailedCount', { count: failed }))
            } else {
                showSuccess(t('commerceHub.publishing.bulkCompleted'))
            }
            setSelectedListingIds({})
            await fetchData()
        } catch (error) {
            showError(getCommerceHubErrorMessage(error, t('commerceHub.publishing.bulkFailed')))
        } finally {
            setBulkSaving(false)
        }
    }

    // ─── Dynamic Attribute Form ─────────────────────────────
    const renderAttributeForm = (variantId, categoryId) => {
        const attrs = categoryAttributes[categoryId]
        if (!attrs || attrs.length === 0) return null
        const state = getEditState(variantId)

        return (
            <div className="grid grid-cols-1 sm:grid-cols-2 lg:grid-cols-3 gap-3 mt-3">
                {attrs.map(attr => {
                    const value = state.attributes[attr.key] ?? ''
                    const options = safeParseOptions(attr.optionsJson)

                    return (
                        <div key={attr.id} className="space-y-1">
                            <label className="text-xs font-medium text-gray-600 flex items-center gap-1">
                                {isRTL ? (attr.nameAr || attr.name) : attr.name}
                                {attr.isRequired && <span className="text-red-500">*</span>}
                            </label>
                            {attr.type === 'select' ? (
                                <select
                                    aria-label={`${isRTL ? (attr.nameAr || attr.name) : attr.name} — ${variantId}`}
                                    value={value}
                                    onChange={e => handleAttributeChange(variantId, attr.key, e.target.value)}
                                    className="w-full px-3 py-2 text-sm border border-gray-300 rounded-lg focus:ring-2 focus:ring-blue-500 focus:border-blue-500 bg-white"
                                >
                                    <option value="">{t('commerceHub.selectPlaceholder')}</option>
                                    {options.map(opt => (
                                        <option key={opt} value={opt}>{opt}</option>
                                    ))}
                                </select>
                            ) : attr.type === 'number' ? (
                                <input
                                    type="number"
                                    value={value}
                                    onChange={e => handleAttributeChange(variantId, attr.key, e.target.value)}
                                    className="w-full px-3 py-2 text-sm border border-gray-300 rounded-lg focus:ring-2 focus:ring-blue-500 focus:border-blue-500"
                                    placeholder={isRTL ? (attr.nameAr || attr.name) : attr.name}
                                />
                            ) : (
                                <input
                                    type="text"
                                    value={value}
                                    onChange={e => handleAttributeChange(variantId, attr.key, e.target.value)}
                                    className="w-full px-3 py-2 text-sm border border-gray-300 rounded-lg focus:ring-2 focus:ring-blue-500 focus:border-blue-500"
                                    placeholder={isRTL ? (attr.nameAr || attr.name) : attr.name}
                                />
                            )}
                        </div>
                    )
                })}
            </div>
        )
    }

    // ─── Category Cascade Selector ──────────────────────────
    const renderCategorySelector = (variantId) => {
        const state = getEditState(variantId)
        const parentId = state.parentCategoryId || ''
        const subs = parentId ? getChildren(parentId) : []

        return (
            <div className="grid grid-cols-1 gap-3 sm:grid-cols-2">
                {/* Root category */}
                <div>
                    <label className="text-xs font-medium text-gray-500 mb-1 block">
                        {t('commerceHub.category')}
                    </label>
                    <select
                        aria-label={`${t('commerceHub.category')} — ${variantId}`}
                        value={parentId}
                        onChange={e => handleParentCategoryChange(variantId, e.target.value)}
                        className="w-full px-3 py-2 text-sm border border-gray-300 rounded-lg focus:ring-2 focus:ring-blue-500 focus:border-blue-500 bg-white"
                    >
                        <option value="">{t('commerceHub.selectPlaceholder')}</option>
                        {rootCategories.map(cat => (
                            <option key={cat.id} value={cat.id}>
                                {cat.iconEmoji} {isRTL ? (cat.nameAr || cat.name) : cat.name}
                            </option>
                        ))}
                    </select>
                </div>

                <div>
                    <label className="text-xs font-medium text-gray-500 mb-1 block">
                        {t('commerceHub.subcategory')}
                    </label>
                    <select
                        aria-label={`${t('commerceHub.subcategory')} — ${variantId}`}
                        value={subs.length === 0 && parentId ? parentId : state.categoryId}
                        onChange={e => handleCategoryChange(variantId, e.target.value)}
                        disabled={!parentId || subs.length === 0}
                        className="w-full px-3 py-2 text-sm border border-gray-300 rounded-lg focus:ring-2 focus:ring-blue-500 focus:border-blue-500 bg-white disabled:bg-slate-100"
                    >
                        <option value="">{!parentId ? t('commerceHub.selectMainCategoryFirst') : t('commerceHub.selectPlaceholder')}</option>
                        {parentId && subs.length === 0 && <option value={parentId}>{t('commerceHub.subcategoryNotRequired')}</option>}
                        {subs.map(cat => (
                            <option key={cat.id} value={cat.id}>
                                {cat.iconEmoji} {isRTL ? (cat.nameAr || cat.name) : cat.name}
                            </option>
                        ))}
                    </select>
                </div>
            </div>
        )
    }

    // ─── Render ─────────────────────────────────────────────
    if (loading) {
        return (
            <div className="flex items-center justify-center py-20">
                <div className="animate-spin rounded-full h-10 w-10 border-b-2 border-blue-600"></div>
            </div>
        )
    }

    const publishedCount = products.reduce((acc, p) =>
        acc + p.variants.filter(v => v.listing?.isPublished).length, 0)
    const totalVariants = products.reduce((acc, p) => acc + p.variants.length, 0)
    const normalizedCatalogSearch = catalogSearch.trim().toLowerCase()
    const filteredProducts = normalizedCatalogSearch
        ? products.filter(product =>
            product.name?.toLowerCase().includes(normalizedCatalogSearch) ||
            product.variants?.some(variant => variant.sku?.toLowerCase().includes(normalizedCatalogSearch)))
        : products

    return (
        <div className="mx-auto max-w-[1500px] space-y-5" dir={isRTL ? 'rtl' : 'ltr'}>
            {/* Header */}
            <div className="flex flex-col gap-4 border-b border-slate-200 pb-5 sm:flex-row sm:items-start sm:justify-between">
                <div className="flex min-w-0 max-w-full items-start gap-3">
                    <div className="flex h-10 w-10 shrink-0 items-center justify-center rounded-xl bg-indigo-100 text-lg">
                        <span aria-hidden="true">🏪</span>
                    </div>
                    <div>
                        <div className="flex flex-wrap items-center gap-2">
                            <h1 className="text-2xl font-bold text-slate-950">{t('commerceHub.publishing.title')}</h1>
                            <span className="rounded-full bg-emerald-100 px-2.5 py-1 text-xs font-bold text-emerald-800">{t('commerceHub.publishing.approvedSeller')}</span>
                        </div>
                        <p className="mt-1 max-w-2xl text-sm leading-6 text-slate-600">
                            {t('commerceHub.subtitle')}
                        </p>
                    </div>
                </div>
                <div className="flex flex-wrap gap-2">
                    <button onClick={() => { catalogSearchRef.current?.focus(); catalogSearchRef.current?.scrollIntoView({ behavior: 'smooth', block: 'center' }) }} className="rounded-xl bg-indigo-600 px-4 py-2.5 text-sm font-semibold text-white shadow-sm hover:bg-indigo-700">{t('commerceHub.publishing.findProduct')}</button>
                    <button onClick={fetchData} className="rounded-xl border border-slate-300 bg-white px-4 py-2.5 text-sm font-semibold text-slate-700 shadow-sm hover:bg-slate-50">{t('common.refresh')}</button>
                </div>
            </div>

            {/* Stats */}
            <div className="grid grid-cols-1 gap-3 sm:grid-cols-3">
                <div className="rounded-xl border border-slate-200 bg-white p-4 shadow-sm">
                    <div className="text-xs font-semibold uppercase tracking-wide text-slate-500">{t('commerceHub.stats.totalProducts')}</div>
                    <div className="mt-1 text-2xl font-bold text-slate-950">{formatNumber(products.length)}</div>
                </div>
                <div className="rounded-xl border border-slate-200 bg-white p-4 shadow-sm">
                    <div className="text-xs font-semibold uppercase tracking-wide text-slate-500">{t('commerceHub.stats.totalVariants')}</div>
                    <div className="mt-1 text-2xl font-bold text-slate-950">{formatNumber(totalVariants)}</div>
                </div>
                <div className="rounded-xl border border-emerald-200 bg-emerald-50 p-4 shadow-sm">
                    <div className="text-xs font-semibold uppercase tracking-wide text-emerald-700">{t('commerceHub.stats.published')}</div>
                    <div className="mt-1 text-2xl font-bold text-emerald-800">{formatNumber(publishedCount)}</div>
                </div>
            </div>

            {/* Authoritative Listing Operations Console */}
            <div className="space-y-4 rounded-2xl border border-slate-200 bg-white p-5 shadow-sm">
                <div className="flex items-start justify-between gap-4 flex-wrap">
                    <div className="min-w-0 max-w-full">
                        <h2 className="text-lg font-bold text-slate-950">{t('commerceHub.publishing.listingOverview')}</h2>
                        <p className="text-sm text-gray-500">
                            {t('commerceHub.publishing.listingOverviewHelp')}
                        </p>
                    </div>
                    <div className="flex flex-wrap gap-2">
                        <button
                            onClick={() => handleBulkOperation('unpublish')}
                            disabled={bulkSaving || selectedListingIdList.length === 0 || !canManageCatalog}
                            className="px-3 py-2 text-sm font-medium text-red-700 bg-red-50 border border-red-200 rounded-lg disabled:opacity-50"
                        >
                            {t('commerceHub.publishing.bulkUnpublish')}
                        </button>
                        <button
                            onClick={() => handleBulkOperation('remove_override')}
                            disabled={bulkSaving || selectedListingIdList.length === 0 || !canManageCatalog}
                            className="px-3 py-2 text-sm font-medium text-blue-700 bg-blue-50 border border-blue-200 rounded-lg disabled:opacity-50"
                        >
                            {t('commerceHub.publishing.bulkRemoveOverrides')}
                        </button>
                    </div>
                </div>

                <div className="grid grid-cols-2 md:grid-cols-6 gap-3">
                    {[
                        [t('commerceHub.publishing.total'), listingKpis?.totalListings],
                        [t('commerceHub.publishing.published'), listingKpis?.publishedListings],
                        [t('commerceHub.publishing.unpublished'), listingKpis?.unpublishedListings],
                        [t('commerceHub.publishing.suppressed'), listingKpis?.suppressedListings],
                        [t('commerceHub.publishing.overrideEnabled'), listingKpis?.overrideEnabledListings],
                        [t('commerceHub.publishing.needsAttention'), listingKpis?.orphanedListings],
                    ].map(([label, value]) => (
                        <div key={label} className="border border-gray-100 rounded-lg p-3">
                            <div className="text-xs uppercase text-gray-500">{label}</div>
                            <div className="text-xl font-bold text-gray-900">{value == null ? (loading ? '…' : t('common.unavailable', 'Unavailable')) : formatNumber(value)}</div>
                        </div>
                    ))}
                </div>

                <div className="grid grid-cols-1 md:grid-cols-5 gap-3 [&>*]:min-w-0">
                    <input
                        value={listingSearch}
                        onChange={event => setListingSearch(event.target.value)}
                        placeholder={t('commerceHub.publishing.searchListings')}
                        className="md:col-span-2 px-3 py-2 text-sm border border-gray-300 rounded-lg"
                    />
                    <select
                        aria-label={t('commerceHub.publishing.statusFilter')}
                        value={listingFilters.status}
                        onChange={event => setListingFilters(prev => ({ ...prev, status: event.target.value }))}
                        className="px-3 py-2 text-sm border border-gray-300 rounded-lg bg-white"
                    >
                        <option value="">{t('commerceHub.publishing.allStatuses')}</option>
                        <option value="published">{t('commerceHub.publishing.published')}</option>
                        <option value="unpublished">{t('commerceHub.publishing.unpublished')}</option>
                        <option value="suppressed">{t('commerceHub.publishing.suppressed')}</option>
                        <option value="orphaned">{t('commerceHub.publishing.needsAttention')}</option>
                    </select>
                    <select
                        aria-label={t('commerceHub.publishing.inventoryFilter')}
                        value={listingFilters.inventoryStatus}
                        onChange={event => setListingFilters(prev => ({ ...prev, inventoryStatus: event.target.value }))}
                        className="px-3 py-2 text-sm border border-gray-300 rounded-lg bg-white"
                    >
                        <option value="">{t('commerceHub.publishing.allInventory')}</option>
                        <option value="in_stock">{t('commerceHub.publishing.inStock')}</option>
                        <option value="out_of_stock">{t('commerceHub.publishing.outOfStock')}</option>
                        <option value="unavailable">{t('commerceHub.publishing.unavailable')}</option>
                    </select>
                    <select
                        aria-label={t('commerceHub.publishing.priceFilter')}
                        value={listingFilters.overrideEnabled}
                        onChange={event => setListingFilters(prev => ({ ...prev, overrideEnabled: event.target.value }))}
                        className="px-3 py-2 text-sm border border-gray-300 rounded-lg bg-white"
                    >
                        <option value="">{t('commerceHub.publishing.allPriceModes')}</option>
                        <option value="true">{t('commerceHub.publishing.overrideEnabled')}</option>
                        <option value="false">{t('commerceHub.publishing.basePrice')}</option>
                    </select>
                </div>

                {listingNotifications.length > 0 && (
                    <div className="space-y-2">
                        {listingNotifications.slice(0, 3).map(notification => (
                            <div key={`${notification.listingId}-${notification.type}`} className="text-sm border border-amber-200 bg-amber-50 text-amber-800 rounded-lg px-3 py-2">
                                {notification.message}
                            </div>
                        ))}
                    </div>
                )}

                <div className="overflow-x-auto border border-gray-100 rounded-lg">
                    <table className="min-w-full text-sm">
                        <thead className="bg-gray-50 text-gray-600">
                            <tr>
                                <th className="px-3 py-2 text-start">{t('commerceHub.publishing.select')}</th>
                                <th className="px-3 py-2 text-start">{t('commerceHub.publishing.listing')}</th>
                                <th className="px-3 py-2 text-start">{t('commerceHub.publishing.statusLabel')}</th>
                                <th className="px-3 py-2 text-start">{t('commerceHub.category')}</th>
                                <th className="px-3 py-2 text-start">{t('commerceHub.publishing.inventory')}</th>
                                <th className="px-3 py-2 text-start">{t('commerceHub.publishing.priceMode')}</th>
                            </tr>
                        </thead>
                        <tbody>
                            {listingRows.map(row => (
                                <tr key={row.listingId} className="border-t border-gray-100">
                                    <td className="px-3 py-2">
                                        <input
                                            type="checkbox"
                                            aria-label={t('commerceHub.publishing.selectListingNamed', { name: row.productName, sku: row.sku || row.variantId })}
                                            checked={!!selectedListingIds[row.listingId]}
                                            onChange={() => toggleListingSelection(row.listingId)}
                                        />
                                    </td>
                                    <td className="px-3 py-2">
                                        <div className="font-medium text-gray-900">{row.productName}</div>
                                        <div className="text-xs text-gray-500">{row.sku || row.barcode || row.variantId}</div>
                                    </td>
                                    <td className="px-3 py-2">
                                        <div className="font-medium">
                                            {row.isSuppressed ? t('commerceHub.publishing.suppressed') : row.isOrphaned ? t('commerceHub.publishing.needsAttention') : row.isPublished ? t('commerceHub.publishing.published') : t('commerceHub.publishing.unpublished')}
                                        </div>
                                        {(row.suppressionReason || row.orphanReason) && (
                                            <div className="text-xs text-gray-500 max-w-xs">{row.suppressionReason || row.orphanReason}</div>
                                        )}
                                    </td>
                                    <td className="px-3 py-2">{getCategoryName(row.categoryId) || (isRTL ? t('common.unavailable') : row.categoryName) || '-'}</td>
                                    <td className="px-3 py-2">
                                        {getInventoryStatusLabel(row.inventoryStatus)} ({formatNumber(row.inventoryAvailable ?? 0)})
                                    </td>
                                    <td className="px-3 py-2">
                                        {row.overrideEnabled ? formatCurrency(row.commerceHubPrice || 0, 'EGP') : t('commerceHub.publishing.basePrice')}
                                    </td>
                                </tr>
                            ))}
                            {listingRows.length === 0 && (
                                <tr>
                                    <td colSpan={6} className="px-3 py-8 text-center text-gray-500">
                                        {t('commerceHub.publishing.noListings')}
                                    </td>
                                </tr>
                            )}
                        </tbody>
                    </table>
                </div>

                {listingAnalytics && (
                    <div className="text-xs text-gray-500">
                        {t('commerceHub.publishing.analyticsSummary', {
                            days: formatNumber(listingAnalytics.days),
                            views: formatNumber(listingAnalytics.listingViews),
                            searches: formatNumber(listingAnalytics.listingSearches),
                            conversions: formatNumber(listingAnalytics.listingConversions),
                        })}
                    </div>
                )}
            </div>

            <div className="flex flex-col gap-3 rounded-2xl border border-slate-200 bg-white p-4 shadow-sm sm:flex-row sm:items-center sm:justify-between">
                <div>
                    <h2 className="font-bold text-slate-950">{t('commerceHub.publishing.catalogProducts')}</h2>
                    <p className="mt-1 text-sm text-slate-500">{t('commerceHub.publishing.catalogProductsHelp')}</p>
                </div>
                <input ref={catalogSearchRef} value={catalogSearch} onChange={(event) => setCatalogSearch(event.target.value)} placeholder={t('commerceHub.publishing.searchCatalog')} className="w-full rounded-xl border border-slate-300 px-3 py-2.5 text-sm focus:border-indigo-500 focus:ring-2 focus:ring-indigo-100 sm:max-w-sm" />
            </div>

            {/* Products Table */}
            {products.length === 0 ? (
                <div className="bg-white border border-gray-200 rounded-xl p-12 text-center">
                    <span className="text-4xl">📦</span>
                    <h3 className="text-lg font-medium text-gray-900 mt-3">{t('commerceHub.emptyTitle')}</h3>
                    <p className="text-sm text-gray-500 mt-1">
                        {t('commerceHub.emptyDescription')}
                    </p>
                </div>
            ) : (
                <div className="space-y-3">
                    {filteredProducts.map(product => (
                        <div key={product.productId} className="bg-white border border-gray-200 rounded-xl overflow-hidden">
                            {/* Product Header (click to expand) */}
                            <button
                                onClick={() => setExpanded(prev => ({ ...prev, [product.productId]: !prev[product.productId] }))}
                                className="w-full flex items-center gap-4 px-5 py-4 hover:bg-gray-50 transition-colors"
                            >
                                {/* Product Image */}
                                <div className="w-10 h-10 bg-gray-100 rounded-lg overflow-hidden flex-shrink-0">
                                    {product.image ? (
                                        <img src={product.image} alt="" className="w-full h-full object-cover" />
                                    ) : (
                                        <div className="w-full h-full flex items-center justify-center text-gray-400">📷</div>
                                    )}
                                </div>
                                
                                <div className={`min-w-0 flex-1 ${isRTL ? 'text-right' : 'text-left'}`}>
                                    <div className="font-medium text-gray-900">{product.name}</div>
                                    <div className="text-xs text-gray-500">
                                        {t('commerceHub.variantCount', {
                                            count: product.variants.length,
                                            formattedCount: formatNumber(product.variants.length),
                                        })}
                                        {product.variants.some(v => v.listing?.isPublished) && (
                                            <span
                                                className="ml-2 inline-flex items-center gap-1 px-2 py-0.5 rounded-full text-xs font-medium bg-green-100 text-green-700"
                                            >
                                                <span aria-hidden="true">●</span>
                                                <span>{t('commerceHub.publishedBadge')}</span>
                                            </span>
                                        )}
                                    </div>
                                </div>
                                
                                <svg
                                    className={`w-5 h-5 text-gray-400 transition-transform ${expanded[product.productId] ? 'rotate-180' : ''}`}
                                    fill="none" stroke="currentColor" viewBox="0 0 24 24"
                                >
                                    <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M19 9l-7 7-7-7" />
                                </svg>
                            </button>

                            {/* Expanded Variant Rows */}
                            {expanded[product.productId] && (
                                <div className="border-t border-gray-100">
                                    {product.variants.map(variant => {
                                        const state = getEditState(variant.variantId)
                                        const isPublished = variant.listing?.isPublished
                                        const isSaving = saving[variant.variantId]
                                        const selectedCategoryName = getCategoryName(state.categoryId)
                                        const isCurrencyBlocked = variant.isCommerceHubCurrencyEligible !== true
                                        const currencyPolicyMessage = isCurrencyBlocked
                                            ? getCommerceHubCurrencyPolicyMessage(variant, t('commerceHub.publishing.currencyBlocked', { currency: variant?.productCurrency || t('common.unavailable') }))
                                            : null
                                        const isPublicationBlocked = variant.isCommerceHubPublicationEligible === false
                                        const publicationEligibilityMessage = isPublicationBlocked
                                            ? variant.commerceHubPublicationEligibilityMessage || t('commerceHub.publicationBlocked')
                                            : null

                                        return (
                                            <div key={variant.variantId}
                                                className={`px-5 py-4 border-b border-gray-50 last:border-b-0 ${
                                                    isPublished ? 'bg-green-50/30' : ''
                                                }`}
                                            >
                                                <div className="grid gap-4 lg:grid-cols-[0.7fr_1.3fr]">
                                                    <section className="rounded-xl border border-slate-200 bg-white p-4">
                                                        <h3 className="text-sm font-bold text-slate-950">{t('commerceHub.form.productIdentity')}</h3>
                                                        <div className="flex items-center gap-2">
                                                            <span className="text-sm font-mono text-gray-600">{variant.sku}</span>
                                                            {isPublished && (
                                                                <span className="w-2 h-2 rounded-full bg-green-500"></span>
                                                            )}
                                                        </div>
                                                        {variant.attributeSummary && (
                                                            <div className="text-xs text-gray-500 mt-0.5">{variant.attributeSummary}</div>
                                                        )}
                                                        <div className="text-sm font-medium text-gray-900 mt-1">
                                                            {formatProductCurrencyPrice(formatCurrency, variant.price || 0, variant.productCurrency, t('commerceHub.publishing.currencyUnavailable'))}
                                                        </div>
                                                        {isCurrencyBlocked && (
                                                            <div className="mt-2 text-xs text-red-700 bg-red-50 border border-red-100 rounded-lg px-2 py-1">
                                                                {currencyPolicyMessage}
                                                            </div>
                                                        )}
                                                        {isPublicationBlocked && (
                                                            <div className="mt-2 text-xs text-amber-800 bg-amber-50 border border-amber-100 rounded-lg px-2 py-1">
                                                                {publicationEligibilityMessage}
                                                            </div>
                                                        )}
                                                    </section>

                                                    <section className="rounded-xl border border-slate-200 bg-white p-4">
                                                        <h3 className="mb-3 text-sm font-bold text-slate-950">{t('commerceHub.form.category')}</h3>
                                                        {renderCategorySelector(variant.variantId)}
                                                    </section>
                                                </div>

                                                {state.categoryId && (
                                                    <section className="mt-4 rounded-xl border border-slate-200 bg-white p-4">
                                                        <h3 className="text-sm font-bold text-slate-950">{t('commerceHub.form.attributes')}</h3>
                                                        <p className="mt-1 text-xs text-slate-500">{t('commerceHub.form.attributesHelp')}</p>
                                                        {renderAttributeForm(variant.variantId, state.categoryId)}
                                                    </section>
                                                )}

                                                <section className="mt-4 rounded-xl border border-slate-200 bg-white p-4">
                                                    <h3 className="text-sm font-bold text-slate-950">{t('commerceHub.form.pricing')}</h3>
                                                    <div className="mt-3 grid gap-3 sm:grid-cols-2">
                                                        <label className={`rounded-xl border p-3 ${state.pricingMode === 'source' ? 'border-indigo-400 bg-indigo-50' : 'border-slate-200'}`}><span className="flex items-start gap-2"><input type="radio" name={`pricing-${variant.variantId}`} checked={state.pricingMode === 'source'} onChange={() => updateEditState(variant.variantId, { pricingMode: 'source' })} /><span><span className="block text-sm font-semibold">{t('commerceHub.pricing.sourceMode')}</span><span className="block text-xs text-slate-500">{t('commerceHub.pricing.sourceHelp')}</span></span></span></label>
                                                        <label className={`rounded-xl border p-3 ${state.pricingMode === 'custom' ? 'border-indigo-400 bg-indigo-50' : 'border-slate-200'}`}><span className="flex items-start gap-2"><input type="radio" name={`pricing-${variant.variantId}`} checked={state.pricingMode === 'custom'} onChange={() => updateEditState(variant.variantId, { pricingMode: 'custom' })} /><span><span className="block text-sm font-semibold">{t('commerceHub.pricing.customMode')}</span><span className="block text-xs text-slate-500">{t('commerceHub.pricing.customHelp')}</span></span></span></label>
                                                    </div>
                                                    {state.pricingMode === 'custom' && <div className="mt-3 max-w-xs"><label className="mb-1 block text-xs font-semibold text-slate-600">{t('commerceHub.pricing.customPrice')}</label><div className="relative"><input type="number" inputMode="decimal" step="0.01" min="0.01" value={state.commerceHubPrice ?? ''} onChange={e => updateEditState(variant.variantId, { commerceHubPrice: e.target.value ? Number(e.target.value) : null })} className="w-full rounded-lg border border-slate-300 px-3 py-2 text-sm" /><span className={`pointer-events-none absolute inset-y-0 flex items-center text-xs font-bold text-slate-500 ${isRTL ? 'left-3' : 'right-3'}`}>EGP</span></div></div>}
                                                </section>

                                                <div className="mt-4">
                                                    <CommissionPreviewPanel
                                                        vendorId={currentVendorId}
                                                        variantId={variant.variantId}
                                                        parentCategoryId={state.parentCategoryId || null}
                                                        categoryId={state.categoryId || null}
                                                        categoryName={selectedCategoryName || null}
                                                        pricingMode={state.pricingMode}
                                                        marketplacePrice={state.pricingMode === 'custom' ? state.commerceHubPrice : null}
                                                        onApplySuggestedPrice={(suggestedPrice) => updateEditState(variant.variantId, { pricingMode: 'custom', commerceHubPrice: suggestedPrice })}
                                                        onResolutionChange={(resolution) => handlePreviewResolution(variant.variantId, resolution)}
                                                    />
                                                </div>

                                                <section className="mt-4 flex flex-wrap items-center justify-between gap-3 rounded-xl border border-slate-200 bg-slate-50 p-4">
                                                    <div><h3 className="text-sm font-bold text-slate-950">{t('commerceHub.form.review')}</h3><p className="mt-1 text-xs text-slate-500">{previewState[variant.variantId]?.ready ? t('commerceHub.form.ready') : t('commerceHub.form.notReady')}</p></div>
                                                    <div className="flex items-center gap-2">
                                                        <button
                                                            onClick={() => handlePublish(variant)}
                                                            disabled={isSaving || !state.categoryId || !previewState[variant.variantId]?.ready || isCurrencyBlocked || isPublicationBlocked || !canManageCatalog}
                                                            title={currencyPolicyMessage || publicationEligibilityMessage || undefined}
                                                            className={`px-4 py-2 text-sm font-medium rounded-lg transition-colors disabled:opacity-50 disabled:cursor-not-allowed ${
                                                                isPublished
                                                                    ? 'bg-blue-600 hover:bg-blue-700 text-white'
                                                                    : 'bg-green-600 hover:bg-green-700 text-white'
                                                            }`}
                                                        >
                                                            <span className="inline-flex items-center gap-1">
                                                                <span aria-hidden="true">{isPublished ? '↻' : '✦'}</span>
                                                                <span>
                                                                    {isSaving ? t('common.saving') : isPublished ? t('commerceHub.update') : t('commerceHub.publish')}
                                                                </span>
                                                            </span>
                                                        </button>
                                                        {isPublished && (
                                                            <button
                                                                onClick={() => handleUnpublish(variant)}
                                                                disabled={isSaving || !canManageCatalog}
                                                                title={t('commerceHub.unpublish')}
                                                                className="px-3 py-2 text-sm font-medium text-red-600 bg-red-50 border border-red-200 rounded-lg hover:bg-red-100 transition-colors disabled:opacity-50"
                                                            >
                                                                <span className="inline-flex items-center gap-1">
                                                                    <span aria-hidden="true">✕</span>
                                                                    <span>{t('commerceHub.unpublish')}</span>
                                                                </span>
                                                            </button>
                                                        )}
                                                    </div>
                                                </section>
                                            </div>
                                        )
                                    })}
                                </div>
                            )}
                        </div>
                    ))}
                </div>
            )}
            {products.length > 0 && filteredProducts.length === 0 && <div className="rounded-2xl border border-dashed border-slate-300 bg-white p-10 text-center text-sm text-slate-500">{t('commerceHub.publishing.noCatalogMatches')}</div>}
        </div>
    )
}
