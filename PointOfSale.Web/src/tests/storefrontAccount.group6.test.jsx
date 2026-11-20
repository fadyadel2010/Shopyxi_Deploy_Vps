import { act, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { beforeEach, expect, it, vi } from 'vitest'
import { MemoryRouter } from 'react-router-dom'
import { ChangePasswordModal } from '@storefront/auth/components/ChangePasswordModal'
import { SessionsSection } from '@storefront/auth/components/SessionsSection'

const mocks = vi.hoisted(() => ({
    changePassword: vi.fn(), logout: vi.fn(), revokeAllSessions: vi.fn(), revokeSession: vi.fn(),
}))
// Navigation is outside these account-dialog contracts. Storefront has its own
// installed router/React; isolate that package instead of changing app providers.
vi.mock('../../../PointOfSale.Storefront/node_modules/react-router-dom', () => ({ useNavigate: () => vi.fn() }))
vi.mock('@storefront/auth/store/authStore', () => ({ useAuthStore: () => ({ logout: mocks.logout }) }))
vi.mock('@storefront/auth/hooks/useAccount', () => ({
    useAccount: () => ({ changePassword: mocks.changePassword, isLoading: false, error: null, clearError: () => {} }),
    useSessions: () => ({ sessions: [
        { id: 'one', isCurrent: true, device: 'Current', createdAt: '2026-11-20' },
        { id: 'two', isCurrent: false, device: 'Other', createdAt: '2026-11-20' },
    ], isLoading: false, error: null, revokeAllSessions: mocks.revokeAllSessions, revokeSession: mocks.revokeSession }),
}))
vi.mock('@storefront/i18n', async () => {
    const { t } = await import('@storefront/i18n/languageStore')
    return { useTranslation: () => ({ t: (key, params) => t(key, params, 'en'), direction: 'ltr' }) }
})
vi.mock('@storefront/store', async importOriginal => ({
    ...await importOriginal(), useStoreConfigStore: () => ({ ui: { buttonStyle: 'solid' } }),
}))
beforeEach(() => {
    vi.clearAllMocks()
    vi.spyOn(window, 'scrollTo').mockImplementation(() => {})
})

it('password Cancel writes nothing and repeated confirm cannot change the password twice', async () => {
    let complete
    mocks.changePassword.mockReturnValue(new Promise(resolve => { complete = resolve }))
    const close = vi.fn()
    render(<MemoryRouter><ChangePasswordModal isOpen onClose={close} /></MemoryRouter>)
    const dialog = screen.getByRole('dialog')
    expect(dialog).toHaveAccessibleName('Change Password')
    fireEvent.change(screen.getByLabelText('Current Password'), { target: { value: 'current-password' } })
    fireEvent.change(screen.getByLabelText('New Password'), { target: { value: 'new-password' } })
    fireEvent.change(screen.getByLabelText('Confirm Password'), { target: { value: 'new-password' } })
    fireEvent.submit(dialog.querySelector('form'))
    fireEvent.click(screen.getByRole('button', { name: 'Cancel' }))
    expect(mocks.changePassword).not.toHaveBeenCalled()
    expect(screen.getByLabelText('New Password')).toHaveValue('new-password')
    fireEvent.submit(dialog.querySelector('form'))
    const confirm = screen.getByRole('button', { name: 'Change Password' })
    act(() => { fireEvent.click(confirm); fireEvent.click(confirm) })
    expect(mocks.changePassword).toHaveBeenCalledTimes(1)
    fireEvent.keyDown(dialog, { key: 'Escape' })
    expect(close).not.toHaveBeenCalled()
    await act(async () => { complete({ success: false }) })
    expect(screen.getByLabelText('New Password')).toHaveValue('new-password')
})

it('session revoke-all has a named focus-contained dialog and pending cannot dismiss or repeat', async () => {
    let complete
    mocks.revokeAllSessions.mockReturnValue(new Promise(resolve => { complete = resolve }))
    render(<MemoryRouter><SessionsSection /></MemoryRouter>)
    const opener = screen.getByRole('button', { name: 'End All Other Sessions' })
    opener.focus()
    fireEvent.click(opener)
    const dialog = screen.getByRole('dialog')
    expect(dialog).toHaveAccessibleName('End All Other Sessions?')
    fireEvent.click(within(dialog).getByRole('button', { name: 'Cancel' }))
    expect(mocks.revokeAllSessions).not.toHaveBeenCalled()
    expect(opener).toHaveFocus()
    fireEvent.click(opener)
    const confirm = within(screen.getByRole('dialog')).getByRole('button', { name: 'End All Other Sessions' })
    act(() => { fireEvent.click(confirm); fireEvent.click(confirm) })
    expect(mocks.revokeAllSessions).toHaveBeenCalledTimes(1)
    await userEvent.setup().tab()
    expect(screen.getByRole('dialog')).toContainElement(document.activeElement)
    await act(async () => { complete({ success: true, revokedCount: 1 }) })
    await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull())
})
