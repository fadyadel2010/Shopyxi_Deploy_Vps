import type { ReactElement } from 'react'
export default function RevenueBySourceChart(props: { loading: boolean; data: {
    revenueBySource: Array<{ source: string; medium?: string; revenue: number; orders: number; percentageOfTotal: number }>
} | null }): ReactElement | null
