import { useRef, useState } from 'react'
import { act, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import { afterEach, beforeEach, expect, it, vi } from 'vitest'
import { MemoryRouter } from 'react-router-dom'
import '../design-system/test/radixJsdomPolyfills'
import Sidebar from './Sidebar'
import Dialog from '../design-system/primitives/Dialog/Dialog'
import { useAuthStore } from '../store/authStore'
import { navigationOwner } from '../navigation/navigation.fixture'
import i18n from '../i18n/config'

beforeEach(async () => {
    await i18n.changeLanguage('en')
    useAuthStore.setState({ user: navigationOwner, isAuthenticated: true, hasHydrated: true })
})
afterEach(() => { vi.unstubAllGlobals() })

it('closes the mobile drawer by its visible close control and restores the invoking control', async () => {
    vi.stubGlobal('matchMedia', vi.fn(query => ({
        matches: false, media: query, onchange: null,
        addEventListener: vi.fn(), removeEventListener: vi.fn(),
        addListener: vi.fn(), removeListener: vi.fn(), dispatchEvent: () => false,
    })))
    function Harness() {
        const [open, setOpen] = useState(false)
        const ref = useRef<HTMLButtonElement>(null)
        return <MemoryRouter><button ref={ref} onClick={() => setOpen(true)}>Open</button>
            <Sidebar isOpen={open} onClose={() => setOpen(false)} restoreFocusRef={ref} />
        </MemoryRouter>
    }
    render(<Harness />)
    const opener = screen.getByRole('button', { name: 'Open' })
    opener.focus()
    fireEvent.click(opener)
    const dialog = await screen.findByRole('dialog')
    fireEvent.click(within(dialog).getByRole('button', { name: i18n.t('common.close') }))
    await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull())
    await waitFor(() => expect(opener).toHaveFocus())
    vi.restoreAllMocks()
})

it('does not dismiss the existing Dialog while busy, including the displayed close control', async () => {
    const close = vi.fn()
    render(<Dialog open busy title="Pending action" onOpenChange={close}><button>Work</button></Dialog>)
    const dialog = await screen.findByRole('dialog')
    expect(dialog).toHaveAttribute('aria-busy', 'true')
    expect(within(dialog).getByRole('button', { name: i18n.t('common.close') })).toBeDisabled()
    await act(async () => { fireEvent.keyDown(dialog, { key: 'Escape' }) })
    expect(close).not.toHaveBeenCalled()
})
