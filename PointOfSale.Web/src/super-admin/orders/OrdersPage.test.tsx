import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter } from 'react-router-dom'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import OrdersPage from './OrdersPage'
import * as service from './ordersService'

vi.mock('./ordersService', async () => ({
    ...await vi.importActual('./ordersService'),
    getSuperAdminOrders: vi.fn(), getSuperAdminOrderStats: vi.fn(),
    getSuperAdminOrderTenants: vi.fn(), getSuperAdminOrderDetails: vi.fn(),
    downloadSuperAdminOrdersCsv: vi.fn(),
}))
vi.mock('../i18n/useSuperAdminLocale', () => ({
    useSuperAdminLocale: () => ({
        isArabic: false, tr: (en: string) => en, locale: 'en-US',
        formatCurrency: (n: number) => `EGP ${n.toFixed(2)}`,
        formatNumber: (n: number) => String(n),
        formatPercent: (n: number) => `${n * 100}%`,
        formatDateTime: (s: string, fallback = '—') => s || fallback,
    }),
}))
let client: QueryClient
const fresh = () => new Date().toISOString()
function mount(query = '') {
    client = new QueryClient({defaultOptions:{queries:{retry:false,gcTime:0}}})
    return render(<QueryClientProvider client={client}><MemoryRouter initialEntries={['/super-admin/orders'+query]}><OrdersPage /></MemoryRouter></QueryClientProvider>)
}
beforeEach(() => {
    vi.mocked(service.getSuperAdminOrders).mockImplementation(async () => ({
        orders:[],meta:{page:1,pageSize:20,totalCount:0,totalPages:0,asOfUtc:fresh()},
    }))
    vi.mocked(service.getSuperAdminOrderStats).mockImplementation(async () => ({
        windowStartUtc:fresh(),windowEndUtc:fresh(),asOfUtc:fresh(),currency:'EGP',
        totalOrdersToday:0,pendingOrders:0,failedPayments:0,capturedSalesToday:0,
    }))
    vi.mocked(service.getSuperAdminOrderTenants).mockResolvedValue([])
})
afterEach(() => { cleanup();client?.clear();vi.clearAllMocks() })

describe('Orders screen read-only authority', () => {
    it('renders zero scope without inventing a one-page result and labels sales honestly', async () => {
        mount()
        expect(await screen.findByText('No orders matched these filters')).toBeInTheDocument()
        expect(screen.getByText('No matching orders. Page controls are disabled.')).toBeInTheDocument()
        expect(screen.queryByText(/Page 1 of 1/)).not.toBeInTheDocument()
        expect(screen.getByRole('button',{name:'Previous'})).toBeDisabled()
        expect(screen.getByRole('button',{name:'Next'})).toBeDisabled()
        expect(screen.getByText('Captured sales today')).toBeInTheDocument()
        expect(screen.queryByText('Revenue today')).not.toBeInTheDocument()
        expect(screen.getByText('Auto-refresh active · 30s')).toBeInTheDocument()
    })
    it('does not display loading stats as financial zero', () => {
        vi.mocked(service.getSuperAdminOrderStats).mockReturnValue(new Promise(()=>{}))
        mount()
        expect(screen.queryByText('EGP 0.00')).not.toBeInTheDocument()
    })
    it('withholds failed statistics and marks monitoring interrupted', async () => {
        vi.mocked(service.getSuperAdminOrderStats).mockRejectedValue(Error('fixture error'))
        mount()
        expect(await screen.findByText('Summary temporarily unavailable')).toBeInTheDocument()
        expect(screen.queryByText('EGP 0.00')).not.toBeInTheDocument()
        expect(screen.getByText('Auto-refresh interrupted')).toBeInTheDocument()
    })
    it('distinguishes failed list refresh from a valid empty result', async () => {
        mount();await screen.findByText('No orders matched these filters')
        vi.mocked(service.getSuperAdminOrders).mockRejectedValue(Error('refresh failed'))
        fireEvent.click(screen.getByRole('button',{name:'Refresh'}))
        expect(await screen.findByText('Orders unavailable')).toBeInTheDocument()
        expect(screen.queryByText('No orders matched these filters')).not.toBeInTheDocument()
    })
    it('fails closed on invalid query links and permits reset', async () => {
        mount('?status=Packed&fromDate=2026-11-20&toDate=2026-11-01')
        expect(screen.getByText('Invalid order filters')).toBeInTheDocument()
        expect(service.getSuperAdminOrders).not.toHaveBeenCalled()
        fireEvent.click(screen.getAllByRole('button',{name:'Reset filters'})[0])
        expect(await screen.findByText('No orders matched these filters')).toBeInTheDocument()
    })
    it('applies combined filters, stable sort and URL state then resets all', async () => {
        mount('?status=Confirmed&paymentStatus=Paid&fromDate=2026-10-18&toDate=2026-11-16&sortBy=TotalAmount&sortDirection=Asc&pageSize=50')
        await waitFor(()=>expect(service.getSuperAdminOrders).toHaveBeenCalledWith(expect.objectContaining({
            status:'Confirmed',paymentStatus:'Paid',fromDate:'2026-10-18',toDate:'2026-11-16',sortBy:'TotalAmount',sortDirection:'Asc',pageSize:50,
        })))
        fireEvent.click(screen.getByRole('button',{name:'Customer'}))
        await waitFor(()=>expect(service.getSuperAdminOrders).toHaveBeenLastCalledWith(expect.objectContaining({sortBy:'Customer',sortDirection:'Desc',page:1})))
        fireEvent.click(screen.getByRole('button',{name:'Reset filters'}))
        await waitFor(()=>expect(service.getSuperAdminOrders).toHaveBeenLastCalledWith(expect.objectContaining({
            status:'',paymentStatus:'',fromDate:'',toDate:'',sortBy:'CreatedAt',sortDirection:'Desc',pageSize:20,page:1,
        })))
    })
    it('keeps native date semantics and gives both inputs accessible labels', async () => {
        mount();await screen.findByText('No orders matched these filters')
        expect(screen.getByLabelText('Orders created from date')).toHaveAttribute('type','date')
        fireEvent.change(screen.getByLabelText('Orders created through date'),{target:{value:'2026-11-16'}})
        await waitFor(()=>expect(service.getSuperAdminOrders).toHaveBeenLastCalledWith(expect.objectContaining({toDate:'2026-11-16'})))
    })
    it('reports CSV failure truthfully instead of a success notice', async () => {
        vi.mocked(service.downloadSuperAdminOrdersCsv).mockRejectedValue(Error('export rejected'))
        mount();await screen.findByText('No orders matched these filters')
        fireEvent.click(screen.getByRole('button',{name:'Export CSV'}))
        expect(await screen.findByText('Export failed')).toBeInTheDocument()
        expect(screen.queryByText('CSV downloaded')).not.toBeInTheDocument()
    })
    it('does not treat an old server snapshot as live merely because HTTP succeeded', async () => {
        vi.mocked(service.getSuperAdminOrderStats).mockResolvedValue({
            windowStartUtc:fresh(),windowEndUtc:fresh(),asOfUtc:'2020-01-01T00:00:00Z',currency:'EGP',
            totalOrdersToday:0,pendingOrders:0,failedPayments:0,capturedSalesToday:0,
        })
        mount();await screen.findByText('No orders matched these filters')
        expect(screen.getByText('Awaiting fresh snapshot')).toBeInTheDocument()
        expect(screen.queryByText('Auto-refresh active · 30s')).not.toBeInTheDocument()
    })
})
