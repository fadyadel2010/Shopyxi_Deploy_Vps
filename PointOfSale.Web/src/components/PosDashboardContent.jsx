import { useState } from 'react'
import { useNavigate } from 'react-router-dom'
import { useTranslation } from 'react-i18next'
import { Plus, RefreshCw } from 'lucide-react'
import { useSettings } from '../contexts/SettingsContext'
import StorefrontStatusCard from './StorefrontStatusCard'
import { useRTL } from '../hooks/useRTL'
import { formatDateTime, formatNumber } from '../utils/dateFormat'
import { useAuthStore } from '../store/authStore'
import { hasPosPermission } from '../auth/posPermissions'
import { Badge, Button, Card, Select, Table, TableBody, TableCaption, TableCell, TableHead, TableHeader, TableRow } from '../design-system/primitives'
import usePosDashboardData, { useDashboardActorScope } from './dashboard/usePosDashboardData'
import DashboardMetric from './dashboard/DashboardMetric'
import DashboardDataState from './dashboard/DashboardDataState'
import DashboardSalesTrend from './dashboard/DashboardSalesTrend'
import DashboardRankedAmounts from './dashboard/DashboardRankedAmounts'
import './dashboard/PosDashboard.css'

// Semantic customer identity first, then the real name, then a neutral unavailable label.
const recentSaleCustomerLabel = (sale, t) => {
    if (sale.isWalkInCustomer === true) {
        return t('dashboard.phase42.walkInCustomer')
    }

    if (typeof sale.customerName === 'string' && sale.customerName.trim().length > 0) {
        return sale.customerName
    }

    return t('dashboard.phase42.customerUnavailable')
}

