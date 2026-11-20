import { afterEach, describe, expect, it } from 'vitest'
import { render, screen } from '@testing-library/react'
import { MemoryRouter, Route, Routes } from 'react-router-dom'
import i18n from '../i18n/config'
import ProtectedRoute from './ProtectedRoute'
import CommerceHubVendorRouteGuard from './CommerceHubVendorRouteGuard'
import { useAuthStore } from '../store/authStore'

const renderProtectedRestore = () => render(
    <MemoryRouter initialEntries={['/']}>
        <Routes>
            <Route element={<ProtectedRoute />}>
                <Route path="/" element={<div>Dashboard</div>} />
            </Route>
        </Routes>
    </MemoryRouter>,
)

const renderVendorValidation = () => render(
    <MemoryRouter initialEntries={['/storefront']}>
        <Routes>
            <Route path="/" element={<div>Dashboard</div>} />
            <Route element={<CommerceHubVendorRouteGuard capability="commercehub.storefront.manage" />}>
                <Route path="/storefront" element={<div>Storefront Administration</div>} />
            </Route>
        </Routes>
    </MemoryRouter>,
)

describe('route guard localization', () => {
    afterEach(async () => {
        await i18n.changeLanguage('en')
        useAuthStore.setState({
            user: null,
            isAuthenticated: false,
            hasHydrated: true,
            isRouteAuthorityValidating: false,
        })
    })

    it('renders the session restore state in English and Arabic', async () => {
        useAuthStore.setState({ hasHydrated: false })
        await i18n.changeLanguage('en')
        renderProtectedRestore()
        expect(screen.getByText('Restoring session...')).toBeInTheDocument()

        await i18n.changeLanguage('ar')
        expect(await screen.findByText(i18n.t('common.restoringSession'))).toBeInTheDocument()
        expect(screen.queryByText('Dashboard')).not.toBeInTheDocument()
    })

    it('renders the vendor authority status in English and Arabic without exposing the route', async () => {
        useAuthStore.setState({ hasHydrated: true, isRouteAuthorityValidating: true })
        await i18n.changeLanguage('en')
        renderVendorValidation()
        expect(screen.getByRole('status', { name: 'Validating vendor authority' })).toBeInTheDocument()

        await i18n.changeLanguage('ar')
        expect(await screen.findByRole('status', { name: i18n.t('common.validatingVendorAuthority') })).toBeInTheDocument()
        expect(screen.queryByText('Storefront Administration')).not.toBeInTheDocument()
    })
})
