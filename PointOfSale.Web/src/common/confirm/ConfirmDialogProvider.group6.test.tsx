import { act, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, expect, it, vi } from 'vitest'
import axe from 'axe-core'
import { ConfirmDialogProvider, useConfirmDialog } from './ConfirmDialogProvider'
import i18n from '../../i18n/config'

function Harness({ prompt = false }: { prompt?: boolean }) {
    const dialog = useConfirmDialog()
    return <button onClick={() => {
        if (prompt) void dialog.prompt({ title: i18n.t('common.confirm'), inputLabel: 'Note' })
        else void dialog.confirm()
    }}>Open</button>
}
afterEach(async () => { await i18n.changeLanguage('en') })

it('localizes shared default title/actions in Arabic without a new language authority', async () => {
    await i18n.changeLanguage('ar')
    render(<ConfirmDialogProvider><Harness /></ConfirmDialogProvider>)
    fireEvent.click(screen.getByRole('button', { name: 'Open' }))
    const dialog = await screen.findByRole('dialog')
    expect(dialog).toHaveAccessibleName(i18n.t('common.confirm'))
    expect(within(dialog).getByRole('button', { name: i18n.t('common.confirm') })).toBeInTheDocument()
    expect(dialog.parentElement).toHaveAttribute('dir', 'rtl')
})

it('focuses the prompt input, contains keyboard focus and restores the opener', async () => {
    render(<ConfirmDialogProvider><Harness prompt /></ConfirmDialogProvider>)
    const opener = screen.getByRole('button', { name: 'Open' })
    opener.focus()
    fireEvent.click(opener)
    const dialog = await screen.findByRole('dialog')
    await waitFor(() => expect(screen.getByLabelText('Note')).toHaveFocus())
    const user = userEvent.setup()
    for (let index = 0; index < 6; index++) await user.tab()
    expect(dialog).toContainElement(document.activeElement as HTMLElement)
    fireEvent.keyDown(window, { key: 'Escape' })
    await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull())
    expect(opener).toHaveFocus()
})

it('has no axe semantic/name violations in the mounted confirmation fixture', async () => {
    render(<ConfirmDialogProvider><Harness /></ConfirmDialogProvider>)
    fireEvent.click(screen.getByRole('button', { name: 'Open' }))
    const dialog = await screen.findByRole('dialog')
    // jsdom cannot measure rendered colors/geometry; those checks remain browser-only.
    const result = await axe.run(dialog, { rules: { 'color-contrast': { enabled: false } } })
    expect(result.violations).toEqual([])
})

it('retained ConfirmModal rejects synchronous duplicate activation even before a loading render', async () => {
    const { default: ConfirmModal } = await import('../../components/ConfirmModal')
    let settle!: () => void
    const mutate = vi.fn(() => new Promise<void>(resolve => { settle = resolve }))
    render(<ConfirmModal title="Delete item" confirmLabel="Delete" onConfirm={mutate} onCancel={vi.fn()} />)
    const confirm = screen.getByRole('button', { name: 'Delete' })
    act(() => { fireEvent.click(confirm); fireEvent.click(confirm) })
    expect(mutate).toHaveBeenCalledTimes(1)
    await act(async () => { settle() })
})
