import { act, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { beforeEach, expect, it, vi } from 'vitest'
import '../design-system/test/radixJsdomPolyfills'
import Categories from './Categories'
import i18n from '../i18n/config'
import { ConfirmDialogProvider } from '../common/confirm/ConfirmDialogProvider'

const api = vi.hoisted(() => ({ get: vi.fn(), post: vi.fn(), put: vi.fn(), delete: vi.fn() }))
vi.mock('../services/api', () => ({ default: api }))
vi.mock('../hooks/usePosSessionPermission', () => ({ useHasPermission: () => true }))
beforeEach(async () => {
    vi.clearAllMocks()
    await i18n.changeLanguage('en')
    api.get.mockResolvedValue({ data: [{ id: 'parent', name: 'Parent', children: [{ id: 'child', name: 'Child' }] }] })
})

it.each(['en', 'ar'])('uses the same logical tree spacing and exposes expanded state in %s', async language => {
    await i18n.changeLanguage(language)
    render(<ConfirmDialogProvider><Categories /></ConfirmDialogProvider>)
    const child = await screen.findByText('Child')
    expect(child.closest('.border-b')).toHaveClass('ms-6', 'border-s-2')
    const toggle = screen.getByRole('button', { expanded: true })
    expect(toggle).toHaveAttribute('aria-controls')
    fireEvent.click(toggle)
    expect(toggle).toHaveAttribute('aria-expanded', 'false')
    expect(screen.queryByText('Child')).toBeNull()
})

it('contains editor focus, preserves unsaved input on Escape, and excludes duplicate saves', async () => {
    let resolve!: (value: unknown) => void
    api.post.mockReturnValue(new Promise(done => { resolve = done }))
    render(<ConfirmDialogProvider><Categories /></ConfirmDialogProvider>)
    await screen.findByText('Parent')
    const opener = screen.getByRole('button', { name: `+ ${i18n.t('categories.addCategory')}` })
    opener.focus()
    fireEvent.click(opener)
    const dialog = await screen.findByRole('dialog')
    const name = within(dialog).getByLabelText(i18n.t('categories.nameRequired'))
    await waitFor(() => expect(name).toHaveFocus())
    fireEvent.change(name, { target: { value: 'New category' } })
    fireEvent.keyDown(dialog, { key: 'Escape' })
    expect(name).toHaveValue('New category')
    const user = userEvent.setup()
    await user.tab({ shift: true })
    expect(dialog).toContainElement(document.activeElement as HTMLElement)
    act(() => {
        fireEvent.submit(dialog.querySelector('form')!)
        fireEvent.submit(dialog.querySelector('form')!)
    })
    expect(api.post).toHaveBeenCalledTimes(1)
    expect(within(dialog).getByRole('button', { name: i18n.t('common.cancel') })).toBeDisabled()
    await act(async () => { resolve({ data: {} }) })
    await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull())
    await waitFor(() => expect(opener).toHaveFocus())
})
