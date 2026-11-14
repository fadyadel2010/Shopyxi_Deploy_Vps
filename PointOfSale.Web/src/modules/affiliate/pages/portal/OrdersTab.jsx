/**
 * AF-15 + AF-21: Orders Tab — Commission orders table with expandable details + Order Lifecycle Status
 */
import { useState, useEffect, useCallback, useRef, Fragment } from 'react'
import affiliateApi from '../../../../services/affiliateApi'
import { ShoppingBag, ChevronDown, Filter, Package, CreditCard, DollarSign, Truck } from 'lucide-react'
import { useLanguage } from '../../i18n/LanguageContext'
import { CommissionStatus } from './PortalUi'
import CanonicalOrderDetails from './CanonicalOrderDetails'
import { SourceOrder } from './CommissionActivity'

// AF-21: Order lifecycle status styles
const ORDER_STATUS_STYLES = {
    Pending: 'bg-yellow-500/10 text-yellow-800 dark:text-yellow-300 border-yellow-500/20',
    Confirmed: 'bg-blue-500/10 text-blue-700 dark:text-blue-300 border-blue-500/20',
    Processing: 'bg-indigo-500/10 text-indigo-700 dark:text-indigo-300 border-indigo-500/20',
    Completed: 'bg-green-500/10 text-green-700 dark:text-green-300 border-green-500/20',
    Cancelled: 'bg-red-500/10 text-red-700 dark:text-red-300 border-red-500/20',
    Refunded: 'bg-orange-500/10 text-orange-800 dark:text-orange-300 border-orange-500/20',
}

const FULFILLMENT_STATUS_STYLES = {
    Created: 'bg-gray-500/10 text-gray-600 dark:text-gray-400 border-gray-500/20',
    Confirmed: 'bg-blue-500/10 text-blue-700 dark:text-blue-300 border-blue-500/20',
    Packed: 'bg-indigo-500/10 text-indigo-700 dark:text-indigo-300 border-indigo-500/20',
    OutForDelivery: 'bg-purple-500/10 text-purple-700 dark:text-purple-300 border-purple-500/20',
    Delivered: 'bg-emerald-500/10 text-emerald-700 dark:text-emerald-300 border-emerald-500/20',
    Completed: 'bg-green-500/10 text-green-700 dark:text-green-300 border-green-500/20',
    Cancelled: 'bg-red-500/10 text-red-700 dark:text-red-300 border-red-500/20',
    Returned: 'bg-orange-500/10 text-orange-800 dark:text-orange-300 border-orange-500/20',
    FailedDelivery: 'bg-red-500/10 text-red-700 dark:text-red-300 border-red-500/20',
}

function StatusBadge({ status }) {
    return <CommissionStatus status={status} />
}

// AF-21: Order lifecycle status badge
function OrderStatusBadge({ status, t }) {
    if (!status) return null
    const translationKey = `portal.ordersTab.orderStatus${status}`
    let label = t(translationKey)
    if (label === translationKey) {
        label = status
    }

    return (
        <span className={`inline-flex items-center px-2.5 py-0.5 rounded-full text-[10px] font-medium border ${ORDER_STATUS_STYLES[status] || 'bg-white/5 text-gray-600 dark:text-gray-400 border-gray-200 dark:border-white/10'}`}>
            {label}
        </span>
    )
}

// AF-21: Fulfillment status badge
function FulfillmentStatusBadge({ status, t }) {
    if (!status) return null
    const translationKey = `portal.ordersTab.fulfillmentStatus${status}`
    let label = t(translationKey)
    if (label === translationKey) {
        label = status
    }

    return (
        <span className={`inline-flex items-center px-2.5 py-0.5 rounded-full text-[10px] font-medium border ${FULFILLMENT_STATUS_STYLES[status] || 'bg-white/5 text-gray-600 dark:text-gray-400 border-gray-200 dark:border-white/10'}`}>
            {label}
        </span>
    )
}

const FILTER_OPTIONS_DEFS = [
    { value: '', labelKey: 'portal.ordersTab.filterAll' },
    { value: 'Earned', labelKey: 'portal.ordersTab.filterEarned' },
    { value: 'Approved', labelKey: 'portal.ordersTab.filterApproved' },
    { value: 'Paid', labelKey: 'portal.ordersTab.filterPaid' },
    { value: 'Reversed', labelKey: 'portal.ordersTab.filterReversed' },
]

