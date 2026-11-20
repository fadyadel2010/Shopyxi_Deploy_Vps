import { act } from 'react'
import { useRef, useState } from 'react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import '../design-system/test/radixJsdomPolyfills'
import { MemoryRouter, useLocation } from 'react-router-dom'
import Sidebar from './Sidebar'
import { useAuthStore } from '../store/authStore'
import { navigationOwner } from '../navigation/navigation.fixture'
import i18n from '../i18n/config'

function requiredElement<T extends HTMLElement>(root: ParentNode, selector: string): T {
    const element = root.querySelector<T>(selector)
    if (!element) throw new Error(`Missing expected element: ${selector}`)
    return element
}

function Location() {
    return <output data-testid="route">{useLocation().pathname}{useLocation().search}</output>
}

const mount = (path = '/', props = {}) => render(
    <MemoryRouter initialEntries={[path]}>
        <Sidebar isOpen onClose={vi.fn()} {...props} />
        <Location />
    </MemoryRouter>,
)

const useMobileViewport = () => {
    const original = window.matchMedia
    window.matchMedia = (query) => ({
        matches: false,
        media: query,
        onchange: null,
        addEventListener: () => {},
        removeEventListener: () => {},
        addListener: () => {},
        removeListener: () => {},
        dispatchEvent: () => false,
    })
    return () => { window.matchMedia = original }
}

function MobileHarness() {
    const triggerRef = useRef(null)
    const [open, setOpen] = useState(false)
    return (
        <MemoryRouter initialEntries={['/']}>
            <button ref={triggerRef} type="button" onClick={() => setOpen(true)}>Open navigation trigger</button>
            <Sidebar isOpen={open} onClose={() => setOpen(false)} restoreFocusRef={triggerRef} />
            <Location />
        </MemoryRouter>
    )
}

describe('POS shell sidebar presentation and responsive behaviour', () => {
    beforeEach(async () => {
        localStorage.clear()
        await act(async () => { await i18n.changeLanguage('en') })
        useAuthStore.setState({
            user: {
                ...navigationOwner,
                firstName: 'Nour',
                lastName: 'Owner',
                email: 'owner@example.test',
                emailVerified: true,
            },
            isAuthenticated: true,
            hasHydrated: true,
        })
    })

    afterEach(() => {
        cleanup()
        useAuthStore.setState({ user: null, isAuthenticated: false, hasHydrated: true })
        vi.restoreAllMocks()
    })

    it('keeps every duplicated destination but exposes aria-current on the first eligible occurrence only', () => {
        mount('/treasury')

        const duplicates = [...document.querySelectorAll('a[href="/treasury"]')]
        expect(duplicates.length).toBeGreaterThan(1)

        const current = duplicates.filter(link => link.getAttribute('aria-current') === 'page')
        expect(current).toHaveLength(1)
        expect(current[0]).toBe(duplicates[0])
        expect(document.querySelectorAll('[aria-current="page"]')).toHaveLength(1)
    })

    it('renders the desktop rail with the official compact identity and no modal drawer', () => {
        mount('/')

        expect(document.querySelector('aside.pos-sidebar')).not.toBeNull()
        expect(screen.queryByRole('dialog')).toBeNull()
        expect(screen.getAllByRole('navigation', { name: i18n.t('nav.mainNavigation') })).toHaveLength(1)

        const brand = document.querySelector('.pos-sidebar__brand-name')
        expect(brand).toHaveTextContent(i18n.t('login.brandName'))
        expect(brand?.querySelector('bdi')).toHaveAttribute('dir', 'ltr')
        expect(document.querySelector('.pos-sidebar__brand-qualifier')).toHaveTextContent(i18n.t('login.productQualifier'))
        const symbol = document.querySelector('.pos-sidebar__symbol-plate img')
        expect(symbol).toHaveAttribute('alt', '')

        const paintedIcons = document.querySelectorAll('aside svg.lucide')
        expect(paintedIcons.length).toBeGreaterThan(0)
        expect(document.querySelector('aside')?.textContent).not.toMatch(/[\u{1F300}-\u{1FAFF}]/u)
    })

    it('navigates from a desktop link and invokes the close callback exactly once', async () => {
        const onClose = vi.fn()
        mount('/products/new/edit', { onClose })

        const link = requiredElement<HTMLAnchorElement>(document, '.pos-sidebar a[href="/products"]')
        expect(link).not.toBeNull()
        fireEvent.click(link)
        await waitFor(() => expect(screen.getByTestId('route')).toHaveTextContent('/products'))
        expect(onClose).toHaveBeenCalledOnce()
    })

    it('marks the verified dynamic parent as context without inventing aria-current', () => {
        mount('/treasury/account/AUDIT')

        const hub = document.querySelector('.pos-sidebar a[href="/treasury/hub"]')
        expect(hub?.closest('.pos-sidebar__item')).toHaveClass('pos-sidebar__item--context')
        expect(hub).not.toHaveAttribute('aria-current')
        expect(document.querySelectorAll('[aria-current="page"]')).toHaveLength(0)
    })

    it('keeps restricted Storefront placeholders as native, focusable buttons', async () => {
        useAuthStore.setState({
            user: { ...navigationOwner, effectivePermissions: ['dashboard.view'], navigationCapabilities: [] },
            isAuthenticated: true,
            hasHydrated: true,
        })
        const open = vi.spyOn(window, 'open').mockImplementation(() => null)
        mount('/storefront')

        const placeholder = requiredElement<HTMLButtonElement>(document, 'button.pos-sidebar__item--placeholder')
        expect(placeholder).not.toBeNull()
        expect(placeholder.tagName).toBe('BUTTON')
        expect(placeholder).toHaveAttribute('type', 'button')
        expect(placeholder).not.toHaveAttribute('role')
        expect(placeholder).not.toHaveAttribute('tabindex')

        for (let node = placeholder.parentElement; node; node = node.parentElement) {
            if (!node.id.startsWith('sidebar-submenu-') || !node.hasAttribute('inert')) continue
             const branch = requiredElement<HTMLButtonElement>(document, `button[aria-controls="${node.id}"]`)
            fireEvent.click(branch)
            await waitFor(() => expect(node).not.toHaveAttribute('inert'))
        }

        placeholder.focus()
        expect(placeholder).toHaveFocus()
        fireEvent.click(placeholder)
        expect(open).toHaveBeenCalledWith(expect.stringContaining('/support?context='), '_blank', 'noopener,noreferrer')
        expect(screen.getByTestId('route')).toHaveTextContent('/')
    })
})

