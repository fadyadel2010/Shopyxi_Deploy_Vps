import { useLanguage } from '../../i18n/LanguageContext'

export default function CanonicalOrderDetails({ order, formatCurrency, showItems = false }) {
    const { t, formatNumber, formatDate } = useLanguage()
    const label = key => t(`ux.readModel.${key}`)
    if (!order) return <p className="text-xs text-gray-500">{t('ux.missingOrderFields')}</p>
    const paymentKey = `ux.readModel.payments.${order.paymentStatus}`
    const sourceKey = `ux.readModel.sources.${order.attributionSource}`
    const fields = [
        ['orderTotal', formatCurrency(order.orderTotal, order.orderCurrency)],
        ['shipping', formatCurrency(order.shippingCharge, order.orderCurrency)],
        ['paymentFee', formatCurrency(order.paymentFee, order.orderCurrency)],
        ['paymentStatus', t(paymentKey) === paymentKey ? order.paymentStatus : t(paymentKey)],
        ['store', order.storeName || order.storeTenantId],
        ['referral', order.referralCode],
        ['source', t(sourceKey) === sourceKey ? order.attributionSource : t(sourceKey)],
        ['promotionLink', order.promotionLinkId || t('ux.unknown')],
        ['promotedProduct', order.promotedProductId || t('ux.unknown')],
    ]
    return <section aria-label={label('canonicalDetails')} className="space-y-3">
        {order.isSyntheticQa && <p className="text-sm text-amber-700 dark:text-amber-300">{t('portal.syntheticQa.label')} — {t('portal.syntheticQa.excluded')}</p>}
        <dl className="grid grid-cols-1 sm:grid-cols-2 lg:grid-cols-3 gap-3">{fields.map(([key, value]) => <div key={key} className="p-3 rounded-lg bg-gray-50 dark:bg-white/5 min-w-0"><dt className="text-xs text-gray-500">{label(key)}</dt><dd className="text-sm mt-1 break-words">{value ?? t('ux.unknown')}</dd></div>)}</dl>
        <p className="text-xs text-gray-500">{label('totalNote')}</p>
        <p className="text-xs text-gray-500">{label('historyOnly')}</p>
        {showItems && <><p className="text-sm break-words">{order.orderNumber} · {formatDate(order.createdAtUtc)} · {t(`portal.ordersTab.orderStatus${order.orderStatus}`)} · {t(`portal.ordersTab.fulfillmentStatus${order.fulfillmentStatus}`)}</p><ul className="space-y-2">{order.items.map(item => <li key={item.orderItemId} className="border border-gray-200 dark:border-white/10 rounded-lg p-3 text-sm break-words"><p>{item.productName || `${label('productId')}: ${item.productId}`} {item.variantName} · {label('quantity')}: {formatNumber(item.quantity)}</p><p className="text-xs text-gray-500">{label('linePayment')}: {formatCurrency(item.linePaymentAllocated, order.orderCurrency)} · {label('lineRefund')}: {formatCurrency(item.lineRefunded, order.orderCurrency)}</p></li>)}</ul></>}
    </section>
}
