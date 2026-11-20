import { beforeEach, describe, expect, it, vi } from 'vitest'
import { fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import Units from './Units'
import { PosOverlayHost, PosOverlayProvider } from '../design-system/overlays/posOverlayContext'
import { expectNoAxeViolations } from '../design-system/test/axe'

const state = vi.hoisted(() => ({
    user: { effectivePermissions: ['catalog.manage', 'catalog.delete'] },
    dir: 'ltr',
    get: vi.fn(),
    post: vi.fn(),
    put: vi.fn(),
    delete: vi.fn(),
    confirm: vi.fn(),
    showSuccess: vi.fn(),
    showError: vi.fn(),
    t: (key: string) => key,
}))

vi.mock('react-i18next', () => ({ useTranslation: () => ({ t: state.t }) }))
vi.mock('../services/api', () => ({
    default: { get: state.get, post: state.post, put: state.put, delete: state.delete },
}))
vi.mock('../store/authStore', () => ({
    useAuthStore: (selector: (snapshot: { user: unknown }) => unknown) => selector({ user: state.user }),
}))
vi.mock('../hooks/useRTL', () => ({ useRTL: () => ({ dir: state.dir }) }))
vi.mock('../components/Toast', () => ({
    useToast: () => ({ showSuccess: state.showSuccess, showError: state.showError }),
}))
vi.mock('../common/confirm/ConfirmDialogProvider', () => ({
    useConfirmDialog: () => ({ confirm: state.confirm }),
}))

const unitRows = [
    { id: 'unit-1', name: 'Piece', symbol: 'pc', description: 'Single item', isActive: true },
    { id: 'unit-2', name: 'Kilogram', symbol: 'kg', description: '', isActive: false },
]

function renderUnits() {
    return render(
        <PosOverlayProvider>
            <Units />
            <PosOverlayHost />
        </PosOverlayProvider>,
    )
}

beforeEach(() => {
    vi.clearAllMocks()
    state.user = { effectivePermissions: ['catalog.manage', 'catalog.delete'] }
    state.dir = 'ltr'
    state.get.mockResolvedValue({ data: unitRows })
    state.post.mockResolvedValue({ data: {} })
    state.put.mockResolvedValue({ data: {} })
    state.delete.mockResolvedValue({ data: {} })
    state.confirm.mockResolvedValue(true)
})

describe('Units screen design-system integration', () => {
    it('loads the unit list from the unchanged API contract and renders status and fallbacks', async () => {
        renderUnits()

        expect(screen.getByRole('status')).toHaveTextContent('common.loading')

        const table = await screen.findByRole('table', { name: 'units.title' })
        expect(state.get).toHaveBeenCalledWith('/units?includeInactive=true')

        const pieceRow = within(table).getByRole('row', { name: /Piece/ })
        expect(within(pieceRow).getByText('pc')).toBeInTheDocument()
        expect(within(pieceRow).getByText('Single item')).toBeInTheDocument()
        expect(within(pieceRow).getByText('common.active')).toBeInTheDocument()

        const kilogramRow = within(table).getByRole('row', { name: /Kilogram/ })
        expect(within(kilogramRow).getByText('—')).toBeInTheDocument()
        expect(within(kilogramRow).getByText('common.inactive')).toBeInTheDocument()
    })

    it('shows the empty state and opens the create dialog from it', async () => {
        state.get.mockResolvedValue({ data: [] })
        renderUnits()

        expect(await screen.findByText('units.noUnits')).toBeInTheDocument()
        fireEvent.click(screen.getByRole('button', { name: 'units.addFirstUnit' }))

        expect(await screen.findByRole('dialog', { name: 'units.addNewUnit' })).toBeInTheDocument()
    })

    it('creates a unit with the unchanged payload and refreshes the list', async () => {
        renderUnits()
        await screen.findByRole('table', { name: 'units.title' })
        fireEvent.click(screen.getByRole('button', { name: 'units.addUnit' }))

        const dialog = await screen.findByRole('dialog', { name: 'units.addNewUnit' })
        const nameInput = within(dialog).getByRole('textbox', { name: /^units\.name/ })
        await waitFor(() => expect(nameInput).toHaveFocus())

        fireEvent.change(nameInput, { target: { value: 'Box' } })
        fireEvent.change(within(dialog).getByRole('textbox', { name: /^units\.symbol/ }), { target: { value: 'bx' } })
        fireEvent.change(within(dialog).getByRole('textbox', { name: /^units\.description/ }), { target: { value: 'Packing box' } })
        fireEvent.click(within(dialog).getByRole('button', { name: 'common.create' }))

        await waitFor(() => expect(state.post).toHaveBeenCalledWith('/units', {
            name: 'Box',
            symbol: 'bx',
            description: 'Packing box',
            isActive: true,
        }))
        expect(state.showSuccess).toHaveBeenCalledWith('units.created')
        await waitFor(() => expect(state.get).toHaveBeenCalledTimes(2))
        await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())
    })

    it('edits the selected unit with the unchanged payload', async () => {
        renderUnits()
        const table = await screen.findByRole('table', { name: 'units.title' })
        fireEvent.click(within(within(table).getByRole('row', { name: /Piece/ })).getByRole('button', { name: 'common.edit' }))

        const dialog = await screen.findByRole('dialog', { name: 'units.editUnit' })
        expect(within(dialog).getByRole('textbox', { name: /^units\.name/ })).toHaveValue('Piece')
        expect(within(dialog).getByRole('textbox', { name: /^units\.symbol/ })).toHaveValue('pc')
        expect(within(dialog).getByRole('textbox', { name: /^units\.description/ })).toHaveValue('Single item')

        fireEvent.change(within(dialog).getByRole('textbox', { name: /^units\.name/ }), { target: { value: 'Pieces' } })
        fireEvent.click(within(dialog).getByRole('button', { name: 'common.update' }))

        await waitFor(() => expect(state.put).toHaveBeenCalledWith('/units/unit-1', {
            name: 'Pieces',
            symbol: 'pc',
            description: 'Single item',
            isActive: true,
        }))
        expect(state.showSuccess).toHaveBeenCalledWith('units.updated')
    })

    it('deletes only after the existing danger confirmation and supports cancelling it', async () => {
        renderUnits()
        const table = await screen.findByRole('table', { name: 'units.title' })
        const row = within(table).getByRole('row', { name: /Piece/ })

        state.confirm.mockResolvedValueOnce(false)
        fireEvent.click(within(row).getByRole('button', { name: 'common.delete' }))
        await waitFor(() => expect(state.confirm).toHaveBeenCalledTimes(1))
        expect(state.delete).not.toHaveBeenCalled()

        fireEvent.click(within(row).getByRole('button', { name: 'common.delete' }))
        await waitFor(() => expect(state.delete).toHaveBeenCalledWith('/units/unit-1'))
        expect(state.confirm).toHaveBeenLastCalledWith({
            confirmLabel: 'common.delete',
            description: 'units.deleteConfirm',
            title: 'common.warning',
            variant: 'danger',
        })
        expect(state.showSuccess).toHaveBeenCalledWith('units.deleted')
    })

    it('seeds default units through the existing confirmation and endpoint', async () => {
        renderUnits()
        await screen.findByRole('table', { name: 'units.title' })
        fireEvent.click(screen.getByRole('button', { name: 'units.seedDefaults' }))

        await waitFor(() => expect(state.confirm).toHaveBeenCalledWith({
            confirmLabel: 'common.confirm',
            description: 'units.seedDefaultsConfirm',
            title: 'common.warning',
            variant: 'warning',
        }))
        await waitFor(() => expect(state.post).toHaveBeenCalledWith('/units/seed-defaults'))
        expect(state.showSuccess).toHaveBeenCalledWith('units.defaultsSeeded')
    })

    it('keeps the read-only projection when catalog mutations are not permitted', async () => {
        state.user = { effectivePermissions: [] }
        renderUnits()

        const table = await screen.findByRole('table', { name: 'units.title' })
        expect(within(table).getByText('Piece')).toBeInTheDocument()
        expect(screen.queryByRole('button', { name: 'units.addUnit' })).not.toBeInTheDocument()
        expect(screen.queryByRole('button', { name: 'units.seedDefaults' })).not.toBeInTheDocument()
        expect(within(table).queryByRole('button', { name: 'common.edit' })).not.toBeInTheDocument()
        expect(within(table).queryByRole('button', { name: 'common.delete' })).not.toBeInTheDocument()
    })

    it('honors the RTL document direction of the POS page', async () => {
        state.dir = 'rtl'
        renderUnits()
        await screen.findByRole('table', { name: 'units.title' })
        expect(screen.getByRole('heading', { name: 'units.title' }).closest('.pos-units')).toHaveAttribute('dir', 'rtl')
    })

    it('reports load failures through the existing error channel', async () => {
        state.get.mockRejectedValue(new Error('offline'))
        vi.spyOn(console, 'error').mockImplementation(() => {})
        renderUnits()

        await waitFor(() => expect(state.showError).toHaveBeenCalledWith('units.failedToLoad'))
        expect(await screen.findByText('units.noUnits')).toBeInTheDocument()
    })

    it('has no axe violations in the loaded list state', async () => {
        const { container } = renderUnits()
        await screen.findByRole('table', { name: 'units.title' })
        await expectNoAxeViolations(container)
    })
})
