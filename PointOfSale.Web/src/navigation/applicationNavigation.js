import {
    resolveStorefrontScreen,
} from '../modules/storefront/informationArchitecture/storefrontInformationArchitecture'

/**
 * The four SHOPYXI applications exposed by the Top Header App Switcher.
 *
 * `rootKey` names an existing root in `buildAdminMenu`. No second menu tree is
 * defined here: labels, icons, children and permissions stay owned by the
 * existing navigation registry.
 *
 * POS Admin owns every navigation root that is not explicitly assigned to one
 * of the other applications, including the Services Marketplace root. The
 * Storefront and CommerceHub roots already carry their own `application`
 * marker; the Commission System is the existing `affiliate-system` root.
 */
export const APPLICATION_IDS = Object.freeze({
    POS: 'pos',
    STOREFRONT: 'storefront',
    COMMERCEHUB: 'commercehub',
    COMMISSION: 'commission',
})

export const APPLICATIONS = Object.freeze([
    {
        id: APPLICATION_IDS.POS,
        rootKey: null,
        labelKey: 'smartNavigation.modules.pos',
        // Existing Dashboard entry: the first POS Admin menu destination.
        landingPath: '/',
    },
    {
        id: APPLICATION_IDS.STOREFRONT,
        rootKey: 'storefront',
        labelKey: 'storefront.ia.root',
        // Existing canonical Storefront Dashboard (storefront.ia.overview).
        landingPath: '/storefront',
    },
    {
        id: APPLICATION_IDS.COMMERCEHUB,
        rootKey: 'commercehub',
        labelKey: 'nav.commerceHub',
        // There is no dedicated CommerceHub dashboard. `/admin/commercehub` is
        // the existing CommerceHub administration/publishing surface; when the
        // actor cannot reach it, the first permitted CommerceHub destination in
        // the existing menu order is used instead.
        landingPath: '/admin/commercehub',
    },
    {
        id: APPLICATION_IDS.COMMISSION,
        rootKey: 'affiliate-system',
        labelKey: 'nav.affiliateSystem',
        // Existing first destination of the `affiliate-system` root.
        landingPath: '/admin/affiliates',
    },
])

const APPLICATION_BY_ROOT_KEY = Object.freeze({
    storefront: APPLICATION_IDS.STOREFRONT,
    commercehub: APPLICATION_IDS.COMMERCEHUB,
    'affiliate-system': APPLICATION_IDS.COMMISSION,
})

const APPLICATION_IDS_SET = new Set(Object.values(APPLICATION_IDS))
const STOREFRONT_PATH_PREFIXES = ['/storefront', '/ecommerce', '/analytics', '/admin/shipping']
const COMMERCEHUB_PATH_PREFIXES = ['/commercehub', '/admin/commercehub']
const COMMISSION_PATH_PREFIXES = ['/admin/affiliates']

const normalizePathname = pathname => {
    const value = String(pathname || '/').split('?')[0]
    if (!value || value === '/') return '/'
    return value.length > 1 ? value.replace(/\/+$/, '') : value
}

const matchesPrefix = (pathname, prefix) => pathname === prefix || pathname.startsWith(`${prefix}/`)

const matchesAnyPrefix = (pathname, prefixes) => prefixes.some(prefix => matchesPrefix(pathname, prefix))

/**
 * Which application owns a top-level navigation root. This is ownership for
 * shell presentation only; it is never used as an authorization predicate.
 */
export function menuApplicationId(node) {
    const byRootKey = node?.key ? APPLICATION_BY_ROOT_KEY[node.key] : null
    if (byRootKey) return byRootKey
    if (node?.application && APPLICATION_IDS_SET.has(node.application) && node.application !== APPLICATION_IDS.POS) {
        return node.application
    }
    return APPLICATION_IDS.POS
}

export function scopeMenuToApplication(menuItems, applicationId) {
    return (menuItems || []).filter(node => menuApplicationId(node) === applicationId)
}

/**
 * Resolve the active application from the committed route, never from storage
 * or a user-editable query parameter. Existing Storefront screen authority is
 * consulted first for routes with declared query selectors; prefix ownership
 * covers the remaining registered/dynamic storefront surfaces.
 */
export function resolveApplicationFromLocation(pathname, search = '') {
    if (resolveStorefrontScreen(pathname, search)) return APPLICATION_IDS.STOREFRONT

    const path = normalizePathname(pathname)
    if (matchesAnyPrefix(path, STOREFRONT_PATH_PREFIXES)) return APPLICATION_IDS.STOREFRONT
    if (matchesAnyPrefix(path, COMMERCEHUB_PATH_PREFIXES)) return APPLICATION_IDS.COMMERCEHUB
    if (matchesAnyPrefix(path, COMMISSION_PATH_PREFIXES)) return APPLICATION_IDS.COMMISSION

    // Unknown or unclassified authenticated routes stay in the POS shell.
    return APPLICATION_IDS.POS
}

const findPath = (nodes, predicate) => {
    for (const node of nodes || []) {
        if (predicate(node)) return node.path
        if (node.children) {
            const childPath = findPath(node.children, predicate)
            if (childPath) return childPath
        }
    }
    return null
}

/**
 * Prefer the application's existing canonical destination, then the first
 * destination still visible after the existing permission/capability filter.
 * A null result means the actor cannot reach any destination in this
 * application, so the App Switcher must not expose it.
 */
export function resolveAuthorizedLandingPath(application, filteredMenuItems) {
    const preferred = application?.landingPath
    const preferredPath = preferred
        ? findPath(filteredMenuItems, node => !node.children && !node.placeholder && node.path === preferred)
        : null
    if (preferredPath) return preferredPath

    return findPath(filteredMenuItems, node => !node.children && !node.placeholder && Boolean(node.path))
}

export function navigationEntryApplication(entry) {
    return resolveApplicationFromLocation(entry?.to?.pathname, entry?.to?.search)
}
