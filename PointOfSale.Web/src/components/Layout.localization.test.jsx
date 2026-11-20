import { act, cleanup, render, screen } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { I18nextProvider } from 'react-i18next'
import { MemoryRouter } from 'react-router-dom'
import i18n from '../i18n/config'
import Layout from './Layout'
import HelpButton from '../admin/help/HelpButton'

const mocks = vi.hoisted(() => ({ role: 'Owner' }))
vi.mock('../store/authStore', () => ({ useAuthStore: () => ({ user: { role: mocks.role } }) }))
vi.mock('./Sidebar', async (importOriginal) => ({
    ...(await importOriginal()),
    default: () => null,
}))
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

    it.each([
        ['ar', 'تخطَّ إلى المحتوى الرئيسي'],
        ['en', 'Skip to main content'],
    ])('renders the bilingual skip link, one main target and compact branding in %s', async (language, skipLabel) => {
        mocks.role = 'Owner'
        const { container } = await mount(language)

        const skip = screen.getByRole('link', { name: skipLabel })
        expect(skip).toHaveAttribute('href', '#pos-main-content')
        expect(container.querySelector('.pos-shell__skip-link')).toBe(skip)
        expect(container.querySelector('a, button, input, select, textarea')).toBe(skip)

        const main = document.getElementById('pos-main-content')
        expect(main?.tagName).toBe('MAIN')
        expect(main).toHaveAttribute('tabindex', '-1')
        expect(container.querySelector('.pos-shell__topbar h1')).toBeNull()

        const brandName = container.querySelector('.pos-shell__brand-name')
        const qualifier = container.querySelector('.pos-shell__brand-qualifier')
        expect(brandName).toHaveTextContent(i18n.t('login.brandName'))
        expect(brandName).toHaveAttribute('dir', 'ltr')
        expect(qualifier).toHaveTextContent(i18n.t('login.productQualifier'))
        expect(qualifier).toHaveAttribute('dir', 'ltr')
        const symbol = container.querySelector('.pos-shell__brand-symbol img')
        expect(symbol).toHaveAttribute('alt', '')
        expect(container.querySelector('.pos-shell__brand-symbol')).toHaveAttribute('aria-hidden', 'true')

        expect(screen.getByRole('button', { name: i18n.t('app.logout') })).toHaveAttribute('title', i18n.t('app.logout'))
        expect(screen.getByRole('button', { name: i18n.t('settings.settings') })).toBeInTheDocument()

        const menu = screen.getByRole('button', { name: i18n.t('smartNavigation.openNavigation') })
        expect(menu).toHaveAttribute('aria-haspopup', 'dialog')
        expect(menu).toHaveAttribute('aria-expanded', 'false')

        const applications = screen.getByRole('button', { name: i18n.t('applications.trigger') })
        expect(container.querySelector('.pos-shell__lead')?.contains(applications)).toBe(true)
        expect(applications).toHaveAttribute('aria-haspopup', 'menu')
    })
})