export default function OrdersTab({ formatCurrency, sourceOrderId }) {
    return <div className="space-y-4">{sourceOrderId && <SourceOrder orderId={sourceOrderId} formatCurrency={formatCurrency} />}<OrderList formatCurrency={formatCurrency} /></div>
}

function OrderList({ formatCurrency }) {
    const { t, formatDate, formatNumber } = useLanguage()
    const [data, setData] = useState(null)
    const [loading, setLoading] = useState(true)
    const [error, setError] = useState(false)
    const [page, setPage] = useState(1)
    const [statusFilter, setStatusFilter] = useState('')
    const [expandedOrder, setExpandedOrder] = useState(null)
    const [search, setSearch] = useState('')
    const [sort, setSort] = useState('newest')
    const [history, setHistory] = useState('all')
    const requestRef = useRef(0)
    const pageSize = 20

    const fetchOrders = useCallback(() => {
        const request = ++requestRef.current
        setLoading(true)
        setError(false)
        affiliateApi.getOrders(page, pageSize, statusFilter)
            .then(d => { if (request === requestRef.current) setData(d) })
            .catch(() => { if (request === requestRef.current) { setData(null); setError(true) } })
            .finally(() => { if (request === requestRef.current) setLoading(false) })
    }, [page, statusFilter])

    const invalidateRequests = useCallback(() => { requestRef.current++ }, [])
    useEffect(() => { fetchOrders(); return invalidateRequests }, [fetchOrders, invalidateRequests])

    if (loading) {
        return (
            <div role="status" aria-label={t('ui.loading')} className="space-y-3">
                {[...Array(5)].map((_, i) => (
                    <div key={i} className="h-14 rounded-xl border border-gray-200 dark:border-white/[0.06] bg-white dark:bg-white/[0.02] animate-pulse" />
                ))}
            </div>
        )
    }

    if (error) return <div role="alert" className="text-center py-20"><p>{t('ui.loadError')}</p><button onClick={fetchOrders}>{t('ui.retry')}</button></div>

    if (!data?.orders?.length) {
        return (
            <div className="text-center py-20">
                <ShoppingBag className="w-14 h-14 text-gray-700 mx-auto mb-4" />
                <h3 className="text-lg font-semibold text-gray-900 dark:text-white mb-2">{t('portal.ordersTab.noOrders')}</h3>
                <p className="text-gray-500 text-sm">{t('portal.ordersTab.noOrdersDesc')}</p>
                {statusFilter && <button onClick={() => { setStatusFilter(''); setPage(1) }}>{t('portal.ordersTab.filterAll')}</button>}
            </div>
        )
    }

    const filterOptions = FILTER_OPTIONS_DEFS.map(opt => ({
        ...opt,
        label: t(opt.labelKey)
    }))
    const orders = data.orders.filter(order =>
        (history === 'all' || (history === 'qa' ? order.isSyntheticQa : !order.isSyntheticQa)) &&
        `${order.orderNumber} ${(order.items || []).map(item => item.productName).join(' ')}`.toLocaleLowerCase().includes(search.toLocaleLowerCase())
    ).sort((a, b) => (sort === 'oldest' ? 1 : -1) * (new Date(a.createdAtUtc) - new Date(b.createdAtUtc)))

    return (
        <div className="space-y-4">
            <div className="grid sm:grid-cols-2 xl:grid-cols-3 gap-3">
                <input type="search" aria-label={t('ux.search')} placeholder={t('ux.search')} value={search} onChange={e => setSearch(e.target.value)} className="rounded-xl border border-gray-200 dark:border-white/10 bg-white dark:bg-white/5 px-4 py-3 text-sm min-w-0" />
                <select aria-label={t('ux.sort')} value={sort} onChange={e => setSort(e.target.value)} className="rounded-xl border border-gray-200 dark:border-white/10 bg-white dark:bg-[#151b29] px-4 py-3 text-sm"><option value="newest">{t('ux.newest')}</option><option value="oldest">{t('ux.oldest')}</option></select>
                <select aria-label={t('ux.history')} value={history} onChange={e => setHistory(e.target.value)} className="rounded-xl border border-gray-200 dark:border-white/10 bg-white dark:bg-[#151b29] px-4 py-3 text-sm"><option value="all">{t('ux.allHistory')}</option><option value="genuine">{t('ux.genuine')}</option><option value="qa">{t('ux.qa')}</option></select>
            </div>
            <p className="text-xs text-gray-500 dark:text-gray-400">{t('ux.searchScope')}</p>
            {/* Header */}
            <div className="flex items-center justify-between">
                <p className="text-sm text-gray-500">{formatNumber(data.totalCount)} {t('portal.ordersTab.ordersCount')}</p>
                <div className="relative">
                    <Filter className="absolute start-3 top-1/2 -translate-y-1/2 w-3.5 h-3.5 text-gray-500" />
                    <select
                        id="affiliate-orders-status-filter"
                        aria-label={t('portal.ordersTab.colStatus')}
                        value={statusFilter}
                        onChange={(e) => { setStatusFilter(e.target.value); setPage(1) }}
                        className="ps-9 pe-4 py-2 rounded-lg bg-white dark:bg-white/[0.03] border border-gray-200 dark:border-white/[0.08] text-sm text-gray-700 dark:text-gray-300 focus:border-[#6C63FF]/50 outline-none cursor-pointer appearance-auto"
                    >
                        {filterOptions.map(opt => (
                            <option key={opt.value} value={opt.value}>{opt.label}</option>
                        ))}
                    </select>
                </div>
            </div>

            {/* Table */}
            <div className="rounded-xl border border-gray-200 dark:border-white/[0.06] overflow-hidden">
                <div className="overflow-x-auto">
                    <table className="w-full">
                        <thead>
                            <tr className="border-b border-gray-200 dark:border-white/[0.06]">
                                <th className="px-4 py-3 text-left text-[10px] font-medium text-gray-500 uppercase tracking-widest">{t('portal.ordersTab.colOrder')}</th>
                                <th className="px-4 py-3 text-left text-[10px] font-medium text-gray-500 uppercase tracking-widest">{t('portal.ordersTab.colStatus')}</th>
                                <th className="px-4 py-3 text-right text-xs text-gray-500">{t('ux.readModel.orderTotal')}</th>
                                <th className="px-4 py-3 text-right text-[10px] font-medium text-gray-500 uppercase tracking-widest">{t('portal.ordersTab.colCommission')}</th>
                                <th className="px-4 py-3 text-left text-[10px] font-medium text-gray-500 uppercase tracking-widest">{t('portal.ordersTab.colCurrency')}</th>
                                <th className="px-4 py-3 text-left text-[10px] font-medium text-gray-500 uppercase tracking-widest">{t('portal.ordersTab.colDate')}</th>
                                <th className="px-4 py-3 w-10"></th>
                            </tr>
                        </thead>
                        <tbody className="divide-y divide-white/[0.04]">
                            {orders.map(order => (
                                <Fragment key={order.orderId}>
                                    <tr
                                        className="hover:bg-white/[0.02] cursor-pointer transition-colors"
                                        onClick={() => setExpandedOrder(expandedOrder === order.orderId ? null : order.orderId)}
                                    >
                                        <td className="px-4 py-4 text-sm font-medium text-gray-900 dark:text-white">
                                            <button aria-expanded={expandedOrder === order.orderId} aria-controls={`order-${order.orderId}`} onClick={e => { e.stopPropagation(); setExpandedOrder(expandedOrder === order.orderId ? null : order.orderId) }} className="text-[#6C63FF] text-start font-semibold">{order.orderNumber}</button>
                                            <p className="text-xs text-gray-500 mt-1 max-w-xs break-words">{(order.items || []).map(item => item.productName || (item.productId ? `${t('ux.readModel.productId')}: ${item.productId}` : t('ux.unknown'))).join(' · ') || t('ux.noItems')}</p>
                                            {order.isSyntheticQa && <p className="text-xs text-amber-700 dark:text-amber-300">{t('portal.syntheticQa.label')} — {t('portal.syntheticQa.excluded')}</p>}
                                        </td>
                                        <td className="px-4 py-3"><StatusBadge status={order.status} t={t} /></td>
                                        <td className="px-4 py-3 text-sm text-end whitespace-nowrap">{order.canonical ? formatCurrency(order.canonical.orderTotal, order.canonical.orderCurrency) : t('ux.unknown')}</td>
                                        <td className="px-4 py-3 text-sm text-right font-semibold text-gray-900 dark:text-white">{formatCurrency(order.totalCommission, order.currency)}</td>
                                        <td className="px-4 py-3 text-sm text-gray-500">{order.currency}</td>
                                        <td className="px-4 py-3 text-xs text-gray-500">{formatDate(order.createdAtUtc)}</td>
                                        <td className="px-4 py-3 text-center">
                                            <ChevronDown className={`w-4 h-4 text-gray-600 transition-transform ${expandedOrder === order.orderId ? 'rotate-180' : ''}`} />
                                        </td>
                                    </tr>
                                    {expandedOrder === order.orderId && (
                                        <tr>
                                            <td colSpan={7} className="px-4 py-0">
                                                <div id={`order-${order.orderId}`} className="py-4 space-y-4">
                                                    <div className="grid grid-cols-2 md:grid-cols-4 gap-3">{[['totalEarned', order.totalCommission], ['historicalApproved', order.totalApproved], ['paid', order.totalPaid], ['reversed', order.totalReversed]].map(([key, amount]) => <div key={key} className="rounded-lg bg-gray-50 dark:bg-white/5 p-3"><p className="text-xs text-gray-500">{t(key === 'historicalApproved' ? 'ux.historicalApproved' : `portal.earningsTab.${key}`)}</p><p className="text-sm font-semibold mt-1">{formatCurrency(amount, order.currency)}</p></div>)}</div>
                                                    <CanonicalOrderDetails order={order.canonical} formatCurrency={formatCurrency} />
                                                    {(order.status === 'AttributedPendingPayment' || order.status === 'Pending') && <p className="text-sm text-amber-700 dark:text-amber-300">{t('ux.pendingNote')}</p>}
                                                    {/* AF-21: Order Lifecycle Status Section */}
                                                    <div className="grid grid-cols-1 md:grid-cols-2 lg:grid-cols-4 gap-3 p-4 rounded-lg border border-gray-200 dark:border-white/[0.06] bg-white dark:bg-white/[0.02]">
                                                        {/* Order Status */}
                                                        {order.orderStatus && (
                                                            <div className="flex items-start gap-2">
                                                                <Package className="w-4 h-4 text-blue-400 mt-0.5 flex-shrink-0" />
                                                                <div className="flex-1 min-w-0">
                                                                    <p className="text-[10px] text-gray-500 uppercase tracking-wider mb-1">{t('portal.ordersTab.orderStatus')}</p>
                                                                    <OrderStatusBadge status={order.orderStatus} t={t} />
                                                                </div>
                                                            </div>
                                                        )}
                                                        
                                                        {/* Fulfillment Status */}
                                                        {order.fulfillmentStatus && (
                                                            <div className="flex items-start gap-2">
                                                                <Truck className="w-4 h-4 text-purple-400 mt-0.5 flex-shrink-0" />
                                                                <div className="flex-1 min-w-0">
                                                                    <p className="text-[10px] text-gray-500 uppercase tracking-wider mb-1">{t('portal.ordersTab.fulfillmentStatus')}</p>
                                                                    <FulfillmentStatusBadge status={order.fulfillmentStatus} t={t} />
                                                                </div>
                                                            </div>
                                                        )}
                                                        
                                                        {/* Payment Method */}
                                                        {order.paymentMethod && (
                                                            <div className="flex items-start gap-2">
                                                                <CreditCard className="w-4 h-4 text-emerald-400 mt-0.5 flex-shrink-0" />
                                                                <div className="flex-1 min-w-0">
                                                                    <p className="text-[10px] text-gray-500 uppercase tracking-wider mb-1">{t('portal.ordersTab.paymentMethod')}</p>
                                                                    <p className="text-sm font-medium text-gray-700 dark:text-gray-300">{t(`ux.paymentMethods.${order.paymentMethod.toUpperCase()}`) === `ux.paymentMethods.${order.paymentMethod.toUpperCase()}` ? order.paymentMethod : t(`ux.paymentMethods.${order.paymentMethod.toUpperCase()}`)}</p>
                                                                </div>
                                                            </div>
                                                        )}
                                                        
                                                        {/* Order Total */}
                                                        {order.paidAmount > 0 && (
                                                            <div className="flex items-start gap-2">
                                                                <DollarSign className="w-4 h-4 text-green-400 mt-0.5 flex-shrink-0" />
                                                                <div className="flex-1 min-w-0">
                                                                    <p className="text-xs text-gray-500 mb-1">{t('ux.commissionBase')}</p>
                                                                    <p className="text-sm font-semibold text-gray-900 dark:text-white">{formatCurrency(order.paidAmount, order.currency)}</p>
                                                                </div>
                                                            </div>
                                                        )}
                                                    </div>

                                                    {/* Order Items */}
                                                    {order.items && order.items.length > 0 && (
                                                        <div className="space-y-2">
                                                            <p className="text-xs text-gray-500 uppercase tracking-wider px-1">{t('portal.ordersTab.orderItems')}</p>
                                                            {order.items.map(item => (
                                                                <div key={item.orderItemId} className={`flex flex-wrap gap-3 items-center justify-between p-3 rounded-lg border ${item.isReversed ? 'border-red-500/10 bg-red-500/5' : 'border-gray-200 dark:border-white/[0.06] bg-white dark:bg-white/[0.02]'
                                                                    }`}>
                                                                    <div>
                                                                        <p className={`text-sm font-medium ${item.isReversed ? 'text-red-400 line-through' : 'text-gray-700 dark:text-gray-300'}`}>
                                                                            {item.productName || (item.productId ? `${t('ux.readModel.productId')}: ${item.productId}` : t('ux.unknown'))}
                                                                        </p>
                                                                        {item.variantName && <p className="text-xs text-gray-600">{item.variantName}</p>}
                                                                        <p className="text-xs text-gray-500">{t('ux.readModel.quantity')}: {item.quantity == null ? t('ux.unknown') : formatNumber(item.quantity)}</p>
                                                                    </div>
                                                                    <div className="flex flex-wrap items-center gap-4 text-xs">
                                                                        <span className="text-gray-500">{t('ux.commissionBase')} {formatCurrency(item.paidAmount, order.currency)}</span>
                                                                        <span className="font-medium text-gray-900 dark:text-white">{t('portal.ordersTab.commission')} {formatCurrency(item.netCommissionAmount ?? item.commissionAmount, order.currency)}</span>
                                                                        <StatusBadge status={item.isReversed ? 'Reversed' : item.settlementStatus} t={t} />
                                                                    </div>
                                                                </div>
                                                            ))}
                                                        </div>
                                                    )}
                                                </div>
                                            </td>
                                        </tr>
                                    )}
                                </Fragment>
                            ))}
                            {!orders.length && <tr><td colSpan={7} className="p-8 text-center text-sm text-gray-500">{t('ux.noMatches')} <button className="text-[#6C63FF]" onClick={() => { setSearch(''); setHistory('all') }}>{t('ux.reset')}</button></td></tr>}
                        </tbody>
                    </table>
                </div>
            </div>

            {/* Pagination */}
            {data.totalPages > 1 && (
                <div className="flex items-center justify-between">
                    <span className="text-sm text-gray-500">{t('portal.ordersTab.page')} {formatNumber(page)} {t('portal.ordersTab.of')} {formatNumber(data.totalPages)}</span>
                    <div className="flex gap-2">
                        <button onClick={() => setPage(p => Math.max(1, p - 1))} disabled={page === 1}
                            className="px-4 py-2 rounded-lg border border-gray-200 dark:border-white/[0.06] text-sm text-gray-600 dark:text-gray-400 hover:text-gray-900 dark:hover:text-white disabled:opacity-30 transition-all">{t('portal.ordersTab.prev')}</button>
                        <button onClick={() => setPage(p => Math.min(data.totalPages, p + 1))} disabled={page === data.totalPages}
                            className="px-4 py-2 rounded-lg border border-gray-200 dark:border-white/[0.06] text-sm text-gray-600 dark:text-gray-400 hover:text-gray-900 dark:hover:text-white disabled:opacity-30 transition-all">{t('portal.ordersTab.next')}</button>
                    </div>
                </div>
            )}
        </div>
    )
}
