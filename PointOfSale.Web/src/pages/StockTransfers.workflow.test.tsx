import { beforeEach, describe, expect, it, vi } from 'vitest'
import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import StockTransfers from './StockTransfers'

const state = vi.hoisted(() => ({
    get: vi.fn(), post: vi.fn(), prompt: vi.fn(), confirm: vi.fn(), success: vi.fn(), error: vi.fn(), invalidate: vi.fn(), approve: true,
    t: (key: string) => key,
}))
vi.mock('react-i18next', () => ({ useTranslation: () => ({ t: state.t }) }))
vi.mock('../services/api', () => ({ default: { get: state.get, post: state.post } }))
vi.mock('../components/Toast', () => ({ useToast: () => ({ showSuccess: state.success, showError: state.error }) }))
vi.mock('../common/confirm/ConfirmDialogProvider', () => ({ useConfirmDialog: () => ({ prompt: state.prompt, confirm: state.confirm }) }))
vi.mock('../hooks/useRTL', () => ({ useRTL: () => ({ dir: 'ltr', isRTL: false, locale: 'en' }) }))
vi.mock('../hooks/usePosSessionPermission', () => ({ useHasPermission: () => state.approve }))
vi.mock('../services/posRuntimeCache', () => ({ invalidatePosRuntimeCache: state.invalidate }))
vi.mock('../components/AsyncEntitySelect', () => ({ default: ({ onChange }: { onChange: (value: { id: string, name: string }) => void }) =>
    <button type="button" onClick={() => onChange({ id: 'product-qa', name: 'QA product' })}>Choose QA product</button> }))

const transfer = { id: 'transfer-1', transferNumber: 'QA-ERP-R10-20261113-TRANSFER', transferDate: '2026-11-13T00:00:00Z',
    fromWarehouse: { name: 'QA source' }, toWarehouse: { name: 'QA destination' }, status: 'Approved', workflowVersion: 4, inTransitQuantity: 0,
    items: [{ id: 'item-1', productName: 'QA product', quantity: 2 }] }
const setup = (status = 'Approved') => state.get.mockImplementation((url: string) => Promise.resolve({ data:
    url === '/Warehouses' ? [] : url.endsWith('/history') ? [{ id: 'event-1', version: 4, action: 'dispatch', reason: 'QA dispatched', createdBy: 'QA actor', detailsJson: '[{"Id":"movement-1","QuantityChange":-2}]' }] :
        url === '/StockTransfers/transfer-1' ? { ...transfer, status } : { items: [{ ...transfer, status, inTransitQuantity: status === 'Dispatched' ? 2 : 0 }] },
}))

beforeEach(() => {
    vi.clearAllMocks(); state.approve = true; setup()
    state.prompt.mockResolvedValue('QA custody confirmation')
    state.confirm.mockResolvedValue(true)
    state.post.mockResolvedValue({ data: {} })
})

describe('Transfer creation idempotency regression', () => {
    const prepare = async () => {
        state.get.mockImplementation((url: string) => Promise.resolve({ data: url === '/Warehouses'
            ? [{ id: 'source', name: 'QA source' }, { id: 'destination', name: 'QA destination' }]
            : { items: [] } }))
        render(<StockTransfers />)
        fireEvent.click(await screen.findByRole('button', { name: /stockTransfers.newTransfer/ }))
        const selects = await screen.findAllByRole('combobox')
        await waitFor(() => expect(screen.getAllByRole('option', { name: 'QA source' })).toHaveLength(2))
        fireEvent.change(selects[0], { target: { value: 'source' } })
        fireEvent.change(selects[1], { target: { value: 'destination' } })
        fireEvent.click(screen.getByRole('button', { name: 'stockTransfers.addItem' }))
        fireEvent.click(screen.getByRole('button', { name: 'Choose QA product' }))
    }

    it('sends the mandatory matching idempotency headers for actual form creation', async () => {
        await prepare()
        fireEvent.click(screen.getByRole('button', { name: 'stockTransfers.createRequest' }))
        await waitFor(() => expect(state.post).toHaveBeenCalledTimes(1))
        const [url, payload, options] = state.post.mock.calls[0]
        expect(url).toBe('/StockTransfers')
        expect(payload).toMatchObject({ fromWarehouseId: 'source', toWarehouseId: 'destination', items: [{ productId: 'product-qa', quantity: 1 }] })
        expect(options.headers['Idempotency-Key']).toEqual(expect.any(String))
        expect(options.headers['X-Request-Id']).toBe(options.headers['Idempotency-Key'])
    })

    it('does not auto-repeat a failed create and keeps the exact key for an explicit unchanged retry', async () => {
        state.post.mockRejectedValue({ response: { data: { message: 'QA lost response' } } })
        await prepare()
        fireEvent.click(screen.getByRole('button', { name: 'stockTransfers.createRequest' }))
        await waitFor(() => expect(state.error).toHaveBeenCalledWith('QA lost response'))
        expect(state.post).toHaveBeenCalledTimes(1)
        const first = state.post.mock.calls[0][2].headers['Idempotency-Key']
        fireEvent.click(screen.getByRole('button', { name: 'stockTransfers.createRequest' }))
        await waitFor(() => expect(state.post).toHaveBeenCalledTimes(2))
        expect(state.post.mock.calls[1][2].headers['Idempotency-Key']).toBe(first)
    })
})

