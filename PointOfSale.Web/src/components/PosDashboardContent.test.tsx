import { readFileSync } from 'node:fs'
import { resolve } from 'node:path'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { cleanup, fireEvent, render, screen, within } from '@testing-library/react'
import PosDashboardContent from './PosDashboardContent'
import { useAuthStore } from '../store/authStore'

const mocks = vi.hoisted(() => ({ navigate: vi.fn(), retry: vi.fn(), refresh: vi.fn() }))
vi.mock('react-router-dom', () => ({ useNavigate: () => mocks.navigate }))
vi.mock('react-i18next', () => ({ useTranslation: () => ({ t: (key: string) => key }) }))
vi.mock('../hooks/useRTL', () => ({ useRTL: () => ({ locale: 'en-US', dir: 'ltr' }) }))
vi.mock('../contexts/SettingsContext', () => ({ useSettings: () => ({ formatCurrency: (value: number) => `EGP ${value}` }) }))
vi.mock('./StorefrontStatusCard', () => ({ default: () => <div>Storefront status widget</div> }))
vi.mock('./dashboard/usePosDashboardData', () => ({
    useDashboardActorScope: () => 'actor',
    default: () => {
        const failed = { data: null, pending: false, error: 'failed', retry: mocks.retry }
        return { stats: failed, trend: failed, products: failed, sales: failed, stock: failed, categories: failed, payments: failed, refresh: mocks.refresh }
    },
}))
afterEach(() => { cleanup(); mocks.navigate.mockClear() })

const readSource = (path: string) => readFileSync(resolve(process.cwd(), 'src', path), 'utf8')

describe('PosDashboardContent permission gating', () => {
    it('renders admin-only widgets and actions only for permission holders', () => {
        const source = readSource('components/PosDashboardContent.jsx')

        expect(source).toContain("hasPosPermission(user, 'storefront.design.manage')")
        expect(source).toContain('canViewStorefrontStatus ? <StorefrontStatusCard')
        expect(source).toContain('presentation="dashboard"')
        expect(source).toContain("hasPosPermission(user, 'sales.operate')")
        expect(source).toContain("hasPosPermission(user, 'catalog.manage')")
        expect(source).toContain("hasPosPermission(user, 'customers.manage')")
        expect(source).toContain("hasPosPermission(user, 'purchases.manage')")
        expect(source).toContain("hasPosPermission(user, 'inventory.view')")
        for (const flag of ['canCreateSale', 'canManageCatalog', 'canManageCustomers', 'canManagePurchases', 'canViewInventory']) {
            expect(source).toContain(`{${flag} ? (`)
        }
        for (const destination of ['/inventory', '/sales-history', '/pos', '/products', '/customers', '/purchase-invoice']) {
            expect(source).toContain(`navigate('${destination}')`)
        }
    })

    it('gates the dashboard storefront tab by analytics permission', () => {
        const dashboard = readSource('pages/Dashboard.jsx')

        expect(dashboard).toContain("permission: 'storefront.analytics.view'")
        expect(dashboard).toContain('availableTabs')
    })
    it('preserves selling shortcuts without unauthorized inventory or financial actions', () => {
        useAuthStore.setState({ user: { id: 'c', role: 'Cashier', effectivePermissions: ['dashboard.view', 'sales.operate', 'sales.view'] } })
        render(<PosDashboardContent />)
        expect(screen.queryByText('Storefront status widget')).not.toBeInTheDocument()
        expect(screen.queryByRole('button', { name: 'dashboard.addProduct' })).not.toBeInTheDocument()
        fireEvent.click(screen.getByRole('button', { name: 'dashboard.newSale' }))
        expect(mocks.navigate).toHaveBeenLastCalledWith('/pos')
        expect(screen.queryByRole('button', { name: 'dashboard.viewDetails' })).not.toBeInTheDocument()
        expect(screen.queryByRole('heading', { name: 'dashboard.salesTrend' })).not.toBeInTheDocument()
        const stockSection = screen.getByRole('heading', { name: 'dashboard.lowStockAlert' }).closest('section')!
        expect(within(stockSection).queryByRole('button', { name: 'dashboard.viewAll' })).not.toBeInTheDocument()
        const recentSection = screen.getByRole('heading', { name: 'dashboard.recentSales' }).closest('section')!
        fireEvent.click(within(recentSection).getByRole('button', { name: 'dashboard.viewAll' }))
        expect(mocks.navigate).toHaveBeenLastCalledWith('/sales-history')
        expect(screen.queryByText('dashboard.allStockGood')).not.toBeInTheDocument()
        expect(screen.queryByText('EGP 0')).not.toBeInTheDocument()
    })
    it('retains all management destinations and separately permission-gated inventory list action', () => {
        useAuthStore.setState({ user: { id: 'm', role: 'Manager', effectivePermissions: ['dashboard.view', 'catalog.manage', 'customers.manage', 'purchases.manage', 'inventory.view', 'storefront.design.manage'] } })
        render(<PosDashboardContent />)
        for (const [label, destination] of [['addProduct', '/products'], ['addCustomer', '/customers'], ['newPurchase', '/purchase-invoice']]) {
            fireEvent.click(screen.getByRole('button', { name: `dashboard.${label}` }))
            expect(mocks.navigate).toHaveBeenLastCalledWith(destination)
        }
        expect(screen.getByText('Storefront status widget')).toBeInTheDocument()
        const stockSection = screen.getByRole('heading', { name: 'dashboard.lowStockAlert' }).closest('section')!
        fireEvent.click(within(stockSection).getByRole('button', { name: 'dashboard.viewAll' }))
        expect(mocks.navigate).toHaveBeenLastCalledWith('/inventory')
    })
})
