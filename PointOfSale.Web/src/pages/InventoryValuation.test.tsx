import { act, cleanup, fireEvent, render, screen } from '@testing-library/react'
import { afterEach, beforeEach, expect, it, vi } from 'vitest'
import InventoryValuation from './InventoryValuation'
import { useAuthStore } from '../store/authStore'

const mocks = vi.hoisted(() => ({ get: vi.fn(), error: vi.fn() }))
vi.mock('../services/api', () => ({ default: { get: mocks.get } }))
vi.mock('react-i18next', () => ({ useTranslation: () => ({ t: (key: string) => key }) }))
vi.mock('../contexts/SettingsContext', () => ({ useSettings: () => ({ formatCurrency: (n: number) => `EGP ${n}` }) }))
vi.mock('../components/Toast', () => ({ useToast: () => ({ showError: mocks.error }) }))
vi.mock('../components/BarChart', () => ({
    default: ({ formatValue }: { formatValue: (n: number) => string }) => <div>{formatValue(17)}</div>,
}))
const report = {
    totalInventoryValue: 123, warehouseInventoryValue: 100, inTransitValue: 23, inTransitQuantity: 2,
    valuationComplete: true, unvaluedInTransitLines: 0, totalProductsInStock: 1, lowStockItemsCount: 1,
    warehouseValuations: [{ warehouseId: 'w', warehouseName: 'Synthetic warehouse', totalValue: 100, itemCount: 1 }],
    lowStockItems: [{ productName: 'Synthetic product', warehouseName: 'Synthetic warehouse', currentStock: 0, minimumStock: 0 }],
}
beforeEach(() => {
    vi.clearAllMocks()
    useAuthStore.setState({ user: { id: 'synthetic-a', tenantId: 'a' } })
    mocks.get.mockResolvedValue({ data: report })
})
afterEach(cleanup)

it('formats totals and the chart using tenant currency, retaining warehouse position identity', async () => {
    render(<InventoryValuation />)
    await screen.findByText('EGP 123')
    expect(screen.getByText('EGP 17')).toBeInTheDocument()
    expect(screen.getByText('Synthetic product — Synthetic warehouse')).toBeInTheDocument()
})

it('shows incomplete transit as unavailable, not a confirmed zero or total', async () => {
    mocks.get.mockResolvedValue({ data: {
        ...report, totalInventoryValue: null, inTransitValue: null, valuationComplete: false, unvaluedInTransitLines: 1,
    } })
    render(<InventoryValuation />)
    await screen.findByRole('alert')
    expect(screen.queryByText('EGP null')).not.toBeInTheDocument()
    expect(screen.queryByText('EGP 123')).not.toBeInTheDocument()
    expect(screen.queryByText('EGP 0')).not.toBeInTheDocument()
    expect(screen.getByText('—')).toBeInTheDocument()
})

it('suppresses contradictory numeric complete totals while preserving the known warehouse subtotal', async () => {
    mocks.get.mockResolvedValue({ data: {
        ...report, valuationComplete: false, unvaluedInTransitLines: 2,
    } })
    render(<InventoryValuation />)
    await screen.findByRole('alert')
    expect(screen.queryByText('EGP 123')).toBeNull()
    expect(screen.queryByText(/EGP 23/)).toBeNull()
    expect(screen.getByText(/transferWorkflow.warehouseValue.*EGP 100/)).toBeInTheDocument()
    expect(screen.getByText('—')).toBeInTheDocument()
})

it.each([403, 500, 503])('renders a recoverable unavailable state for HTTP %s, never a zero report', async status => {
    mocks.get.mockRejectedValueOnce({ response: { status, data: { message: 'private detail' } } })
    render(<InventoryValuation />)
    await screen.findByText('inventoryValuation.failedToLoad')
    expect(screen.queryByText('private detail')).not.toBeInTheDocument()
    expect(screen.queryByText('inventoryValuation.allGood')).not.toBeInTheDocument()
    fireEvent.click(screen.getByRole('button', { name: 'common.retry' }))
    await screen.findByText('EGP 123')
})

it('rejects malformed report arrays rather than crashing or inventing an empty result', async () => {
    mocks.get.mockResolvedValue({ data: { ...report, warehouseValuations: null } })
    render(<InventoryValuation />)
    await screen.findByText('inventoryValuation.failedToLoad')
    expect(screen.queryByText('EGP 123')).not.toBeInTheDocument()
})

it('hides the previous actor report during render on an authority switch', async () => {
    render(<InventoryValuation />)
    await screen.findByText('EGP 123')
    mocks.get.mockReturnValue(new Promise(() => {}))
    act(() => useAuthStore.setState({ user: { id: 'synthetic-b', tenantId: 'b' } }))
    expect(screen.queryByText('EGP 123')).not.toBeInTheDocument()
    expect(screen.queryByText('Synthetic product — Synthetic warehouse')).not.toBeInTheDocument()
})
