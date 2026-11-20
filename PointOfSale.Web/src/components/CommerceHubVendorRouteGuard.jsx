import { Navigate, Outlet } from 'react-router-dom'
import { useTranslation } from 'react-i18next'
import { canAccessCommerceHubVendorNavigationCapability } from '../auth/posVendorSessionAuthority'
import { useAuthStore } from '../store/authStore'
import { PosSurfaceContext } from '../design-system/overlays/posOverlayContext'

export default function CommerceHubVendorRouteGuard({ capability, deniedElement }) {
    const { t } = useTranslation()
    const user = useAuthStore((state) => state.user)
    const isRouteAuthorityValidating = useAuthStore((state) => state.isRouteAuthorityValidating)

    if (isRouteAuthorityValidating) {
        return (
            <div
                role="status"
                aria-label={t('common.validatingVendorAuthority', { defaultValue: 'Validating vendor authority' })}
            />
        )
    }

    if (!canAccessCommerceHubVendorNavigationCapability(user, capability)) {
        return deniedElement ?? <Navigate to="/" replace />
    }

    return <PosSurfaceContext.Provider value="commercehub"><Outlet /></PosSurfaceContext.Provider>
}
