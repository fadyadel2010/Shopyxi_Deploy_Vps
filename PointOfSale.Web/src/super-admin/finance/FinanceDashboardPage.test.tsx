import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import type { ComponentProps } from 'react'
import FinanceDashboardPage from './FinanceDashboardPage'
import * as service from './financeService'
import type FinanceCharts from './FinanceCharts'

vi.mock('./financeService', () => ({
    getFinancialOverview: vi.fn(), getFinancialTrends: vi.fn(),
    getFinancialPromos: vi.fn(), getFinancialVendors: vi.fn(),
    getFinancialRefunds: vi.fn(), getFinancialIntegrityCheck: vi.fn(),
}))
vi.mock('../i18n/useSuperAdminLocale', () => ({
    useSuperAdminLocale: () => ({
        isArabic: false, tr: (en: string) => en, locale: 'en-US',
        formatCurrency: (n: number) => `EGP ${n.toFixed(2)}`,
        formatNumber: (n: number) => String(n),
        formatPercent: (n: number) => `${n * 100}%`,
        formatDateTime: (s: string) => s || '—',
    }),
}))
vi.mock('./FinanceCharts', () => ({
    default: (props: ComponentProps<typeof FinanceCharts>) => (
        <div>
            <input aria-label="From date" value={props.fromDate} onChange={e => props.onFromDateChange(e.target.value)} />
            <input aria-label="Vendor filter" value={props.vendorSearch} onChange={e => props.onVendorSearchChange(e.target.value)} />
            <input aria-label="Promo filter" value={props.promoCode} onChange={e => props.onPromoCodeChange(e.target.value)} />
            <button onClick={props.onResetFilters}>Reset filters</button>
        </div>
    ),
}))
vi.mock('./FinancePerformanceTables', () => ({ default: () => null }))
vi.mock('./VendorFinanceWorkspace', () => ({ default: () => null }))

const zero = {
    currency: 'EGP', totalGrossCommission: 0, totalPromoCost: 0, totalNetCommission: 0,
    netRevenue: 0, totalRefundAmount: 0, averageOrderValue: 0, totalOrdersCount: 0,
    refundedOrdersCount: 0, refundRate: 0, refundImpactOnRevenue: 0,
    dataSource: 'LIVE_LEDGER', fromDateUtc: '2026-10-18T00:00:00Z',
    toDateUtc: '2026-11-16T23:59:59Z',
} as service.SuperAdminFinancialOverview
let client: QueryClient
function mount() {
    client = new QueryClient({ defaultOptions: { queries: { retry: false, gcTime: 0 } } })
    return render(<QueryClientProvider client={client}><FinanceDashboardPage /></QueryClientProvider>)
}
beforeEach(() => {
    vi.mocked(service.getFinancialOverview).mockResolvedValue(zero)
    vi.mocked(service.getFinancialTrends).mockResolvedValue({ points: [] } as unknown as service.SuperAdminFinancialTrends)
    vi.mocked(service.getFinancialPromos).mockResolvedValue({ items: [] } as unknown as Awaited<ReturnType<typeof service.getFinancialPromos>>)
    vi.mocked(service.getFinancialVendors).mockResolvedValue({ items: [] } as unknown as Awaited<ReturnType<typeof service.getFinancialVendors>>)
    vi.mocked(service.getFinancialRefunds).mockResolvedValue({
        alerts: [], topRefundedVendors: [], topRefundedItems: [],
    } as unknown as Awaited<ReturnType<typeof service.getFinancialRefunds>>)
    vi.mocked(service.getFinancialIntegrityCheck).mockResolvedValue({
        status: 'NO_RECORDS', issues: [], totalIssueCount: 0,
    } as unknown as service.SuperAdminFinancialIntegrityCheck)
})
afterEach(() => { cleanup(); client?.clear(); vi.resetAllMocks() })

describe('Finance page query states and scope', () => {
    it('does not present loading data as zero or an empty financial population', () => {
        vi.mocked(service.getFinancialOverview).mockReturnValue(new Promise(() => {}))
        mount()
        expect(screen.queryByText('No recognized financial activity in this scope')).not.toBeInTheDocument()
        expect(screen.queryByText('EGP 0.00')).not.toBeInTheDocument()
        expect(screen.getByRole('button', { name: 'Refreshing' })).toBeDisabled()
    })

    it('shows a recoverable unavailable state, not fabricated zero KPIs', async () => {
        vi.mocked(service.getFinancialOverview).mockRejectedValue(new Error('private backend details'))
        mount()
        expect(await screen.findByText('Financial dashboard unavailable')).toBeInTheDocument()
        expect(screen.queryByText('private backend details')).not.toBeInTheDocument()
        expect(screen.queryByText('EGP 0.00')).not.toBeInTheDocument()
        vi.mocked(service.getFinancialOverview).mockResolvedValue(zero)
        fireEvent.click(screen.getByRole('button', { name: 'Retry request' }))
        expect(await screen.findByText('No recognized financial activity in this scope')).toBeInTheDocument()
    })

    it('labels previous values explicitly if a refresh fails', async () => {
        mount()
        await screen.findByText('No recognized financial activity in this scope')
        vi.mocked(service.getFinancialOverview).mockRejectedValue(new Error('refresh failed'))
        await waitFor(() => expect(screen.getByRole('button', { name: 'Refresh' })).toBeEnabled())
        fireEvent.click(screen.getByRole('button', { name: 'Refresh' }))
        expect(await screen.findByText('Refresh failed — showing previous results')).toBeInTheDocument()
        expect(screen.queryByText('No recognized financial activity in this scope')).not.toBeInTheDocument()
    })

    it('sends date, normalized promo and vendor filters to all six read endpoints', async () => {
        mount()
        await screen.findByText('No recognized financial activity in this scope')
        fireEvent.change(screen.getByLabelText('From date'), { target: { value: '2026-10-18' } })
        fireEvent.change(screen.getByLabelText('Vendor filter'), { target: { value: '  Test vendor  ' } })
        fireEvent.change(screen.getByLabelText('Promo filter'), { target: { value: '  promo  ' } })
        for (const fn of Object.values(service)) {
            await waitFor(() => expect(fn).toHaveBeenLastCalledWith(expect.objectContaining({
                fromDate: '2026-10-18', vendorSearch: 'Test vendor', promoCode: 'PROMO',
            })))
        }
        fireEvent.click(screen.getByRole('button', { name: 'Reset filters' }))
        await waitFor(() => expect(service.getFinancialOverview).toHaveBeenLastCalledWith(expect.objectContaining({
            vendorSearch: undefined, promoCode: undefined,
        })))
    })
})
