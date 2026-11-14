import CanonicalCatalogBootstrap from '../../modules/shipping-administration/components/CanonicalCatalogBootstrap'
import { getSuperAdminAuthorityRevision, useSuperAdminAuthStore } from '../auth/superAdminAuthStore'
import { useSuperAdminLocale } from '../i18n/useSuperAdminLocale'

export default function GeographicCatalogPage() {
    const user = useSuperAdminAuthStore(state => state.user)
    const authenticated = useSuperAdminAuthStore(state => state.isAuthenticated)
    const { tr, dir } = useSuperAdminLocale()

    // The enclosing SuperAdminProtectedRoute is the established platform-policy boundary.
    // Do not infer global authority from merchant roles or invent a navigation grant.
    if (!authenticated || user?.role !== 'SuperAdmin' || user.authType !== 'super_admin') return null

    return <main dir={dir} className="space-y-4">
        <h1 className="text-xl font-semibold">{tr('Global geographic catalog', 'الكتالوج الجغرافي العام')}</h1>
        <CanonicalCatalogBootstrap platformIdentity={user.superAdminId} authorityRevision={getSuperAdminAuthorityRevision()} />
    </main>
}
