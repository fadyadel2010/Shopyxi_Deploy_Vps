import { useEffect, useRef, useState, useCallback } from 'react'
import { useTranslation } from 'react-i18next'
import { useToast } from '../components/Toast'
import { useConfirmDialog } from '../common/confirm/ConfirmDialogProvider'
import api from '../services/api'
import { useRTL } from '../hooks/useRTL'
import { formatDate } from '../utils/dateFormat'
import { toSafeNumber } from '../utils/financialMath'
import AsyncEntitySelect from '../components/AsyncEntitySelect'
import { invalidatePosRuntimeCache } from '../services/posRuntimeCache'
import { useHasPermission } from '../hooks/usePosSessionPermission'
import { buildIdempotencyHeaders, stableStringify } from '../utils/requestSafety'

const StockTransfers = () => {
    const { t } = useTranslation()
    const { dir, isRTL, locale } = useRTL()
    const { confirm, prompt } = useConfirmDialog()
    const { showSuccess, showError } = useToast()
    const canApproveTransfers = useHasPermission('inventory.approve')
    const [activeTab, setActiveTab] = useState('list')
    const [transfers, setTransfers] = useState([])
    const [inspectedId, setInspectedId] = useState(null)
    const [detail, setDetail] = useState(null)
    const [history, setHistory] = useState([])
    const [detailRevision, setDetailRevision] = useState(0)
    const [warehouses, setWarehouses] = useState([])
    const [listLoading, setListLoading] = useState(true)
    const [isSubmitting, setIsSubmitting] = useState(false)
    const [pendingActions, setPendingActions] = useState(() => new Set())
    const [statusFilter, setStatusFilter] = useState('')
    const [formData, setFormData] = useState({
        fromWarehouseId: '',
        toWarehouseId: '',
        notes: '',
        items: [],
    })

    const tableTextAlignClass = isRTL ? 'text-right' : 'text-left'
    const actionAlignClass = isRTL ? 'text-left' : 'text-right'
    const hasStaleTransferRows = listLoading && transfers.length > 0
    const warehousesRequestIdRef = useRef(0)
    const transfersRequestIdRef = useRef(0)
    const pendingActionsRef = useRef(new Set())
    const isSubmittingRef = useRef(false)
    const createAttemptRef = useRef(null)
    const cloneTransferItem = (item) => ({ ...item })
    const parseQuantityInput = (value) => {
        if (typeof value === 'string' && !value.trim()) {
            return ''
        }

        const parsedValue = toSafeNumber(value, Number.NaN)
        return Number.isFinite(parsedValue) ? parsedValue : ''
    }

    // PERFORMANCE: Async product loader instead of loading all with pageSize=1000
    const loadProductsAsync = useCallback(async ({ search, page, pageSize = 20 }) => {
        const params = new URLSearchParams({ page: page.toString(), pageSize: pageSize.toString() })
        if (search) params.set('search', search)
        const response = await api.get(`/Products?${params}`)
        return { items: response.data.items || response.data || [], hasMore: page < (response.data.totalPages || 1) }
    }, [])

    useEffect(() => {
        void loadWarehouses()
    }, [])

    useEffect(() => {
        void loadTransfers()
    }, [statusFilter])

    useEffect(() => {
        const controller = new AbortController()
        setDetail(null); setHistory([])
        if (inspectedId) Promise.all([
            api.get(`/StockTransfers/${inspectedId}`, { signal: controller.signal }),
            api.get(`/StockTransfers/${inspectedId}/history`, { signal: controller.signal }),
        ]).then(([d, h]) => { if (!controller.signal.aborted) { setDetail(d.data); setHistory(h.data) } })
            .catch(error => { if (!controller.signal.aborted) showError(error.response?.data?.message || t('stockTransfers.failedToLoad')) })
        return () => controller.abort()
    }, [inspectedId, detailRevision, showError, t])

    useEffect(() => {
        return () => {
            warehousesRequestIdRef.current += 1
            transfersRequestIdRef.current += 1
        }
    }, [])

    const loadWarehouses = async () => {
        const requestId = ++warehousesRequestIdRef.current
        try {
            const response = await api.get('/Warehouses')

            if (requestId !== warehousesRequestIdRef.current) {
                return
            }

            setWarehouses(response.data.items || response.data)
        } catch {
            if (requestId !== warehousesRequestIdRef.current) {
                return
            }

            showError(t('stockTransfers.failedToLoadWarehouses'))
        }
    }

    const loadTransfers = async () => {
        const requestId = ++transfersRequestIdRef.current
        setListLoading(true)
        try {
            const params = statusFilter ? `?status=${statusFilter}` : ''
            const response = await api.get(`/StockTransfers${params}`)

            if (requestId !== transfersRequestIdRef.current) {
                return
            }

            setTransfers(response.data.items || [])
        } catch {
            if (requestId !== transfersRequestIdRef.current) {
                return
            }

            showError(t('stockTransfers.failedToLoad'))
        } finally {
            if (requestId === transfersRequestIdRef.current) {
                setListLoading(false)
            }
        }
    }

    const addItem = () => {
        setFormData((prev) => ({
            ...prev,
            items: [...prev.items, cloneTransferItem({ productId: '', quantity: 1, notes: '' })],
        }))
    }

    const removeItem = (index) => {
        setFormData((prev) => ({
            ...prev,
            items: prev.items.filter((_, itemIndex) => itemIndex !== index),
        }))
    }

    const updateItem = (index, field, value, productName = '') => {
        if (field === 'productId' && value) {
            const isDuplicate = formData.items.some((item, itemIndex) => itemIndex !== index && item.productId === value)
            if (isDuplicate) {
                showError(t('stockTransfers.productAlreadyAdded'))
                return
            }
        }

        setFormData((prev) => {
            const nextItems = prev.items.map((item, itemIndex) => {
                const nextItem = cloneTransferItem(item)

                if (itemIndex !== index) {
                    return nextItem
                }

                return {
                    ...nextItem,
                    [field]: value,
                    ...(field === 'productId' ? { productName } : {}),
                }
            })

            return {
                ...prev,
                items: nextItems,
            }
        })
    }

    const handleSubmit = async (e) => {
        e.preventDefault()

        if (isSubmittingRef.current) {
            return
        }

        if (formData.fromWarehouseId === formData.toWarehouseId) {
            showError(t('stockTransfers.sameWarehouseError'))
            return
        }

        if (formData.items.some(item => !item.productId)) {
            showError(t('stockTransfers.itemsMissingProduct'))
            return
        }

        isSubmittingRef.current = true
        setIsSubmitting(true)
        try {
            const fingerprint = stableStringify(formData)
            const key = createAttemptRef.current?.fingerprint === fingerprint
                ? createAttemptRef.current.key : crypto.randomUUID()
            createAttemptRef.current = { key, fingerprint }
            await api.post('/StockTransfers', formData, { headers: buildIdempotencyHeaders(key) })
            createAttemptRef.current = null
            invalidatePosRuntimeCache({ reason: 'stock-transferred' })
            showSuccess(t('stockTransfers.transferCreated'))
            setFormData({ fromWarehouseId: '', toWarehouseId: '', notes: '', items: [] })
            setActiveTab('list')
            await loadTransfers()
        } catch (error) {
            showError(error.response?.data?.message || t('stockTransfers.failedToCreate'))
        } finally {
            isSubmittingRef.current = false
            setIsSubmitting(false)
        }
    }

    const beginPendingAction = (id) => {
        if (pendingActionsRef.current.has(id)) {
            return false
        }

        const nextPendingActions = new Set(pendingActionsRef.current)
        nextPendingActions.add(id)
        pendingActionsRef.current = nextPendingActions
        setPendingActions(nextPendingActions)
        return true
    }

    const endPendingAction = (id) => {
        if (!pendingActionsRef.current.has(id)) {
            return
        }

        const nextPendingActions = new Set(pendingActionsRef.current)
        nextPendingActions.delete(id)
        pendingActionsRef.current = nextPendingActions
        setPendingActions(nextPendingActions)
    }

    const handleApprove = async (id) => {
        if (listLoading || pendingActionsRef.current.has(id)) {
            return
        }

        const confirmed = await confirm({
            confirmLabel: t('stockTransfers.approve'),
            description: t('stockTransfers.confirmApprove'),
            title: t('common.confirm'),
            variant: 'warning',
        })

        if (!confirmed || !beginPendingAction(id)) return

        try {
            await api.post(`/StockTransfers/${id}/approve`, { notes: '' })
            invalidatePosRuntimeCache({ reason: 'stock-transferred' })
            showSuccess(t('stockTransfers.transferApproved'))
            await loadTransfers()
        } catch (error) {
            showError(error.response?.data?.message || t('stockTransfers.failedToApprove'))
        } finally {
            endPendingAction(id)
        }
    }

    const handleReject = async (id) => {
        if (listLoading || pendingActionsRef.current.has(id)) {
            return
        }

        const notes = await prompt({
            confirmLabel: t('stockTransfers.reject'),
            description: t('stockTransfers.reasonForRejection'),
            inputLabel: t('stockTransfers.reasonForRejection'),
            minLength: 1,
            placeholder: t('stockTransfers.reasonForRejection'),
            title: t('common.warning'),
            trim: true,
            variant: 'warning',
        })

        if (!notes || !beginPendingAction(id)) return

        try {
            await api.post(`/StockTransfers/${id}/reject`, { notes })
            invalidatePosRuntimeCache({ reason: 'stock-transferred' })
            showSuccess(t('stockTransfers.transferRejected'))
            await loadTransfers()
        } catch (error) {
            showError(error.response?.data?.message || t('stockTransfers.failedToReject'))
        } finally {
            endPendingAction(id)
        }
    }

    const handleComplete = async (id) => {
        if (listLoading || pendingActionsRef.current.has(id)) {
            return
        }

        const confirmed = await confirm({
            confirmLabel: t('stockTransfers.complete'),
            description: t('stockTransfers.confirmComplete'),
            title: t('common.confirm'),
            variant: 'warning',
        })

        if (!confirmed || !beginPendingAction(id)) return

        try {
            await api.post(`/StockTransfers/${id}/complete`)
            invalidatePosRuntimeCache({ reason: 'stock-transferred' })
            showSuccess(t('stockTransfers.transferCompleted'))
            await loadTransfers()
        } catch (error) {
            showError(error.response?.data?.message || t('stockTransfers.failedToComplete'))
        } finally {
            endPendingAction(id)
        }
    }

    const handleWorkflow = async (transfer, action) => {
        if (listLoading || pendingActionsRef.current.has(transfer.id)) return
        const reason = await prompt({
            title: t(`transferWorkflow.${action}`), description: t(`transferWorkflow.${action}Policy`),
            inputLabel: t('stockCounts.reason'), minLength: 1, maxLength: 1000, trim: true,
            confirmLabel: t('common.confirm'), variant: 'warning',
        })
        if (!reason || !beginPendingAction(transfer.id)) return
        try {
            await api.post(`/StockTransfers/${transfer.id}/${action}`, { version: transfer.workflowVersion, reason })
            invalidatePosRuntimeCache({ reason: `stock-transfer-${action}` })
            showSuccess(t('transferWorkflow.saved'))
        } catch (error) {
            showError(error.response?.data?.message || t('transferWorkflow.verifyBeforeRetry'))
        } finally {
            await loadTransfers()
            setDetailRevision(v => v + 1)
            endPendingAction(transfer.id)
        }
    }

    const getStatusColor = (status) => {
        const colors = {
            Pending: 'bg-yellow-100 text-yellow-800',
            Approved: 'bg-blue-100 text-blue-800',
            Rejected: 'bg-red-100 text-red-800',
            Completed: 'bg-green-100 text-green-800',
            Cancelled: 'bg-gray-100 text-gray-800',
            Dispatched: 'bg-purple-100 text-purple-800',
            Reversed: 'bg-orange-100 text-orange-800',
        }

        return colors[status] || 'bg-gray-100 text-gray-800'
    }

    return (
        <div dir={dir} className="space-y-6">
            <p className="rounded bg-amber-50 p-3">{t('transferWorkflow.policy')}</p>
            <div className="flex items-center justify-between">
                <h1 className="text-2xl font-bold text-gray-900">{t('stockTransfers.title')}</h1>
                <button
                    onClick={() => setActiveTab(activeTab === 'list' ? 'create' : 'list')}
                    className="rounded-lg bg-blue-600 px-4 py-2 text-white hover:bg-blue-700"
                >
                    {activeTab === 'list' ? t('stockTransfers.newTransfer') : t('stockTransfers.backToList')}
                </button>
            </div>

            {activeTab === 'list' ? (
                <div className="rounded-lg bg-white shadow">
                    {listLoading && transfers.length > 0 && (
                        <div className="border-b border-gray-200 bg-gray-50 px-4 py-2 text-xs text-gray-500">
                            {t('common.loading')}
                        </div>
                    )}
                    <div className="border-b p-4">
                        <select
                            value={statusFilter}
                            onChange={(e) => setStatusFilter(e.target.value)}
                            className="rounded-lg border border-gray-300 px-3 py-2"
                        >
                            <option value="">{t('stockTransfers.allStatus')}</option>
                            <option value="0">{t('stockTransfers.pending')}</option>
                            <option value="1">{t('stockTransfers.approved')}</option>
                            <option value="2">{t('stockTransfers.rejected')}</option>
                            <option value="3">{t('stockTransfers.completed')}</option>
                            <option value="4">{t('transferWorkflow.cancelled')}</option>
                            <option value="5">{t('transferWorkflow.dispatched')}</option>
                            <option value="6">{t('transferWorkflow.reversed')}</option>
                        </select>
                    </div>

                    <div className="overflow-x-auto">
                        <table className="min-w-full divide-y divide-gray-200">
                            <thead className="bg-gray-50">
                                <tr>
                                    <th className={`px-6 py-3 text-xs font-medium uppercase text-gray-500 ${tableTextAlignClass}`}>{t('stockTransfers.transferNumber')}</th>
                                    <th className={`px-6 py-3 text-xs font-medium uppercase text-gray-500 ${tableTextAlignClass}`}>{t('stockTransfers.date')}</th>
                                    <th className={`px-6 py-3 text-xs font-medium uppercase text-gray-500 ${tableTextAlignClass}`}>{t('stockTransfers.from')}</th>
                                    <th className={`px-6 py-3 text-xs font-medium uppercase text-gray-500 ${tableTextAlignClass}`}>{t('stockTransfers.to')}</th>
                                    <th className={`px-6 py-3 text-xs font-medium uppercase text-gray-500 ${tableTextAlignClass}`}>{t('stockTransfers.items')}</th>
                                    <th className={`px-6 py-3 text-xs font-medium uppercase text-gray-500 ${tableTextAlignClass}`}>{t('stockTransfers.status')}</th>
                                    <th className={`px-6 py-3 text-xs font-medium uppercase text-gray-500 ${actionAlignClass}`}>{t('stockTransfers.actions')}</th>
                                </tr>
                            </thead>
                            <tbody className="divide-y divide-gray-200 bg-white">
                                {listLoading && transfers.length === 0 ? (
                                    <tr>
                                        <td colSpan="7" className="px-6 py-8 text-center text-gray-500">
                                            {t('common.loading')}
                                        </td>
                                    </tr>
                                ) : null}
                                {transfers.map((transfer) => {
                                    const rowActionsDisabled = hasStaleTransferRows || pendingActions.has(transfer.id)

                                    return (
                                        <tr key={transfer.id} className="hover:bg-gray-50">
                                            <td className={`whitespace-nowrap px-6 py-4 font-medium ${tableTextAlignClass}`}><button className="text-blue-700 underline" onClick={() => setInspectedId(transfer.id)}>{transfer.transferNumber}</button></td>
                                            <td className={`whitespace-nowrap px-6 py-4 text-sm ${tableTextAlignClass}`}>
                                                {formatDate(transfer.transferDate, locale)}
                                            </td>
                                            <td className={`whitespace-nowrap px-6 py-4 text-sm ${tableTextAlignClass}`}>{transfer.fromWarehouse?.name}</td>
                                            <td className={`whitespace-nowrap px-6 py-4 text-sm ${tableTextAlignClass}`}>{transfer.toWarehouse?.name}</td>
                                            <td className={`whitespace-nowrap px-6 py-4 text-sm ${tableTextAlignClass}`}>{transfer.items?.length || 0}</td>
                                            <td className={`whitespace-nowrap px-6 py-4 ${tableTextAlignClass}`}>
                                                <span className={`rounded-full px-2 py-1 text-xs font-medium ${getStatusColor(transfer.status)}`}>
                                                    {t(`transferWorkflow.${transfer.status.toLowerCase()}`)}
                                                </span>
                                            </td>
                                            <td className={`whitespace-nowrap px-6 py-4 text-sm ${actionAlignClass}`}>
                                                <div className={`flex items-center gap-2 ${isRTL ? 'justify-start' : 'justify-end'} ${rowActionsDisabled ? 'pointer-events-none opacity-50' : ''}`}>
                                                    {canApproveTransfers && transfer.status === 'Pending' && (
                                                        <>
                                                            <button
                                                                type="button"
                                                                disabled={rowActionsDisabled}
                                                                onClick={() => handleApprove(transfer.id)}
                                                                className="text-green-600 hover:text-green-800 disabled:cursor-not-allowed disabled:text-green-400"
                                                            >
                                                                {t('stockTransfers.approve')}
                                                            </button>
                                                            <button
                                                                type="button"
                                                                disabled={rowActionsDisabled}
                                                                onClick={() => handleReject(transfer.id)}
                                                                className="text-red-600 hover:text-red-800 disabled:cursor-not-allowed disabled:text-red-400"
                                                            >
                                                                {t('stockTransfers.reject')}
                                                            </button>
                                                        </>
                                                    )}
                                                    {transfer.status === 'Approved' && <button type="button" disabled={rowActionsDisabled} onClick={() => handleWorkflow(transfer, 'dispatch')} className="text-purple-700">{t('transferWorkflow.dispatch')}</button>}
                                                    {transfer.status === 'Dispatched' && <button type="button" disabled={rowActionsDisabled} onClick={() => handleWorkflow(transfer, 'receive')} className="text-green-700">{t('transferWorkflow.receive')}</button>}
                                                    {['Pending', 'Approved', 'Dispatched'].includes(transfer.status) && <button type="button" disabled={rowActionsDisabled} onClick={() => handleWorkflow(transfer, 'cancel')} className="text-red-700">{t('transferWorkflow.cancel')}</button>}
                                                    {canApproveTransfers && transfer.status === 'Completed' && <button type="button" disabled={rowActionsDisabled} onClick={() => handleWorkflow(transfer, 'reverse')} className="text-orange-700">{t('transferWorkflow.reverse')}</button>}
                                                    {transfer.status === 'Dispatched' && <span>{t('transferWorkflow.inTransit')}: {transfer.inTransitQuantity}</span>}
                                                    {transfer.status === 'Approved' && (
                                                        <button
                                                            type="button"
                                                            disabled={rowActionsDisabled}
                                                            onClick={() => handleComplete(transfer.id)}
                                                            className="text-blue-600 hover:text-blue-800 disabled:cursor-not-allowed disabled:text-blue-400"
                                                        >
                                                                {t('transferWorkflow.immediateComplete')}
                                                        </button>
                                                    )}
                                                </div>
                                            </td>
                                        </tr>
                                    )
                                })}
                            </tbody>
                        </table>
                    </div>

                    {transfers.length === 0 && !listLoading && (
                        <div className="py-12 text-center text-gray-500">
                            {t('stockTransfers.noTransfers')}
                        </div>
                    )}
                </div>
            ) : (
                <div className="rounded-lg bg-white p-6 shadow">
                    <h2 className="mb-6 text-xl font-semibold">{t('stockTransfers.createTitle')}</h2>

                    <form onSubmit={handleSubmit} className="space-y-6">
                        <div className="grid grid-cols-1 gap-4 md:grid-cols-2">
                            <div>
                                <label className="mb-2 block text-sm font-medium text-gray-700">
                                    {t('stockTransfers.fromWarehouse')} *
                                </label>
                                <select
                                    required
                                    value={formData.fromWarehouseId}
                                    onChange={(e) => setFormData((prev) => ({ ...prev, fromWarehouseId: e.target.value }))}
                                    className="w-full rounded-lg border border-gray-300 px-3 py-2"
                                >
                                    <option value="">{t('stockTransfers.selectWarehouse')}</option>
                                    {warehouses.map((warehouse) => (
                                        <option key={warehouse.id} value={warehouse.id}>{warehouse.name}</option>
                                    ))}
                                </select>
                            </div>

                            <div>
                                <label className="mb-2 block text-sm font-medium text-gray-700">
                                    {t('stockTransfers.toWarehouse')} *
                                </label>
                                <select
                                    required
                                    value={formData.toWarehouseId}
                                    onChange={(e) => setFormData((prev) => ({ ...prev, toWarehouseId: e.target.value }))}
                                    className="w-full rounded-lg border border-gray-300 px-3 py-2"
                                >
                                    <option value="">{t('stockTransfers.selectWarehouse')}</option>
                                    {warehouses.map((warehouse) => (
                                        <option key={warehouse.id} value={warehouse.id}>{warehouse.name}</option>
                                    ))}
                                </select>
                            </div>
                        </div>

                        <div>
                            <label className="mb-2 block text-sm font-medium text-gray-700">{t('stockTransfers.notes')}</label>
                            <textarea
                                value={formData.notes}
                                onChange={(e) => setFormData((prev) => ({ ...prev, notes: e.target.value }))}
                                rows={2}
                                className="w-full rounded-lg border border-gray-300 px-3 py-2"
                            />
                        </div>

                        <div>
                            <div className="mb-4 flex items-center justify-between">
                                <h3 className="text-lg font-medium">{t('stockTransfers.items')}</h3>
                                <button
                                    type="button"
                                    onClick={addItem}
                                    className="rounded-lg bg-green-600 px-4 py-2 text-white hover:bg-green-700"
                                >
                                    {t('stockTransfers.addItem')}
                                </button>
                            </div>

                            {formData.items.map((item, index) => (
                                <div key={`${item.productId}-${index}`} className="mb-4 flex items-start gap-4">
                                    <div className="flex-1">
                                        <AsyncEntitySelect
                                            value={item.productId ? { id: item.productId, name: item.productName || '' } : null}
                                            onChange={(product) => updateItem(index, 'productId', product?.id || '', product?.name || '')}
                                            loadOptions={loadProductsAsync}
                                            placeholder={t('stockTransfers.selectProduct')}
                                            className="w-full"
                                        />
                                    </div>
                                    <div className="w-32">
                                        <input
                                            type="number"
                                            required
                                            min="1"
                                            value={item.quantity}
                                            onChange={(e) => updateItem(index, 'quantity', parseQuantityInput(e.target.value))}
                                            className="w-full rounded-lg border border-gray-300 px-3 py-2"
                                            placeholder={t('stockTransfers.qty')}
                                        />
                                    </div>
                                    <button
                                        type="button"
                                        onClick={() => removeItem(index)}
                                        className="rounded-lg bg-red-600 px-3 py-2 text-white hover:bg-red-700"
                                    >
                                        {t('common.delete')}
                                    </button>
                                </div>
                            ))}

                            {formData.items.length === 0 && (
                                <p className="py-4 text-center text-gray-500">{t('stockTransfers.noItems')}</p>
                            )}
                        </div>

                        <div className="flex gap-4">
                            <button
                                type="submit"
                                disabled={isSubmittingRef.current || isSubmitting || formData.items.length === 0}
                                className="flex-1 rounded-lg bg-blue-600 px-4 py-2 text-white hover:bg-blue-700 disabled:bg-gray-400"
                            >
                                {isSubmittingRef.current || isSubmitting ? t('stockTransfers.creating') : t('stockTransfers.createRequest')}
                            </button>
                        </div>
                    </form>
                </div>
            )}
            {detail && <section className="rounded border bg-white p-4 space-y-3" aria-label={t('transferWorkflow.details')}>
                <div className="flex justify-between gap-3"><h2 className="text-xl font-bold">{detail.transferNumber}</h2><button type="button" onClick={() => setInspectedId(null)}>{t('stockCounts.dismiss')}</button></div>
                <p>{detail.fromWarehouse?.name} → {detail.toWarehouse?.name} · {t(`transferWorkflow.${detail.status.toLowerCase()}`, { defaultValue: detail.status })}</p>
                <p>{t('transferWorkflow.inTransit')}: {detail.inTransitQuantity} · {t('transferWorkflow.version')}: {detail.workflowVersion}</p>
                <p>{t('transferWorkflow.inTransitValue')}: {detail.inTransitValue}</p>
                <p>{t('transferWorkflow.dispatch')}: {detail.dispatchedById} · {detail.dispatchedAt}</p>
                <p>{t('transferWorkflow.receive')}: {detail.completedBy?.username} · {detail.completedAt}</p>
                <p>{t('transferWorkflow.reverse')}: {detail.reversedById} · {detail.reversedAt} · {detail.reversalReason}</p>
                {detail.cancellationReason && <p>{t('transferWorkflow.cancel')}: {detail.cancellationReason}</p>}
                <ul>{detail.items.map(item => <li key={item.id}>{item.productName} · {item.productVariant?.sku} · {item.quantity}</li>)}</ul>
                <h3 className="font-bold">{t('transferWorkflow.history')}</h3>
                {history.length === 0 && <p>{t('transferWorkflow.legacyHistory')}</p>}
                <ol>{history.map(h => <li key={h.id} className="border-b py-2">{t(`transferWorkflow.${h.action}`)} · {h.createdBy} · {h.createdAt}<p>{h.reason}</p><pre className="text-xs whitespace-pre-wrap break-all" dir="ltr">{h.detailsJson}</pre></li>)}</ol>
            </section>}
        </div>
    )
}

export default StockTransfers
