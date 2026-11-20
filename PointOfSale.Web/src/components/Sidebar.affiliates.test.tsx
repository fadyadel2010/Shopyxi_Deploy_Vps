import { act, fireEvent, render, screen, within } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { createRoutesFromChildren, matchRoutes, MemoryRouter, Route, Routes, useLocation } from 'react-router-dom'
import { isValidElement } from 'react'
import { appRouteElements } from '../App'
import PosPermissionRouteGuard from './PosPermissionRouteGuard'
import Sidebar, { buildAdminMenu } from './Sidebar'
import { useAuthStore } from '../store/authStore'
import i18n from '../i18n/config'

const actor = { id: 'p54d-owner', tenantId: 'p54d-store', role: 'Owner', effectivePermissions: ['affiliates.manage'], navigationCapabilities: [] }
const paths = ['/admin/affiliates', '/admin/affiliates/products']
function Location() { return <output data-testid="route">{useLocation().pathname}</output> }
const open = (path: string, onClose = vi.fn()) => render(<MemoryRouter initialEntries={[path]}><Sidebar isOpen onClose={onClose} /><Location /></MemoryRouter>)

beforeEach(async () => {
    localStorage.clear()
    await i18n.changeLanguage('en')
    useAuthStore.setState({ user: actor, isAuthenticated: true, hasHydrated: true })
})
afterEach(() => useAuthStore.setState({ user: null, isAuthenticated: false, hasHydrated: true }))

describe('Affiliate System sidebar', () => {
    it.each(['en', 'ar'])('has exactly two unique destinations in an independent localized group (%s)', async language => {
        await i18n.changeLanguage(language)
        const menu = buildAdminMenu((key, fallback) => ({ key, fallback }), actor)
        const group = menu.find(node => node.key === 'affiliate-system')!
        expect(i18n.t(group.label.key)).toBe(language === 'en' ? 'Affiliate System' : 'نظام العمولة')
        expect(group.children?.map(child => child.path)).toEqual(paths)
        expect(group.children?.map(child => i18n.t(child.label.key))).toEqual(language === 'en' ? ['Affiliate Management', 'Affiliate Products'] : ['إدارة المسوقين بالعمولة', 'منتجات الأفلييت'])
        const collect = (nodes: typeof menu): string[] => nodes.flatMap(node => [...(node.path ? [node.path] : []), ...collect(node.children || [])])
        for (const path of paths) expect(collect(menu).filter(value => value === path)).toHaveLength(1)
        expect(collect(menu.find(node => node.key === 'commercehub')!.children || [])).not.toContain(paths[0])
        expect(collect(menu.find(node => node.key === 'commercehub')!.children || [])).not.toContain(paths[1])
        expect(group.permission).toBe('affiliates.manage')
        expect(group.children?.every(child => child.permission === 'affiliates.manage')).toBe(true)
    })

    it.each(paths)('expands and highlights only the correct child on direct navigation and remount: %s', path => {
        const first = open(path)
        expect(screen.getByRole('button', { name: 'Affiliate System' })).toHaveAttribute('aria-expanded', 'true')
        expect(document.querySelectorAll('#sidebar-submenu-affiliate-system a[aria-current="page"]')).toHaveLength(1)
        expect(document.querySelector('#sidebar-submenu-affiliate-system a[aria-current="page"]')).toHaveAttribute('href', path)
        fireEvent.click(screen.getByRole('button', { name: 'Affiliate System' }))
        expect(document.getElementById('sidebar-submenu-affiliate-system')).toHaveAttribute('inert')
        first.unmount()
        open(path)
        expect(screen.getByRole('button', { name: 'Affiliate System' })).toHaveAttribute('aria-expanded', 'true')
        expect(screen.getByRole('button', { name: 'Affiliate System' }).className).toContain('pos-sidebar__item--context')
    })

    it('updates bilingual labels in place, navigates and preserves existing close behavior', async () => {
        const onClose = vi.fn()
        open(paths[0], onClose)
        const parent = screen.getByRole('button', { name: 'Affiliate System' })
        parent.focus()
        expect(parent).toHaveFocus()
        await act(() => i18n.changeLanguage('ar'))
        expect(screen.getByRole('button', { name: 'نظام العمولة' })).toHaveAttribute('aria-expanded', 'true')
        expect(screen.getByTestId('route')).toHaveTextContent(paths[0])
        fireEvent.click(within(document.getElementById('sidebar-submenu-affiliate-system')!).getByRole('link', { name: 'منتجات الأفلييت' }))
        expect(screen.getByTestId('route')).toHaveTextContent(paths[1])
        expect(onClose).toHaveBeenCalledOnce()
        expect(screen.getByRole('link', { name: 'منتجات الأفلييت' })).toHaveAttribute('aria-current', 'page')
        expect(screen.getByRole('link', { name: 'إدارة المسوقين بالعمولة' })).not.toHaveAttribute('aria-current')
    })

    it('does not reveal either destination without the existing backend-derived permission', () => {
        useAuthStore.setState({ user: { ...actor, role: 'Cashier', effectivePermissions: ['sales.view'] } })
        open(paths[1])
        expect(screen.queryByRole('button', { name: 'Affiliate System' })).not.toBeInTheDocument()
        for (const path of paths) expect(document.querySelector(`a[href="${path}"]`)).toBeNull()
    })

    it.each(paths)('retains the actual application permission boundary for direct route %s', path => {
        const matches = matchRoutes(createRoutesFromChildren(appRouteElements()), path)!
        const guarded = matches.map(match => match.route.element).find(element => isValidElement<{ permission?: string }>(element)
            && element.type === PosPermissionRouteGuard && element.props.permission === 'affiliates.manage')
        expect(guarded).toBeTruthy()
        const renderRoute = () => render(<MemoryRouter initialEntries={[path]}><Routes>
            <Route path="/" element={<p>Denied destination</p>} />
            <Route element={guarded}><Route path={path} element={<p>Authorized affiliate screen</p>} /></Route>
        </Routes></MemoryRouter>)
        const authorized = renderRoute()
        expect(screen.getByText('Authorized affiliate screen')).toBeInTheDocument()
        authorized.unmount()
        useAuthStore.setState({ user: { ...actor, effectivePermissions: [] } })
        renderRoute()
        expect(screen.queryByText('Authorized affiliate screen')).not.toBeInTheDocument()
        expect(screen.getByText('Denied destination')).toBeInTheDocument()
    })
})