export default function PosDashboardContent() {
    const { t } = useTranslation()
    const { dir, locale } = useRTL()
    const navigate = useNavigate()
    const { formatCurrency, status: settingsStatus, currencyConfirmed, currencyProvenance } = useSettings()
    const user = useAuthStore(state => state.user)
    const actorScope = useDashboardActorScope()
    const canViewStorefrontStatus = hasPosPermission(user, 'storefront.design.manage')
    const canCreateSale = hasPosPermission(user, 'sales.operate')
    const canManageCatalog = hasPosPermission(user, 'catalog.manage')
    const canManageCustomers = hasPosPermission(user, 'customers.manage')
    const canManagePurchases = hasPosPermission(user, 'purchases.manage')
    const canViewInventory = hasPosPermission(user, 'inventory.view')
    const canViewFinance = hasPosPermission(user, 'finance.view')
    const canViewSales = hasPosPermission(user, 'sales.view')
    const [trendPeriod, setTrendPeriod] = useState('week')
    const data = usePosDashboardData(trendPeriod)
    const stats = data.stats.data
    const money = field => stats && stats[field] != null
        && !(field === 'inventoryValue' && stats.inventoryValuationComplete === false)
        ? formatCurrency(stats[field]) : undefined
    const count = field => stats && stats[field] != null ? formatNumber(stats[field], locale) : undefined
    const copy = key => t(`dashboard.phase42.${key}`)
    const busy = ['stats', 'trend', 'products', 'sales', 'stock', 'categories', 'payments'].some(key => data[key].pending)
    const ranked = (source, label, value, title, basis) => (
        <Card as="section" className="pos-dashboard__panel">
            <h2>{title}</h2>
            <p className="pos-dashboard__caption">{basis}</p>
            <DashboardDataState source={source}>
                <DashboardRankedAmounts data={(source.data || []).map(row => ({
                    label: label === 'paymentMethod' ? t(`dashboard.phase42.paymentLabels.${row[label]}`, { defaultValue: row[label] })
                        : label === 'categoryName' && row.isUncategorized === true ? copy('uncategorized') : row[label],
                    value: row[value],
                }))} caption={title} labelHeading={copy('name')} valueHeading={t('dashboard.amount')} formatValue={formatCurrency} />
            </DashboardDataState>
        </Card>
    )

    return (
        <div className="pos-dashboard__pos" dir={dir}>
            <div className="pos-dashboard__toolbar">
                <div className="pos-dashboard__actions" aria-label={t('dashboard.quickActions')}>
                    {canCreateSale ? (<Button leadingIcon={<Plus />} onClick={() => navigate('/pos')}>{t('dashboard.newSale')}</Button>) : null}
                    {canManageCatalog ? (<Button variant="secondary" onClick={() => navigate('/products')}>{t('dashboard.addProduct')}</Button>) : null}
                    {canManageCustomers ? (<Button variant="secondary" onClick={() => navigate('/customers')}>{t('dashboard.addCustomer')}</Button>) : null}
                    {canManagePurchases ? (<Button variant="secondary" onClick={() => navigate('/purchase-invoice')}>{t('dashboard.newPurchase')}</Button>) : null}
                </div>
                <Button variant="secondary" leadingIcon={<RefreshCw />} onClick={data.refresh} aria-busy={busy}>{t('common.refresh')}</Button>
            </div>
            {settingsStatus && !currencyConfirmed ? <p role="status" className="pos-dashboard__caption">{copy('currencyUnconfirmed')}</p> : null}
            {currencyProvenance === 'stale' ? <p role="status" className="pos-dashboard__caption">{copy('currencyStale')}</p> : null}

            <section aria-label={copy('overview')}>
                <DashboardDataState source={data.stats} showUnavailableChildren>
                    <div className="pos-dashboard__kpis">
                        {canViewFinance ? <DashboardMetric label={copy('todaySales')} value={money('todaySales')} caption={copy('todayBasis')}>
                            <p className="pos-dashboard__caption">{copy('change')}: <bdi>{stats?.salesChangePercentage != null ? `${formatNumber(stats.salesChangePercentage, locale, { minimumFractionDigits: 1, maximumFractionDigits: 1, signDisplay: 'exceptZero' })}%` : '—'}</bdi></p>
                            <p className="pos-dashboard__caption">{copy('changeCaution')}</p>
                        </DashboardMetric> : null}
                        {canViewFinance ? <DashboardMetric label={copy('saleJournals')} value={count('todayTransactionCount')} caption={copy('journalsBasis')} /> : null}
                        <DashboardMetric label={copy('lowStockPositions')} value={count('lowStockCount')} caption={copy('stockBasis')}>
                            {canViewInventory ? <Button variant="ghost" size="sm" onClick={() => navigate('/inventory')}>{t('dashboard.viewDetails')}</Button> : null}
                        </DashboardMetric>
                        {canViewFinance ? <DashboardMetric label={t('dashboard.inventoryValue')} value={money('inventoryValue')} caption={copy('inventoryBasis')}>
                            {stats?.inventoryValuationComplete === false ? <p role="status">{copy('incompleteValuation')}</p> : null}
                        </DashboardMetric> : null}
                    </div>
                    <div className="pos-dashboard__counts">
                        <span>{t('dashboard.totalProducts')}: <bdi>{count('totalProducts')}</bdi></span>
                        <span>{t('dashboard.totalCustomers')}: <bdi>{count('totalCustomers')}</bdi></span>
                    </div>
                    {canViewFinance ? <><p className="pos-dashboard__caption">{copy('calendarBasis')}</p>
                    <div className="pos-dashboard__three">
                        {[['thisWeek', 'weekSales'], ['thisMonth', 'monthSales'], ['thisYear', 'yearSales']].map(([label, field]) => <DashboardMetric key={field} label={t(`dashboard.${label}`)} value={money(field)} />)}
                    </div></> : <p className="pos-dashboard__caption">{copy('restricted')}</p>}
                </DashboardDataState>
            </section>

            {canViewFinance ? <div className="pos-dashboard__performance">
                <Card as="section" className="pos-dashboard__panel">
                    <div className="pos-dashboard__section-heading">
                        <div><h2>{t('dashboard.salesTrend')}</h2><p className="pos-dashboard__caption">{copy('ledgerBasis')}</p></div>
                        <Select label={copy('trendPeriod')} value={trendPeriod} onChange={event => setTrendPeriod(event.target.value)}>
                            <option value="week">{t('dashboard.last7Days')}</option>
                            <option value="month">{t('dashboard.last30Days')}</option>
                            <option value="year">{t('dashboard.last12Months')}</option>
                        </Select>
                    </div>
                    <p className="pos-dashboard__caption">{copy(trendPeriod === 'year' ? 'yearTrendBasis' : 'dayTrendBasis')}</p>
                    <DashboardDataState source={data.trend}>
                        <DashboardSalesTrend data={data.trend.data || []} period={trendPeriod} formatCurrency={formatCurrency} />
                    </DashboardDataState>
                </Card>
                <div className="pos-dashboard__products">{ranked(data.products, 'productName', 'totalRevenue', t('dashboard.topSellingProducts'), copy('lineBasis'))}</div>
            </div> : null}

            <div className="pos-dashboard__pair">
                <Card as="section" className="pos-dashboard__panel">
                    <div className="pos-dashboard__section-heading">
                        <h2>{t('dashboard.lowStockAlert')}</h2>
                        {canViewInventory ? (<Button variant="ghost" size="sm" onClick={() => navigate('/inventory')}>{t('dashboard.viewAll')}</Button>) : null}
                    </div>
                    <p className="pos-dashboard__caption">{copy('stockListBasis')}</p>
                    <DashboardDataState source={data.stock} emptyMessage={copy('noLowStock')}>
                        <Table className="pos-dashboard__operations-table">
                            <TableCaption>{t('dashboard.lowStockAlert')}</TableCaption>
                            <TableHeader><TableRow>{['product', 'warehouse', 'stock'].map(key => <TableHead key={key}>{key === 'stock' ? copy('availableStock') : t(`dashboard.${key}`)}</TableHead>)}<TableHead>{t('common.status')}</TableHead></TableRow></TableHeader>
                            <TableBody>{(data.stock.data || []).map((item, index) => <TableRow key={index}>
                                <TableCell><bdi>{item.productName}</bdi></TableCell>
                                <TableCell><bdi>{item.warehouseName}</bdi></TableCell>
                                <TableCell className="pos-dashboard__number"><bdi>{formatNumber(item.currentStock, locale)}</bdi></TableCell>
                                <TableCell><Badge variant={item.status === 'Out of Stock' ? 'danger' : 'warning'}>{t(item.status === 'Out of Stock' ? 'dashboard.outOfStock' : 'dashboard.lowStockStatus')}</Badge></TableCell>
                            </TableRow>)}</TableBody>
                        </Table>
                    </DashboardDataState>
                </Card>
                {canViewSales ? <Card as="section" className="pos-dashboard__panel">
                    <div className="pos-dashboard__section-heading">
                        <h2>{t('dashboard.recentSales')}</h2>
                        <Button variant="ghost" size="sm" onClick={() => navigate('/sales-history')}>{t('dashboard.viewAll')}</Button>
                    </div>
                    <p className="pos-dashboard__caption">{copy('recentBasis')}</p>
                    <DashboardDataState source={data.sales} emptyMessage={t('dashboard.noRecentSales')}>
                        <Table className="pos-dashboard__operations-table">
                            <TableCaption>{t('dashboard.recentSales')}</TableCaption>
                            <TableHeader><TableRow>{['invoice', 'customer', 'amount', 'date'].map(key => <TableHead key={key}>{t(`dashboard.${key}`)}</TableHead>)}</TableRow></TableHeader>
                            <TableBody>{(data.sales.data || []).map((sale, index) => <TableRow key={sale.id || index}>
                                <TableCell><bdi>{sale.invoiceNumber}</bdi></TableCell>
                                <TableCell><bdi>{recentSaleCustomerLabel(sale, t)}</bdi></TableCell>
                                <TableCell className="pos-dashboard__number"><bdi>{formatCurrency(sale.totalAmount)}</bdi></TableCell>
                                <TableCell><bdi>{formatDateTime(sale.invoiceDate, locale)}</bdi></TableCell>
                            </TableRow>)}</TableBody>
                        </Table>
                    </DashboardDataState>
                </Card> : null}
            </div>
            {canViewFinance ? <><div className="pos-dashboard__pair">
                {ranked(data.categories, 'categoryName', 'totalRevenue', t('dashboard.salesByCategory'), copy('lineBasis'))}
                {ranked(data.payments, 'paymentMethod', 'totalAmount', t('dashboard.paymentMethods'), copy('paymentBasis'))}
            </div>
            <section aria-label={copy('financialSummary')}>
                <h2>{copy('financialSummary')}</h2>
                <DashboardDataState source={data.stats} showUnavailableChildren>
                    <div className="pos-dashboard__three">
                        <DashboardMetric label={copy('allTimeSales')} value={money('totalRevenue')} caption={copy('allTimeBasis')} />
                        <DashboardMetric label={copy('customerNet')} value={money('outstandingReceivables')} caption={copy('customerBasis')} />
                        <DashboardMetric label={copy('supplierNet')} value={money('outstandingPayables')} caption={copy('supplierBasis')} />
                    </div>
                </DashboardDataState>
            </section></> : null}
            {canViewStorefrontStatus ? <StorefrontStatusCard key={actorScope} presentation="dashboard" /> : null}
        </div>
    )
}
