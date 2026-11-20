import type { CampaignPerformance } from '../components/CampaignPerformanceTable'
export interface FinancialSource<T> {
    data: T | null
    loading: boolean
    error: string | null
    refetch: () => Promise<boolean>
}
export interface AttributionSummary {
    totalRevenue: number
    recognizedRevenue: number
    totalOrders: number
    revenueBySource: Array<{ source: string; revenue: number; orders: number; averageOrderValue: number }>
}
export function useAttributionSummary(model?: string, period?: string): FinancialSource<AttributionSummary>
export function useAttributionCampaigns(model?: string, period?: string): FinancialSource<{ campaigns: CampaignPerformance[] }>
export function useAttributionOrders(model?: string, period?: string, skip?: number, take?: number): FinancialSource<{ totalCount: number; orders: Array<{ orderId: string; orderValue: number; source: string; currency: string }> }>
export function useAttributionModels(period?: string): FinancialSource<{ firstTouch: Omit<AttributionSummary, 'revenueBySource'> & { topSources: AttributionSummary['revenueBySource'] }; lastTouch: Omit<AttributionSummary, 'revenueBySource'> & { topSources: AttributionSummary['revenueBySource'] } }>
