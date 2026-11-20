import { cleanup, render, screen, within } from '@testing-library/react'
import { afterEach, expect, it, vi } from 'vitest'
import PosDashboardContent from './PosDashboardContent'
import { useAuthStore } from '../store/authStore'

const state = vi.hoisted(() => ({ stats: {} }))
vi.mock('react-router-dom', () => ({ useNavigate: () => vi.fn() }))
vi.mock('react-i18next', () => ({ useTranslation: () => ({ t: key => key }) }))
vi.mock('../hooks/useRTL', () => ({ useRTL: () => ({ locale: 'en-US', dir: 'ltr' }) }))
vi.mock('../contexts/SettingsContext', () => ({ useSettings: () => ({ formatCurrency: value => `MONEY ${value}` }) }))
vi.mock('./StorefrontStatusCard', () => ({ default: () => null }))
vi.mock('./dashboard/usePosDashboardData', () => ({
    useDashboardActorScope: () => 'actor',
    default: () => {
        const source = { data: [], pending: false, error: null }
        return { stats: { ...source, data: state.stats }, trend: source, products: source, sales: source, stock: source, categories: source, payments: source }
    },
}))
afterEach(cleanup)
it.each([false, true])('only displays a complete inventory headline (complete=%s)', complete => {
    useAuthStore.setState({ user: { id: 'a', effectivePermissions: ['dashboard.view', 'finance.view'] } })
    state.stats = { inventoryValue: 321, inventoryValuationComplete: complete }
    render(<PosDashboardContent />)
    const metric = screen.getByText('dashboard.inventoryValue').closest('section')
    if (complete) expect(within(metric).getByText('MONEY 321')).toBeVisible()
    else {
        expect(screen.queryByText('MONEY 321')).not.toBeInTheDocument()
        expect(screen.getByText('dashboard.phase42.incompleteValuation')).toBeVisible()
    }
})
