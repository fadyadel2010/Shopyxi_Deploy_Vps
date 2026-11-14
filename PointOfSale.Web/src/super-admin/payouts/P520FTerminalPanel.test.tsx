import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { P520FTerminalPanel } from './P520FTerminalPanel'

const api = vi.hoisted(() => ({ getRaw: vi.fn(), postRaw: vi.fn() }))
vi.mock('../core/superAdminApiClient', () => ({ superAdminApiClient: api }))

describe('separate P520F internal simulation controls', () => {
    beforeEach(() => vi.resetAllMocks())
    it('requires non-external simulation confirmation and never targets normal payout endpoints', async () => {
        api.postRaw.mockResolvedValue({ payout: { status: 'SimulatedCompleted', externalTransferHandoffToken: 'private-operation-token' }, externalMoneyMovementConfirmed: false })
        render(<P520FTerminalPanel />)
        const complete = screen.getByRole('button', { name: 'Complete internal simulated payout' })
        expect(complete).toBeDisabled()
        fireEvent.change(screen.getByRole('textbox', { name: 'Simulation vendor ID' }), { target: { value: 'owned-vendor' } })
        fireEvent.change(screen.getByRole('textbox', { name: 'Simulation earning IDs' }), { target: { value: 'owned-earning' } })
        fireEvent.change(screen.getByRole('textbox', { name: 'Simulation payout method ID' }), { target: { value: 'owned-method' } })
        fireEvent.click(screen.getByRole('checkbox'))
        fireEvent.click(complete)
        await waitFor(() => expect(screen.getByRole('status')).toHaveTextContent('SimulatedCompleted'))
        expect(screen.getByRole('status')).not.toHaveTextContent('private-operation-token')
        expect(api.postRaw).toHaveBeenCalledWith('/api/dev/commercehub/p520f/P520F-20261028-TERMINAL01/vendors/owned-vendor/payout/complete', expect.objectContaining({ earningIds: ['owned-earning'], payoutMethodId: 'owned-method' }))
        expect(api.postRaw.mock.calls[0][1]).not.toHaveProperty('externalTransferConfirmedExecuted')
    })
    it('shows disabled-runtime refusal without claiming completion', async () => {
        api.getRaw.mockRejectedValue(new Error('404: test clock disabled'))
        render(<P520FTerminalPanel />)
        fireEvent.click(screen.getByRole('checkbox'))
        fireEvent.click(screen.getByRole('button', { name: 'Read virtual clock' }))
        await waitFor(() => expect(screen.getByRole('status')).toHaveTextContent('test clock disabled'))
        expect(api.postRaw).not.toHaveBeenCalled()
    })
    it('uses expected and target virtual timestamps for monotonic server admission', async () => {
        api.postRaw.mockResolvedValue({ isSimulatedTime: true })
        render(<P520FTerminalPanel />)
        fireEvent.change(screen.getByRole('textbox', { name: 'Expected virtual UTC' }), { target: { value: '2026-10-28T00:00:00Z' } })
        fireEvent.change(screen.getByRole('textbox', { name: 'Target virtual UTC' }), { target: { value: '2026-11-04T00:00:01Z' } })
        fireEvent.click(screen.getByRole('checkbox'))
        fireEvent.click(screen.getByRole('button', { name: 'Advance virtual clock' }))
        await waitFor(() => expect(api.postRaw).toHaveBeenCalledWith(expect.stringMatching(/\/clock\/advance$/), { expectedUtc: '2026-10-28T00:00:00Z', targetUtc: '2026-11-04T00:00:01Z' }))
    })
})
