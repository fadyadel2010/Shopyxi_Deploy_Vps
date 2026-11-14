import { act, cleanup, fireEvent, render, screen } from '@testing-library/react'
import { afterEach, beforeEach, expect, it, vi } from 'vitest'
import GeographicCatalogPage from './GeographicCatalogPage'
import BostaGeographySetup from '../../modules/shipping-administration/components/BostaGeographySetup'
import { useSuperAdminAuthStore } from '../auth/superAdminAuthStore'

const api = vi.hoisted(() => ({ get: vi.fn(), post: vi.fn() }))
vi.mock('./geographicCatalogApi', () => ({ geographicCatalogApi: api }))
vi.mock('react-i18next', () => ({ useTranslation: () => ({ i18n: { language: 'en' }, t: (key: string) => key }) }))
beforeEach(() => {
    vi.resetAllMocks()
    useSuperAdminAuthStore.setState({ user: null, isAuthenticated: false, hasHydrated: true })
})
afterEach(cleanup)

it('does not mount the global editor without a platform session', () => {
    render(<GeographicCatalogPage />)
    expect(screen.queryByLabelText('Governed canonical catalog bootstrap')).not.toBeInTheDocument()
    expect(api.get).not.toHaveBeenCalled()
    expect(api.post).not.toHaveBeenCalled()
})

it('does not equate a tenant Owner role with platform authority', () => {
    useSuperAdminAuthStore.setState({ isAuthenticated: true, user: {
        superAdminId: 'tenant-owner', email: 'synthetic@example.invalid', role: 'Owner',
        authType: 'super_admin', navigationCapabilities: ['super-admin.finance.view'],
    } })
    render(<GeographicCatalogPage />)
    expect(screen.queryByLabelText('Governed canonical catalog bootstrap')).not.toBeInTheDocument()
    expect(api.post).not.toHaveBeenCalled()
})

it('clears a platform draft on logout rather than leaking it into the next identity', () => {
    useSuperAdminAuthStore.setState({ isAuthenticated: true, user: {
        superAdminId: 'platform-editor', email: 'synthetic@example.invalid', role: 'SuperAdmin',
        authType: 'super_admin', navigationCapabilities: ['super-admin.finance.view'],
    } })
    render(<GeographicCatalogPage />)
    fireEvent.change(screen.getByLabelText('Canonical catalog JSON manifest'), { target: { value: 'synthetic-unsaved-draft' } })
    act(() => useSuperAdminAuthStore.getState().logout({ broadcast: false }))
    expect(screen.queryByDisplayValue('synthetic-unsaved-draft')).not.toBeInTheDocument()
    expect(screen.queryByLabelText('Governed canonical catalog bootstrap')).not.toBeInTheDocument()
    expect(api.post).not.toHaveBeenCalled()
})

it('keeps tenant Bosta tools available without exposing global catalog mutations', () => {
    render(<BostaGeographySetup accountId="tenant-carrier-account" environment="Sandbox" />)
    expect(screen.getByRole('button', { name: 'Load current geography reference' })).toBeEnabled()
    expect(screen.queryByLabelText('Governed canonical catalog bootstrap')).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Stage canonical import draft only' })).not.toBeInTheDocument()
    expect(api.post).not.toHaveBeenCalled()
})
