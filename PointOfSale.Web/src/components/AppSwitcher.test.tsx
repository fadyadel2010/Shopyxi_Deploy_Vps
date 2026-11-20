import { act } from 'react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { cleanup, render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter, useLocation } from 'react-router-dom'
import '../design-system/test/radixJsdomPolyfills'
import AppSwitcher from './AppSwitcher'
import { useAuthStore } from '../store/authStore'
import { navigationOwner } from '../navigation/navigation.fixture'
import i18n from '../i18n/config'

function Location() {
    return <output data-testid="route">{useLocation().pathname}{useLocation().search}</output>
}

const mount = (path = '/') => render(
    <MemoryRouter initialEntries={[path]}>
        <AppSwitcher />
        <Location />
    </MemoryRouter>,
)

const trigger = () => screen.getByRole('button', { name: i18n.t('applications.trigger') })

async function openMenu() {
    const user = userEvent.setup()
    await user.click(trigger())
    return { user, menu: await screen.findByRole('menu') }
}

const applicationLabel = (applicationId: string) => ({
    pos: i18n.t('smartNavigation.modules.pos'),
    storefront: i18n.t('storefront.ia.root'),
    commercehub: i18n.t('nav.commerceHub'),
    commission: i18n.t('nav.affiliateSystem'),
} as Record<string, string>)[applicationId]

const escapeRegExp = (value: string) => value.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')
const menuItem = (menu: HTMLElement, label: string) => within(menu).getByRole('menuitem', { name: new RegExp(escapeRegExp(label)) })

beforeEach(async () => {
    localStorage.clear()
    await act(async () => { await i18n.changeLanguage('en') })
    useAuthStore.setState({ user: navigationOwner, isAuthenticated: true, hasHydrated: true })
})

afterEach(() => {
    cleanup()
    useAuthStore.setState({ user: null, isAuthenticated: false, hasHydrated: true })
    vi.restoreAllMocks()
})

describe('Top Header application switcher', () => {
    it('exposes all four applications when the actor is authorized for each one', async () => {
        mount('/')
        const { menu } = await openMenu()

        for (const id of ['pos', 'storefront', 'commercehub', 'commission']) {
            expect(menuItem(menu, applicationLabel(id))).toBeInTheDocument()
        }
        expect(within(menu).getAllByRole('menuitem')).toHaveLength(4)
    })

    it('marks the application of the committed route as active', async () => {
        mount('/storefront')
        const { menu } = await openMenu()

        const active = menuItem(menu, applicationLabel('storefront'))
        expect(active).toHaveAttribute('aria-current', 'page')
        expect(within(active).getByText(i18n.t('common.active'))).toBeInTheDocument()
        expect(menuItem(menu, applicationLabel('commercehub'))).not.toHaveAttribute('aria-current')
    })

    it.each([
        ['storefront', '/storefront'],
        ['commercehub', '/admin/commercehub'],
        ['commission', '/admin/affiliates'],
    ])('navigates %s with its existing canonical landing route', async (applicationId, landingPath) => {
        mount('/')
        const { user, menu } = await openMenu()

        await user.click(menuItem(menu, applicationLabel(applicationId)))

        await waitFor(() => expect(screen.getByTestId('route')).toHaveTextContent(landingPath))
    })

    it('keeps the current page when the active application is selected', async () => {
        mount('/treasury')
        const { user, menu } = await openMenu()

        await user.click(menuItem(menu, applicationLabel('pos')))

        await waitFor(() => expect(screen.queryByRole('menu')).not.toBeInTheDocument())
        expect(screen.getByTestId('route')).toHaveTextContent('/treasury')
    })

    it('does not expose applications the actor cannot reach', async () => {
        useAuthStore.setState({
            user: { ...navigationOwner, effectivePermissions: ['dashboard.view'], navigationCapabilities: [] },
            isAuthenticated: true,
            hasHydrated: true,
        })
        mount('/')
        const { menu } = await openMenu()

        expect(within(menu).getAllByRole('menuitem')).toHaveLength(1)
        expect(menuItem(menu, applicationLabel('pos'))).toBeInTheDocument()
        expect(within(menu).queryByRole('menuitem', { name: new RegExp(escapeRegExp(applicationLabel('storefront'))) })).toBeNull()
        expect(within(menu).queryByRole('menuitem', { name: new RegExp(escapeRegExp(applicationLabel('commercehub'))) })).toBeNull()
        expect(within(menu).queryByRole('menuitem', { name: new RegExp(escapeRegExp(applicationLabel('commission'))) })).toBeNull()
    })

    it('keeps a deep link to an unauthorized application out of the switcher', async () => {
        useAuthStore.setState({
            user: { ...navigationOwner, effectivePermissions: ['dashboard.view'], navigationCapabilities: [] },
            isAuthenticated: true,
            hasHydrated: true,
        })
        mount('/admin/affiliates')
        const { menu } = await openMenu()

        expect(within(menu).queryByRole('menuitem', { name: new RegExp(escapeRegExp(applicationLabel('commission'))) })).toBeNull()
        expect(within(menu).getAllByRole('menuitem')).toHaveLength(1)
    })

    it('supports keyboard opening, arrow navigation and Escape with focus restoration', async () => {
        mount('/')
        const button = trigger()
        button.focus()
        const user = userEvent.setup()

        await user.keyboard('{Enter}')
        const menu = await screen.findByRole('menu')
        expect(button).toHaveAttribute('aria-expanded', 'true')

        await user.keyboard('{ArrowDown}')
        await waitFor(() => expect(document.activeElement).toHaveAttribute('role', 'menuitem'))
        expect(document.activeElement).toBeInstanceOf(HTMLElement)
        expect(menu.contains(document.activeElement)).toBe(true)

        await user.keyboard('{Escape}')
        await waitFor(() => expect(screen.queryByRole('menu')).toBeNull())
        expect(button).toHaveFocus()
        expect(button).toHaveAttribute('aria-expanded', 'false')
    })

    it.each([
        ['ar', 'rtl'],
        ['en', 'ltr'],
    ])('renders localized names and menu direction in %s', async (language, dir) => {
        await act(async () => { await i18n.changeLanguage(language) })
        mount('/')
        const { menu } = await openMenu()

        expect(screen.getByTestId('route')).toHaveTextContent('/')
        for (const id of ['pos', 'storefront', 'commercehub', 'commission']) {
            expect(menuItem(menu, applicationLabel(id))).toBeInTheDocument()
        }
        expect(menu).toHaveAttribute('dir', dir)
    })

    it('disables the trigger without a misleading empty menu when no application is reachable', () => {
        useAuthStore.setState({ user: null, isAuthenticated: false, hasHydrated: true })
        mount('/')

        expect(trigger()).toBeDisabled()
    })
})
