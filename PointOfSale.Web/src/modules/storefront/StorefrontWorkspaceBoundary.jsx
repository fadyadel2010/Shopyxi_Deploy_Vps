import { Fragment } from 'react'
import { Outlet, useLocation } from 'react-router-dom'
import { useAuthStore } from '../../store/authStore'
import { PosSurfaceContext } from '../../design-system/overlays/posOverlayContext'

// Candidates, accepted revisions and confirmations belong to one account/store.
export function StorefrontWorkspaceBoundary() {
    const user = useAuthStore(state => state.user)
    const { pathname } = useLocation()
    return (
        <PosSurfaceContext.Provider value="storefront">
            <Fragment key={`${user?.tenantId || ''}:${user?.id || ''}:${pathname}`}><Outlet /></Fragment>
        </PosSurfaceContext.Provider>
    )
}