describe('POS shell mobile navigation drawer', () => {
    let restore: () => void
    beforeEach(async () => {
        localStorage.clear()
        restore = useMobileViewport()
        await act(async () => { await i18n.changeLanguage('en') })
        useAuthStore.setState({ user: navigationOwner, isAuthenticated: true, hasHydrated: true })
    })
    afterEach(() => {
        cleanup()
        restore()
        useAuthStore.setState({ user: null, isAuthenticated: false, hasHydrated: true })
    })

    it('renders a single modal navigation instance, focuses it on open and restores focus on Escape', async () => {
        render(<MobileHarness />)
        const trigger = screen.getByRole('button', { name: 'Open navigation trigger' })
        trigger.focus()
        fireEvent.click(trigger)

        const dialog = await screen.findByRole('dialog')
        expect(dialog).toHaveClass('pos-shell-drawer')
        expect(dialog).toHaveAttribute('aria-modal', 'true')
        expect(document.querySelectorAll('aside.pos-sidebar')).toHaveLength(0)
        expect(document.querySelectorAll('nav[aria-label="Main navigation"]')).toHaveLength(1)

        const close = within(dialog).getByRole('button', { name: i18n.t('common.close') })
        await waitFor(() => expect(close).toHaveFocus())

        fireEvent.keyDown(document, { key: 'Escape' })
        await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull())
        await waitFor(() => expect(trigger).toHaveFocus())
    })

    it('navigates from the drawer, closes it and keeps a single aria-current owner', async () => {
        render(<MobileHarness />)
        fireEvent.click(screen.getByRole('button', { name: 'Open navigation trigger' }))
        const dialog = await screen.findByRole('dialog')

        const link = requiredElement<HTMLAnchorElement>(dialog, 'a[href="/units"]')
        expect(link).not.toBeNull()
        fireEvent.click(link)
        await waitFor(() => expect(screen.getByTestId('route')).toHaveTextContent('/units'))
        await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull())
    })
})
