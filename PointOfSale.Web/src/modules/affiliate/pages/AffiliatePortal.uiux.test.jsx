import { cleanup, render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { MemoryRouter } from 'react-router-dom'
import AffiliatePortal from './AffiliatePortal'
import { LanguageProvider } from '../i18n/LanguageContext'

vi.mock('../../../services/affiliateAuthApi', () => ({
    AFFILIATE_AUTH_SESSION_EVENT_KEY: 'synthetic-session-event',
    affiliateAuthApi: { restoreSession: async () => ({ affiliateAccountId: 'a', displayName: 'Test Affiliate' }), logout: vi.fn() },
    affiliateAxios: { get: vi.fn() },
}))
vi.mock('../components/LanguageSwitcher', () => ({ default: () => null }))
vi.mock('../components/ThemeSwitcher', () => ({ default: () => null }))
vi.mock('./portal/DashboardTab', () => ({ default: () => <h1>Dashboard fixture</h1> }))
beforeEach(() => { localStorage.setItem('affiliate_lang', 'en') })
afterEach(cleanup)
const open = () => render(<LanguageProvider><MemoryRouter><AffiliatePortal /></MemoryRouter></LanguageProvider>)
describe('UIUX external Affiliate navigation', () => {
    it.each([['en', 'Open navigation', 'Close navigation'], ['ar', 'فتح التنقل', 'إغلاق التنقل']])('contains and restores mobile focus in %s', async (lang, openLabel, closeLabel) => {
        localStorage.setItem('affiliate_lang', lang)
        open()
        const user = userEvent.setup()
        const trigger = await screen.findByRole('button', { name: openLabel })
        await user.click(trigger)
        const dialog = screen.getByRole('dialog')
        const close = within(dialog).getByRole('button', { name: closeLabel })
        expect(close).toHaveFocus()
        expect(dialog).toHaveAttribute('aria-modal', 'true')
        expect(document.body.style.overflow).toBe('hidden')
        await user.keyboard('{Shift>}{Tab}{/Shift}')
        expect(dialog).toContainElement(document.activeElement)
        await user.keyboard('{Tab}')
        expect(close).toHaveFocus()
        await user.keyboard('{Escape}')
        await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull())
        expect(trigger).toHaveFocus()
        expect(trigger).toHaveAttribute('aria-expanded', 'false')
        expect(document.body.style.overflow).not.toBe('hidden')
    })
    it('names collapsed desktop logout and exposes expansion state', async () => {
        open()
        const user = userEvent.setup()
        const collapse = await screen.findByRole('button', { name: 'Collapse' })
        expect(collapse).toHaveAttribute('aria-expanded', 'true')
        await user.click(collapse)
        expect(screen.getByRole('button', { name: 'Expand navigation' })).toHaveAttribute('aria-expanded', 'false')
        expect(screen.getByRole('button', { name: 'Logout' })).toBeInTheDocument()
    })
    it('closes on a navigation choice and restores focus', async () => {
        open()
        const user = userEvent.setup()
        const trigger = await screen.findByRole('button', { name: 'Open navigation' })
        await user.click(trigger)
        await user.click(within(screen.getByRole('dialog')).getByRole('button', { name: 'Dashboard' }))
        await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull())
        expect(trigger).toHaveFocus()
    })
})
