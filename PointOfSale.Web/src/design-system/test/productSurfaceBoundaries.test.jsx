import { fireEvent, render, screen } from '@testing-library/react'
import { MemoryRouter, Route, Routes } from 'react-router-dom'
import { describe, expect, it, vi } from 'vitest'
import { ToastProvider, useToast } from '../../components/Toast'
import { ConfirmDialogProvider, useConfirmDialog } from '../../common/confirm/ConfirmDialogProvider'
import { StorefrontWorkspaceBoundary } from '../../modules/storefront/StorefrontWorkspaceBoundary'
import CommerceHubVendorRouteGuard from '../../components/CommerceHubVendorRouteGuard'
import { PosSurfaceContext } from '../overlays/posOverlayContext'

vi.mock('../../store/authStore', () => ({
    useAuthStore: (selector) => selector({ user: { id: 'owner', tenantId: 'store' }, isRouteAuthorityValidating: false }),
}))
vi.mock('../../auth/posVendorSessionAuthority', () => ({
    canAccessCommerceHubVendorNavigationCapability: () => true,
}))
vi.mock('react-i18next', () => ({
    useTranslation: () => ({ t: (key, options) => options?.defaultValue ?? key, i18n: { language: 'en' } }),
}))

function Consumer({ explicitSurface }) {
    const toast = useToast(explicitSurface ? { surface: explicitSurface } : undefined)
    const { confirm } = useConfirmDialog()
    return (
        <>
            <button onClick={() => toast.showInfo('Module notice', 0)}>Notify module</button>
            <button onClick={() => void confirm({ title: 'Module confirmation' })}>Confirm module</button>
        </>
    )
}

describe('embedded product notification boundaries', () => {
    it.each([
        ['Storefront', <StorefrontWorkspaceBoundary />],
        ['CommerceHub', <CommerceHubVendorRouteGuard capability="commercehub.catalog.manage" />],
    ])('%s retains non-POS Toast and Confirm defaults inside the POS shell', (_name, boundary) => {
        render(
            <MemoryRouter>
                <ToastProvider>
                    <ConfirmDialogProvider>
                        <PosSurfaceContext.Provider value="pos">
                            <Routes>
                                <Route element={boundary}>
                                    <Route path="/" element={<Consumer />} />
                                </Route>
                            </Routes>
                        </PosSurfaceContext.Provider>
                    </ConfirmDialogProvider>
                </ToastProvider>
            </MemoryRouter>,
        )
        fireEvent.click(screen.getByText('Notify module'))
        expect(screen.getByRole('status')).not.toHaveAttribute('data-product')
        expect(screen.getByText('Module notice').closest('.pos-toast')).toBeNull()
        fireEvent.click(screen.getByText('Confirm module'))
        expect(screen.getByRole('dialog').closest('[data-product="pos"]')).toBeNull()
    })

    it('honors explicit CommerceHub origin for the standalone seller-application entry', () => {
        render(<ToastProvider><PosSurfaceContext.Provider value="pos"><Consumer explicitSurface="commercehub" /></PosSurfaceContext.Provider></ToastProvider>)
        fireEvent.click(screen.getByText('Notify module'))
        expect(screen.getByRole('status')).not.toHaveAttribute('data-product')
    })
})
