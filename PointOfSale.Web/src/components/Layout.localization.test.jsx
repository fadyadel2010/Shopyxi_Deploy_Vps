import { act, cleanup, render, screen } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { I18nextProvider } from 'react-i18next'
import { MemoryRouter } from 'react-router-dom'
import i18n from '../i18n/config'
import Layout from './Layout'
import HelpButton from '../admin/help/HelpButton'

const mocks = vi.hoisted(() => ({ role: 'Owner' }))
vi.mock('../store/authStore', () => ({ useAuthStore: () => ({ user: { role: mocks.role } }) }))
vi.mock('./Sidebar', () => ({ default: () => null }))
vi.mock('./EmailVerificationBanner', () => ({ default: () => null }))
vi.mock('./MarketplaceNotificationBell', () => ({ default: () => null }))
vi.mock('../admin/help/HelpDrawer', () => ({ default: () => null }))
vi.mock('../navigation/SmartNavigationProvider', () => ({ default: ({ children }) => children }))
vi.mock('../navigation/CommandPalette', () => ({ default: () => null }))
vi.mock('../navigation/NavigationBreadcrumbs', () => ({ default: () => null }))
afterEach(cleanup)

async function mount(language) {
    await act(async () => { await i18n.changeLanguage(language) })
    return render(<I18nextProvider i18n={i18n}><MemoryRouter initialEntries={['/admin/commercehub']}><Layout /></MemoryRouter></I18nextProvider>)
}

describe('shared Admin shell localization', () => {
    it.each([['ar', 'مالك', 'مساعدة', 'قريباً'], ['en', 'Owner', 'Help', 'Soon']])(
        'renders the existing Owner role and unavailable help in %s', async (language, role, help, soon) => {
            mocks.role = 'Owner'
            const { container } = await mount(language)
            expect(screen.getByText(role)).toBeInTheDocument()
            expect(screen.getByRole('button', { name: new RegExp(`${help}\\s*${soon}`) })).toHaveAttribute('title', i18n.t('common.helpUnavailable'))
            expect(container.firstChild).toHaveAttribute('dir', language === 'ar' ? 'rtl' : 'ltr')
        })

    it.each(['Owner', 'Admin', 'Manager', 'Cashier', 'Warehouse', 'unknown_role'])('never leaks the raw Arabic role %s', async role => {
        mocks.role = role
        await mount('ar')
        expect(screen.getByText(i18n.t(`userManagement.roles.${role}`, { defaultValue: i18n.t('common.unavailable') }))).toBeInTheDocument()
        expect(screen.queryByText(role)).not.toBeInTheDocument()
    })

    it('localizes the available help indicator using the same shared keys', async () => {
        await act(async () => { await i18n.changeLanguage('ar') })
        render(<I18nextProvider i18n={i18n}><HelpButton hasArticle /></I18nextProvider>)
        expect(screen.getByRole('button', { name: /مساعدة\s*متاح/ })).toHaveAttribute('title', 'مساعدة')
    })
})
