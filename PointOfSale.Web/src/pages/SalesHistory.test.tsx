import { act, render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { beforeEach, expect, it, vi } from 'vitest'
import SalesHistory from './SalesHistory'
import { useAuthStore } from '../store/authStore'

const mocks = vi.hoisted(() => ({ get: vi.fn(), error: vi.fn(), t: (key: string) => key }))
vi.mock('../services/api', () => ({ default: { get: mocks.get } }))
vi.mock('react-i18next', () => ({ useTranslation: () => ({ t: mocks.t }) }))
vi.mock('../contexts/SettingsContext', () => ({ useSettings: () => ({ formatCurrency: String }) }))
vi.mock('../components/Toast', () => ({ useToast: () => ({ showError: mocks.error }) }))
vi.mock('../hooks/useRTL', () => ({ useRTL: () => ({ dir: 'ltr', isRTL: false, locale: 'en' }) }))
vi.mock('../components/SaleDetailsModal', () => ({ default: () => null }))

const sale = (number: string) => ({ id: number, invoiceNumber: number, invoiceDate: '2026-11-12', totalAmount: 11, status: 'FullyReturned' })
beforeEach(() => {
    vi.clearAllMocks()
    useAuthStore.setState({ user: { id: 'synthetic-a', tenantId: 'tenant-a' } })
    mocks.get.mockResolvedValue({ data: { items: [], totalPages: 0 } })
})

it('normalizes an empty result to page one and disables both pagination layouts', async () => {
    render(<SalesHistory />)
    await screen.findByText('salesHistory.noSalesFound')
    for (const button of screen.getAllByRole('button', { name: /salesHistory.next|salesHistory.previous/ })) {
        expect(button).toBeDisabled()
    }
    expect(mocks.get).toHaveBeenCalledTimes(1)
})

it('applies filters once on submit and resets pagination before querying', async () => {
    const user = userEvent.setup()
    mocks.get.mockResolvedValue({ data: { items: [sale('QA-1')], totalPages: 3 } })
    render(<SalesHistory />)
    await screen.findByText('QA-1')
    await user.click(screen.getAllByRole('button', { name: 'salesHistory.next' })[0])
    await waitFor(() => expect(mocks.get).toHaveBeenCalledTimes(2))
    await user.type(screen.getByLabelText('salesHistory.searchLabel'), 'QA-ERP-NO-MATCH')
    expect(mocks.get).toHaveBeenCalledTimes(2)
    mocks.get.mockResolvedValue({ data: { items: [], totalPages: 0 } })
    await user.click(screen.getByRole('button', { name: 'common.filter' }))
    await screen.findByText('salesHistory.noSalesFound')
    expect(mocks.get).toHaveBeenCalledTimes(3)
    expect(mocks.get.mock.lastCall?.[1].params).toEqual(expect.objectContaining({ page: 1, search: 'QA-ERP-NO-MATCH' }))
    for (const button of screen.getAllByRole('button', { name: 'salesHistory.next' })) expect(button).toBeDisabled()
})

it('ignores a superseded response even when the transport completes after abort', async () => {
    const user = userEvent.setup()
    let finishOld!: (result: unknown) => void
    mocks.get.mockReturnValueOnce(new Promise(resolve => { finishOld = resolve }))
    render(<SalesHistory />)
    await user.type(screen.getByLabelText('salesHistory.searchLabel'), 'new')
    mocks.get.mockResolvedValue({ data: { items: [sale('NEW')], totalPages: 1 } })
    await user.click(screen.getByRole('button', { name: 'common.filter' }))
    await screen.findByText('NEW')
    await act(async () => { finishOld({ data: { items: [sale('STALE')], totalPages: 4 } }) })
    expect(screen.queryByText('STALE')).not.toBeInTheDocument()
    expect(screen.getByText('NEW')).toBeInTheDocument()
    expect(mocks.error).not.toHaveBeenCalled()
})

it('shows an unavailable state with retry, never the empty-state copy, when the request fails', async () => {
    const user = userEvent.setup()
    mocks.get.mockRejectedValueOnce(new Error('offline'))
    render(<SalesHistory />)

    expect(await screen.findByText('salesHistory.unavailableTitle')).toBeInTheDocument()
    expect(screen.getByText('salesHistory.unavailableMessage')).toBeInTheDocument()
    expect(screen.queryByText('salesHistory.noSalesFound')).not.toBeInTheDocument()
    expect(mocks.error).toHaveBeenCalledWith('salesHistory.failedToLoad')

    mocks.get.mockResolvedValueOnce({ data: { items: [sale('RECOVERED')], totalPages: 1 } })
    await user.click(screen.getByRole('button', { name: 'common.retry' }))
    await screen.findByText('RECOVERED')
    expect(screen.queryByText('salesHistory.unavailableTitle')).not.toBeInTheDocument()
    expect(mocks.get).toHaveBeenCalledTimes(2)
})

it('distinguishes an authorization failure from genuinely empty history', async () => {
    mocks.get.mockRejectedValueOnce({ response: { status: 403 } })
    render(<SalesHistory />)

    expect(await screen.findByText('salesHistory.accessDenied')).toBeInTheDocument()
    expect(screen.getByText('salesHistory.accessDeniedMessage')).toBeInTheDocument()
    expect(screen.queryByText('salesHistory.noSalesFound')).not.toBeInTheDocument()
})

it('renders a successful non-empty history without failure or empty copy', async () => {
    mocks.get.mockResolvedValue({ data: { items: [sale('QA-2')], totalPages: 1 } })
    render(<SalesHistory />)

    await screen.findByText('QA-2')
    expect(screen.queryByText('salesHistory.unavailableTitle')).not.toBeInTheDocument()
    expect(screen.queryByText('salesHistory.noSalesFound')).not.toBeInTheDocument()
})

it('treats a malformed payload as unavailable rather than empty or zero', async () => {
    mocks.get.mockResolvedValue({ data: { items: null, totalPages: 0 } })
    render(<SalesHistory />)

    expect(await screen.findByText('salesHistory.unavailableTitle')).toBeInTheDocument()
    expect(screen.queryByText('salesHistory.noSalesFound')).not.toBeInTheDocument()
})

it('removes prior actor history immediately and ignores its pending response after a tenant switch', async () => {
    let finishOld!: (result: unknown) => void
    mocks.get.mockReturnValueOnce(new Promise(resolve => { finishOld = resolve }))
    render(<SalesHistory />)
    mocks.get.mockResolvedValue({ data: { items: [sale('TENANT-B')], totalPages: 1 } })
    act(() => useAuthStore.setState({ user: { id: 'synthetic-b', tenantId: 'tenant-b' } }))
    await screen.findByText('TENANT-B')
    await act(async () => { finishOld({ data: { items: [sale('TENANT-A-STALE')], totalPages: 1 } }) })
    expect(screen.queryByText('TENANT-A-STALE')).not.toBeInTheDocument()
    expect(screen.getByText('TENANT-B')).toBeInTheDocument()
})
