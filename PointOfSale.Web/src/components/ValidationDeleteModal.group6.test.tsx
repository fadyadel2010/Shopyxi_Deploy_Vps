import { act, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import { useState } from 'react'
import { beforeEach, expect, it, vi } from 'vitest'
import userEvent from '@testing-library/user-event'
import '././../design-system/test/radixJsdomPolyfills'
import ValidationDeleteModal from './ValidationDeleteModal'
import i18n from '../i18n/config'

const api = vi.hoisted(() => ({ get: vi.fn(), put: vi.fn(), delete: vi.fn() }))
vi.mock('../services/api', () => ({ default: api }))
function Harness() {
    const [open, setOpen] = useState(false)
    return <><button onClick={() => setOpen(true)}>Open delete</button><ValidationDeleteModal isOpen={open}
        onClose={() => setOpen(false)} entityType="product" entityId="one" entityName="Coffee" expectedVersion="1" /></>
}
beforeEach(async () => {
    vi.clearAllMocks()
    await i18n.changeLanguage('en')
    api.get.mockResolvedValue({ data: { canDelete: true } })
})

it('has a usable name while checking, contains focus and Cancel issues no mutation', async () => {
    api.get.mockReturnValue(new Promise(() => {}))
    render(<Harness />)
    const opener = screen.getByRole('button', { name: 'Open delete' })
    opener.focus()
    fireEvent.click(opener)
    const dialog = await screen.findByRole('dialog')
    expect(dialog).toHaveAccessibleName(i18n.t('common.delete'))
    fireEvent.keyDown(dialog, { key: 'Escape' })
    await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull())
    expect(opener).toHaveFocus()
    expect(api.put).not.toHaveBeenCalled()
    expect(api.delete).not.toHaveBeenCalled()
})

it('keeps deactivation pending inside the modal and cannot dismiss or mutate twice', async () => {
    api.get.mockResolvedValue({ data: { canDelete: false, messageEn: 'In use' } })
    let settle!: () => void
    api.put.mockReturnValue(new Promise<void>(resolve => { settle = resolve }))
    render(<Harness />)
    fireEvent.click(screen.getByRole('button', { name: 'Open delete' }))
    const inactive = await screen.findByRole('button', { name: i18n.t('common.markInactive') })
    const dialog = screen.getByRole('dialog')
    act(() => { fireEvent.click(inactive); fireEvent.click(inactive) })
    expect(api.put).toHaveBeenCalledTimes(1)
    expect(within(dialog).getByRole('button', { name: i18n.t('common.close') })).toBeDisabled()
    fireEvent.keyDown(dialog, { key: 'Escape' })
    expect(dialog).toBeInTheDocument()
    await userEvent.setup().tab()
    expect(dialog).toContainElement(document.activeElement as HTMLElement)
    await act(async () => { settle() })
    await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull())
})
