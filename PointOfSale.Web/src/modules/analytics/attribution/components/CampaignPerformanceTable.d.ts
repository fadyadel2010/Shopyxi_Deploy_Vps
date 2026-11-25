import type { ReactElement } from 'react'
export interface CampaignPerformance {
    campaign: string
    source: string
    medium?: string
    revenue: number
    orders: number
    averageOrderValue: number
}
export default function CampaignPerformanceTable(props: { data: { campaigns: CampaignPerformance[] } | null; loading: boolean }): ReactElement | null
