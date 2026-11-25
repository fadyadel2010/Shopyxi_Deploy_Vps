import { useFinancialAnalyticsSource } from './useFinancialAnalyticsSource'
import { financialAnalyticsContracts as contracts } from '../financialAnalyticsContracts'

// Presentation-only reads. Attribution and gross/net business definitions remain
// server-owned; source validation never substitutes zeros for unavailable values.
export function useAttributionSummary(model = 'LastTouch', period = 'last_7_days') {
    return useFinancialAnalyticsSource('/admin/analytics/attribution/summary', { model, period }, contracts.summary)
}
export function useAttributionCampaigns(model = 'LastTouch', period = 'last_7_days') {
    return useFinancialAnalyticsSource('/admin/analytics/attribution/campaigns', { model, period }, contracts.campaigns)
}
export function useAttributionOrders(model = 'LastTouch', period = 'last_7_days', skip = 0, take = 50) {
    return useFinancialAnalyticsSource('/admin/analytics/attribution/orders', { model, period, skip, take }, contracts.orders)
}
export function useAttributionModels(period = 'last_7_days') {
    return useFinancialAnalyticsSource('/admin/analytics/attribution/models', { period }, contracts.models)
}
