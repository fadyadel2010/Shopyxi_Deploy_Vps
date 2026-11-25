import type { ReactElement } from 'react'
export interface AttributionCardData {
    totalRevenue: number
    recognizedRevenue?: number
    totalOrders: number
}
export default function AttributionSummaryCards(props: { data: AttributionCardData | null; loading: boolean }): ReactElement | null
