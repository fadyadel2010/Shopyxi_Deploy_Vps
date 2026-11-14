import { beforeEach, describe, expect, it, vi } from 'vitest'
import { fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import StockCounts from './StockCounts'

const state = vi.hoisted(() => ({
    user: { role: 'Owner', effectivePermissions: ['inventory.view', 'inventory.adjust', 'inventory.approve', 'finance.operate', 'reports.financial.view'] },
    get: vi.fn(), post: vi.fn(), put: vi.fn(), invalidate: vi.fn(), t: (key: string) => key,
}))
vi.mock('react-i18next', () => ({ useTranslation: () => ({ t: state.t }) }))
vi.mock('../services/api', () => ({ default: { get: state.get, post: state.post, put: state.put }, isRequestCanceled: () => false }))
vi.mock('../store/authStore', () => ({ useAuthStore: (selector: (x: unknown) => unknown) => selector({ user: state.user }) }))
vi.mock('../hooks/useRTL', () => ({ useRTL: () => ({ dir: 'rtl' }) }))
vi.mock('../services/posRuntimeCache', () => ({ invalidatePosRuntimeCache: state.invalidate }))

const line = { id: 'line-1', variantId: 'variant-1', productName: 'QA product', sku: 'QA-SKU', systemQuantity: 10, reservedSnapshot: 2, damagedSnapshot: 1, unitCost: 5, countedQuantity: null, varianceReason: null, postedValue: 0 }
const session = { id: 'count-1', number: 'QA-ERP-R10-20261113-COUNT', warehouseId: 'warehouse-1', countDate: '2026-11-13', snapshotAtUtc: '2026-11-13T00:00:00Z', status: 0, version: 3, reason: 'Synthetic count', lines: [line] }
beforeEach(() => {
    vi.clearAllMocks()
    state.user = { role: 'Owner', effectivePermissions: ['inventory.view', 'inventory.adjust', 'inventory.approve', 'finance.operate', 'reports.financial.view'] }
    state.get.mockImplementation((url: string) => Promise.resolve({ data:
        url === '/Warehouses' ? [{ id: 'warehouse-1', name: 'QA Warehouse', isActive: true }] :
            url.endsWith('/variance-accounts') ? [{ id: 'expense-1', accountCode: '6010', name: 'QA variance' }] :
                url.endsWith('/variants') ? { items: [{ id: 'variant-1', productName: 'QA product', sku: 'QA-SKU' }], total: 1, pageSize: 50 } :
                    url === '/stock-counts/count-1' ? { session, history: [] } : { items: [session], total: 1, pageSize: 50 },
    }))
    state.post.mockResolvedValue({ data: session })
    state.put.mockResolvedValue({ data: session })
})

describe('Stock count sessions', () => {
    it('keeps blank counts uncounted rather than turning them into zero and saves explicit progress', async () => {
        render(<StockCounts />)
        fireEvent.click(await screen.findByRole('button', { name: session.number }))
        const quantity = await screen.findByRole('spinbutton', { name: 'stockCounts.counted QA-SKU' })
        expect(quantity).toHaveValue(null)
        fireEvent.change(quantity, { target: { value: '12' } })
        fireEvent.change(screen.getByRole('textbox', { name: 'stockCounts.reason QA-SKU' }), { target: { value: 'QA physical surplus' } })
        expect(screen.getByRole('button', { name: 'stockCounts.post' })).toBeDisabled()
        fireEvent.click(screen.getByRole('button', { name: 'stockCounts.save' }))
        await waitFor(() => expect(state.put).toHaveBeenCalledWith('/stock-counts/count-1/counts', { version: 3, lines: [{ lineId: 'line-1', countedQuantity: 12, reason: 'QA physical surplus' }] }))
        expect(state.post).not.toHaveBeenCalled()
    })

    it('requires explicit posting confirmation with version, reason and selected tenant variance account', async () => {
        render(<StockCounts />)
        fireEvent.click(await screen.findByRole('button', { name: session.number }))
        fireEvent.click(await screen.findByRole('button', { name: 'stockCounts.post' }))
        const dialog = screen.getByRole('dialog')
        fireEvent.change(within(dialog).getByLabelText('stockCounts.varianceAccount'), { target: { value: 'expense-1' } })
        fireEvent.change(within(dialog).getByRole('textbox', { name: 'stockCounts.reason' }), { target: { value: 'QA confirmation' } })
        expect(state.post).not.toHaveBeenCalled()
        fireEvent.click(within(dialog).getByRole('button', { name: 'stockCounts.confirm' }))
        await waitFor(() => expect(state.post).toHaveBeenCalledWith('/stock-counts/count-1/post', { version: 3, reason: 'QA confirmation', varianceAccountId: 'expense-1' }))
        expect(state.invalidate).toHaveBeenCalledTimes(1)
    })

    it('does not retry posting or reversal automatically after a conflict or lost response', async () => {
        state.post.mockRejectedValue({ response: { data: { message: 'STOCK_COUNT_STALE: recount required' } } })
        render(<StockCounts />)
        fireEvent.click(await screen.findByRole('button', { name: session.number }))
        fireEvent.click(await screen.findByRole('button', { name: 'stockCounts.post' }))
        fireEvent.change(screen.getByRole('textbox', { name: 'stockCounts.reason' }), { target: { value: 'QA confirmation' } })
        fireEvent.click(screen.getByRole('button', { name: 'stockCounts.confirm' }))
        expect(await screen.findByRole('alert')).toHaveTextContent('STOCK_COUNT_STALE')
        await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())
        expect(state.post).toHaveBeenCalledTimes(1)
        expect(state.invalidate).not.toHaveBeenCalled()
    })

    it('lets warehouse operators count but hides posting, reversal and account reads without finance authority', async () => {
        state.user = { role: 'Warehouse', effectivePermissions: ['inventory.view', 'inventory.adjust'] }
        render(<StockCounts />)
        fireEvent.click(await screen.findByRole('button', { name: session.number }))
        await screen.findByRole('region', { name: 'stockCounts.details' })
        expect(screen.getByRole('button', { name: 'stockCounts.create' })).toBeInTheDocument()
        expect(screen.getByRole('button', { name: 'stockCounts.cancel' })).toBeInTheDocument()
        expect(screen.queryByRole('button', { name: 'stockCounts.post' })).not.toBeInTheDocument()
        expect(state.get.mock.calls.some(([url]) => String(url).endsWith('/variance-accounts'))).toBe(false)
    })

    it('renders posted counts read-only and exposes only linked compensation', async () => {
        const original = state.get.getMockImplementation()!
        state.get.mockImplementation((url: string, ...args: unknown[]) => url === '/stock-counts/count-1'
            ? Promise.resolve({ data: { session: { ...session, status: 1, lines: [{ ...line, countedQuantity: 12, variance: 2, postedValue: 10 }] }, history: [] } }) : original(url, ...args))
        render(<StockCounts />)
        fireEvent.click(await screen.findByRole('button', { name: session.number }))
        expect(await screen.findByRole('button', { name: 'stockCounts.reverse' })).toBeInTheDocument()
        expect(screen.queryByRole('spinbutton')).not.toBeInTheDocument()
        expect(screen.queryByRole('button', { name: 'stockCounts.post' })).not.toBeInTheDocument()
        fireEvent.click(screen.getByRole('button', { name: 'stockCounts.reverse' }))
        fireEvent.change(screen.getByRole('textbox', { name: 'stockCounts.reason' }), { target: { value: 'QA compensation' } })
        fireEvent.click(screen.getByRole('button', { name: 'stockCounts.confirm' }))
        await waitFor(() => expect(state.post).toHaveBeenCalledWith('/stock-counts/count-1/reverse', { version: 3, reason: 'QA compensation' }))
    })

    it('creates an explicit warehouse/date/variant snapshot using a stable operation ID', async () => {
        render(<StockCounts />)
        fireEvent.click(screen.getByRole('button', { name: 'stockCounts.create' }))
        const dialog = screen.getByRole('dialog')
        fireEvent.click(await within(dialog).findByRole('checkbox', { name: 'QA product · QA-SKU' }))
        fireEvent.change(within(dialog).getByLabelText('stockCounts.warehouse'), { target: { value: 'warehouse-1' } })
        fireEvent.change(within(dialog).getByLabelText('stockCounts.date'), { target: { value: '2026-11-13' } })
        fireEvent.change(within(dialog).getByLabelText('stockCounts.reason'), { target: { value: 'QA synthetic count' } })
        fireEvent.click(within(dialog).getByRole('button', { name: 'stockCounts.confirm' }))
        await waitFor(() => expect(state.post).toHaveBeenCalledWith('/stock-counts', { operationId: expect.any(String), warehouseId: 'warehouse-1', countDate: '2026-11-13', variantIds: ['variant-1'], reason: 'QA synthetic count' }))
    })
})
