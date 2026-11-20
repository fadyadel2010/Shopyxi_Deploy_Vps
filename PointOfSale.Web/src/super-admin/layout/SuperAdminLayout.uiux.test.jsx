import { cleanup, render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter } from 'react-router-dom'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import '../../design-system/test/radixJsdomPolyfills'
import SuperAdminLayout from './SuperAdminLayout'

const locale = vi.hoisted(() => ({ ar: false }))
vi.mock('../i18n/useSuperAdminLocale', () => ({ useSuperAdminLocale: () => ({
    t: key => key, tr: (en, ar) => locale.ar ? ar : en, isArabic: locale.ar,
}) }))
vi.mock('../auth/superAdminAuthStore', () => ({ useSuperAdminAuthStore: () => ({ user: null, logout: vi.fn() }) }))
vi.mock('../auth/superAdminAuthService', () => ({ logoutSuperAdmin: vi.fn() }))
beforeEach(() => vi.stubGlobal('matchMedia', () => ({ matches: false, addEventListener: vi.fn(), removeEventListener: vi.fn() })))
afterEach(() => { cleanup(); vi.unstubAllGlobals() })
describe('UIUX Super Admin drawer', () => {
    it.each([false, true])('uses modal keyboard behavior without exposing capability links (Arabic=%s)', async ar => {
        locale.ar = ar
        render(<MemoryRouter initialEntries={['/super-admin/finance']}><SuperAdminLayout /></MemoryRouter>)
        const user = userEvent.setup()
        const trigger = screen.getByRole('button', { name: ar ? 'فتح القائمة' : 'Open sidebar' })
        await user.click(trigger)
        const dialog = await screen.findByRole('dialog')
        expect(trigger).toHaveAttribute('aria-expanded', 'true')
        const close = within(dialog).getByRole('button', { name: ar ? 'إغلاق القائمة' : 'Close sidebar' })
        expect(close).toHaveFocus()
        expect(within(dialog).queryByRole('link', { name: /superAdmin.navigation.finance$/ })).not.toBeInTheDocument()
        await user.keyboard('{Shift>}{Tab}{/Shift}')
        expect(dialog).toContainElement(document.activeElement)
        await user.keyboard('{Escape}')
        await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull())
        expect(trigger).toHaveFocus()
        expect(trigger).toHaveAttribute('aria-expanded', 'false')
    })
})