describe('Transfer dispatch / receipt workflow', () => {
    it('dispatches only after an explicit reason and submits the displayed document version', async () => {
        render(<StockTransfers />)
        fireEvent.click(await screen.findByRole('button', { name: 'transferWorkflow.dispatch' }))
        await waitFor(() => expect(state.post).toHaveBeenCalledWith('/StockTransfers/transfer-1/dispatch', { version: 4, reason: 'QA custody confirmation' }))
        expect(state.prompt).toHaveBeenCalledWith(expect.objectContaining({ description: 'transferWorkflow.dispatchPolicy', minLength: 1 }))
        expect(state.post).toHaveBeenCalledTimes(1)
    })

    it('presents dispatched quantity as in transit and exposes receipt or source-restoring cancellation', async () => {
        setup('Dispatched'); render(<StockTransfers />)
        const receive = await screen.findByRole('button', { name: 'transferWorkflow.receive' })
        expect(screen.getByText('transferWorkflow.inTransit: 2')).toBeInTheDocument()
        expect(screen.queryByRole('button', { name: 'transferWorkflow.dispatch' })).not.toBeInTheDocument()
        expect(screen.getByRole('button', { name: 'transferWorkflow.cancel' })).toBeInTheDocument()
        fireEvent.click(receive)
        await waitFor(() => expect(state.post).toHaveBeenCalledWith('/StockTransfers/transfer-1/receive', { version: 4, reason: 'QA custody confirmation' }))
    })

    it('does not perform a write when custody confirmation is dismissed', async () => {
        state.prompt.mockResolvedValue(null); render(<StockTransfers />)
        fireEvent.click(await screen.findByRole('button', { name: 'transferWorkflow.dispatch' }))
        await waitFor(() => expect(state.prompt).toHaveBeenCalled())
        expect(state.post).not.toHaveBeenCalled()
    })

    it('reloads after a lost response without automatically repeating dispatch', async () => {
        state.post.mockRejectedValue({ response: { data: { message: 'QA uncertain response: verify custody' } } })
        render(<StockTransfers />)
        fireEvent.click(await screen.findByRole('button', { name: 'transferWorkflow.dispatch' }))
        await waitFor(() => expect(state.error).toHaveBeenCalledWith('QA uncertain response: verify custody'))
        await waitFor(() => expect(state.get.mock.calls.filter(([url]) => url === '/StockTransfers').length).toBeGreaterThan(1))
        expect(state.post).toHaveBeenCalledTimes(1)
        expect(state.invalidate).not.toHaveBeenCalled()
    })

    it('hides completed transfer reversal without inventory approval authority', async () => {
        setup('Completed'); state.approve = false; render(<StockTransfers />)
        await screen.findByRole('button', { name: transfer.transferNumber })
        expect(screen.queryByRole('button', { name: 'transferWorkflow.reverse' })).not.toBeInTheDocument()
    })

    it('shows immutable movement links in the source-document history', async () => {
        setup('Dispatched'); render(<StockTransfers />)
        fireEvent.click(await screen.findByRole('button', { name: transfer.transferNumber }))
        const details = await screen.findByRole('region', { name: 'transferWorkflow.details' })
        expect(details).toHaveTextContent('QA dispatched')
        expect(details).toHaveTextContent('movement-1')
        expect(details).toHaveTextContent('QA actor')
        expect(state.post).not.toHaveBeenCalled()
    })
})
