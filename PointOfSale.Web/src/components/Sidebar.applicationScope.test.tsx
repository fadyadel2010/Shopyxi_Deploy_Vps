import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { MemoryRouter, useNavigate } from 'react-router-dom'
import Sidebar from './Sidebar'
import { useAuthStore } from '../store/authStore'
import { navigationOwner } from '../navigation/navigation.fixture'
import i18n from '../i18n/config'

const mountAt = (path: string) => render(
    <MemoryRouter initialEntries={[path]}>
        <Sidebar isOpen onClose={vi.fn()} />
    </MemoryRouter>,
)

function HistoryHarness() {
    const navigate = useNavigate()
    return (
        <>
            <Sidebar isOpen onClose={vi.fn()} />
            <button type="button" onClick={() => navigate(-1)}>Back</button>
            <button type="button" onClick={() => navigate(1)}>Forward</button>
        </>
    )
}

const branch = (name: string) => screen.queryByRole('button', { name })
const link = (href: string) => document.querySelector(`aside.pos-sidebar a[href="${href}"]`)

beforeEach(async () => {
    localStorage.clear()
    await i18n.changeLanguage('en')
    useAuthStore.setState({ user: navigationOwner, isAuthenticated: true, hasHydrated: true })
})

afterEach(() => {
    cleanup()
    useAuthStore.setState({ user: null, isAuthenticated: false, hasHydrated: true })
})

describe('application-scoped Sidebar', () => {
    it('shows POS Admin navigation only on POS routes and keeps child destinations', () => {
        mountAt('/')

        expect(link('/products')).not.toBeNull()
        expect(link('/treasury/hub')).not.toBeNull()
        expect(link('/marketplace')).not.toBeNull()
        expect(branch('Storefront')).toBeNull()
        expect(branch('CommerceHub')).toBeNull()
        expect(branch('Affiliate System')).toBeNull()
        expect(link('/storefront')).toBeNull()
        expect(link('/commercehub/orders')).toBeNull()
        expect(link('/admin/affiliates')).toBeNull()
    })

    it('shows Storefront navigation only on Storefront routes and keeps its hierarchy', () => {
        mountAt('/storefront')

        expect(branch('Storefront')).not.toBeNull()
        expect(branch('Design')).not.toBeNull()
        expect(branch('Operations')).not.toBeNull()
        expect(link('/storefront')).not.toBeNull()
        expect(link('/storefront/domains')).not.toBeNull()
        expect(link('/storefront/catalog/product-template')).not.toBeNull()
        expect(link('/ecommerce/orders')).not.toBeNull()
        expect(link('/ecommerce/payment-methods')).not.toBeNull()

        expect(branch('CommerceHub')).toBeNull()
        expect(branch('Affiliate System')).toBeNull()
        expect(link('/')).toBeNull()
        expect(link('/products')).toBeNull()
    })

    it('shows CommerceHub navigation only on CommerceHub routes and keeps its destinations', () => {
        mountAt('/commercehub/orders')

        expect(branch('CommerceHub')).not.toBeNull()
        expect(link('/commercehub/seller-application')).not.toBeNull()
        expect(link('/commercehub/orders')).not.toBeNull()
        expect(link('/admin/commercehub')).not.toBeNull()
        expect(link('/commercehub/finance')).not.toBeNull()

        expect(branch('Storefront')).toBeNull()
        expect(branch('Affiliate System')).toBeNull()
        expect(link('/')).toBeNull()
        expect(link('/products')).toBeNull()
    })

    it('shows Commission System navigation only on Commission routes and keeps its destinations', () => {
        mountAt('/admin/affiliates')

        expect(branch('Affiliate System')).not.toBeNull()
        expect(link('/admin/affiliates')).not.toBeNull()
        expect(link('/admin/affiliates/products')).not.toBeNull()

        expect(branch('Storefront')).toBeNull()
        expect(branch('CommerceHub')).toBeNull()
        expect(link('/')).toBeNull()
        expect(link('/products')).toBeNull()
    })

    it.each([
        ['/storefront/domains/synthetic-domain', 'Storefront'],
        ['/ecommerce/orders/synthetic-order', 'Storefront'],
        ['/analytics/attribution/models', 'Storefront'],
        ['/settings?tab=storefront', 'Storefront'],
        ['/commercehub/orders/synthetic-suborder', 'CommerceHub'],
        ['/admin/commercehub/earnings', 'CommerceHub'],
        ['/admin/affiliates/synthetic-affiliate', 'Affiliate System'],
        ['/treasury/account/AUDIT', null],
    ])('resolves direct deep link %s to the correct application Sidebar', (path, expectedRoot) => {
        mountAt(path)

        if (expectedRoot) {
            expect(branch(expectedRoot)).not.toBeNull()
        } else {
            expect(link('/treasury/hub')).not.toBeNull()
        }
        expect(document.querySelectorAll('aside.pos-sidebar')).toHaveLength(1)
    })

    it('follows Browser Back and Forward without leaking the previous application menu', async () => {
        render(
            <MemoryRouter initialEntries={['/', '/commercehub/orders']} initialIndex={1}>
                <HistoryHarness />
            </MemoryRouter>,
        )

        expect(branch('CommerceHub')).not.toBeNull()
        expect(branch('Storefront')).toBeNull()

        fireEvent.click(screen.getByRole('button', { name: 'Back' }))
        await waitFor(() => expect(link('/products')).not.toBeNull())
        expect(branch('CommerceHub')).toBeNull()
        expect(branch('Storefront')).toBeNull()

        fireEvent.click(screen.getByRole('button', { name: 'Forward' }))
        await waitFor(() => expect(branch('CommerceHub')).not.toBeNull())
        expect(branch('Storefront')).toBeNull()
        expect(link('/products')).toBeNull()
    })

    it('preserves the correct application after a remount and does not cross scopes', () => {
        const first = mountAt('/storefront')
        expect(branch('Storefront')).not.toBeNull()
        expect(branch('CommerceHub')).toBeNull()
        first.unmount()

        mountAt('/commercehub/orders')
        expect(branch('CommerceHub')).not.toBeNull()
        expect(branch('Storefront')).toBeNull()
    })

    it('keeps the existing permission predicate: the Storefront placeholder replaces unavailable links', () => {
        useAuthStore.setState({
            user: { ...navigationOwner, effectivePermissions: ['dashboard.view'], navigationCapabilities: [] },
            isAuthenticated: true,
            hasHydrated: true,
        })
        mountAt('/storefront')

        expect(document.querySelector('button.pos-sidebar__item--placeholder')).not.toBeNull()
        expect(link('/storefront')).toBeNull()
        expect(link('/ecommerce/orders')).toBeNull()
        expect(link('/products')).toBeNull()
        expect(link('/admin/affiliates')).toBeNull()
    })

    it('does not expose the Commission System Sidebar when its permission is absent', () => {
        useAuthStore.setState({
            user: { ...navigationOwner, effectivePermissions: ['dashboard.view'], navigationCapabilities: [] },
            isAuthenticated: true,
            hasHydrated: true,
        })
        mountAt('/admin/affiliates')

        expect(branch('Affiliate System')).toBeNull()
        expect(link('/admin/affiliates')).toBeNull()
        expect(link('/products')).toBeNull()
    })
})
