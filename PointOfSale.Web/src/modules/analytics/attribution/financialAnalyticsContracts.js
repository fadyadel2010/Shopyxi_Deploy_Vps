const number = value => typeof value === 'number' && Number.isFinite(value)
const count = value => Number.isSafeInteger(value) && value >= 0
const text = value => typeof value === 'string'
const nullableNumber = value => value == null || number(value)
const rows = (value, valid) => Array.isArray(value) && value.every(row => row && valid(row))
const source = row => text(row.source) && number(row.revenue) && count(row.orders)
    && number(row.averageOrderValue) && nullableNumber(row.percentageOfTotal)
const campaign = row => source(row) && text(row.campaign)
const totals = value => value && number(value.totalRevenue) && count(value.totalOrders)
const roiTotals = value => totals(value) && number(value.totalCost) && number(value.totalProfit)
    && nullableNumber(value.overallRoi) && nullableNumber(value.costPerAcquisition)

export const financialAnalyticsContracts = {
    summary: value => totals(value) && number(value.recognizedRevenue) && rows(value.revenueBySource, row => source(row) && number(row.percentageOfTotal)),
    campaigns: value => value && rows(value.campaigns, campaign),
    orders: value => value && count(value.totalCount) && rows(value.orders, row =>
        text(row.orderId) && number(row.orderValue) && text(row.source) && text(row.currency)
        && text(row.attributedAt) && Number.isFinite(Date.parse(row.attributedAt))),
    models: value => value && [value.firstTouch, value.lastTouch].every(model =>
        totals(model) && rows(model.topSources, source)),
    roiSummary: value => roiTotals(value) && rows(value.sources, row =>
        source(row) && number(row.cost) && number(row.profit) && nullableNumber(row.roiPercentage)),
    roiCampaigns: value => roiTotals(value) && rows(value.campaigns, row =>
        campaign(row) && number(row.cost) && number(row.profit) && nullableNumber(row.roiPercentage)),
    costs: value => value && count(value.totalCount) && nullableNumber(value.totalCost)
        && rows(value.costs, row => text(row.campaignCostId) && number(row.costAmount) && text(row.currency)),
}
