import { describe, expect, it, vi } from 'vitest'
import { act, fireEvent, render, screen, waitFor } from '@testing-library/react'
import '../design-system/test/radixJsdomPolyfills'
import api from '../services/api'
import SaleDetailsModal from './SaleDetailsModal'
import { getInvoiceStatus } from './invoiceAuthoring'

vi.mock('react-i18next', () => ({ useTranslation: () => ({ t: (key: string) => key, i18n: { dir: () => 'ltr' } }) }))
vi.mock('../contexts/SettingsContext', () => ({ useSettings: () => ({ formatCurrency: (value: number) => value.toFixed(2) }) }))
vi.mock('./ReceiptModal', () => ({ default: () => null }))
vi.mock('./ReturnModal', () => ({ default: () => null }))
vi.mock('../services/api', () => ({ default: { get: vi.fn() } }))

const sale = {
    id: 'sale-1', invoiceNumber: 'QA-ERP-SALE', invoiceDate: '2026-11-12T00:00:00Z',
    subtotal: 10, taxAmount: 1, totalAmount: 11, paymentMethod: 'Cash',
    items: [{ id: 'line-1', productName: 'Original invoice product', quantity: 1, unitPrice: 10, totalAmount: 11,
        productVariant: { sku: 'QA-ERP-SKU' } }],
}

describe('sale details response contract', () => {
    it('names the dialog and retains a localized receipt failure without duplicate requests', async () => {
        let fail!: (error: unknown) => void
        vi.mocked(api.get).mockReturnValue(new Promise((_resolve, reject) => { fail = reject }))
        render(<SaleDetailsModal sale={sale} onClose={vi.fn()} onReturnProcessed={vi.fn()} />)
        expect(screen.getByRole('dialog')).toHaveAccessibleName('saleDetailsModal.title')
        const print = screen.getByRole('button', { name: 'saleDetailsModal.reprintReceipt' })
        act(() => { fireEvent.click(print); fireEvent.click(print) })
        expect(api.get).toHaveBeenCalledTimes(1)
        await act(async () => { fail(new Error('private diagnostic')) })
        expect(await screen.findByRole('alert')).toHaveTextContent('saleDetailsModal.failedToLoadReceipt')
        await waitFor(() => expect(print).toBeEnabled())
        expect(screen.queryByText('private diagnostic')).toBeNull()
    })
    it.each(['FullyReturned', 4])('renders original product and server-returned status %s without offering another return', status => {
        render(<SaleDetailsModal sale={{ ...sale, status }} onClose={vi.fn()} onReturnProcessed={vi.fn()} />)
        expect(screen.getByText('Original invoice product')).toBeInTheDocument()
        expect(screen.getByText('QA-ERP-SKU')).toBeInTheDocument()
        expect(screen.getByText('salesHistory.status.FullyReturned')).toBeInTheDocument()
        expect(screen.queryByText('saleDetailsModal.unknownProduct')).not.toBeInTheDocument()
        expect(screen.queryByRole('button', { name: 'saleDetailsModal.processReturn' })).not.toBeInTheDocument()
        expect(screen.getByRole('button', { name: 'saleDetailsModal.reprintReceipt' })).toBeEnabled()
    })

    it.each(['Completed', 'PartiallyReturned', 1, 3])('retains return controls for refundable status %s', status => {
        render(<SaleDetailsModal sale={{ ...sale, status }} onClose={vi.fn()} onReturnProcessed={vi.fn()} />)
        expect(screen.getByRole('button', { name: 'saleDetailsModal.processReturn' })).toBeEnabled()
    })

    it.each(['Draft', 'Cancelled', 'Unexpected', undefined])('does not infer completion from a positive amount for %s', status => {
        render(<SaleDetailsModal sale={{ ...sale, status }} onClose={vi.fn()} onReturnProcessed={vi.fn()} />)
        expect(screen.queryByRole('button', { name: 'saleDetailsModal.processReturn' })).not.toBeInTheDocument()
        expect(screen.queryByText('salesHistory.status.Completed')).not.toBeInTheDocument()
    })

    it.each(['Draft', 'Completed', 'Cancelled', 'PartiallyReturned', 'FullyReturned'])('normalizes enum identity %s', status => {
        expect(getInvoiceStatus(status)).toBe(status)
    })

    it.each([-1, 5, 1.5, null, {}, 'not-a-status'])('does not manufacture status for invalid input %s', status => {
        expect(getInvoiceStatus(status)).toBeUndefined()
    })
})
