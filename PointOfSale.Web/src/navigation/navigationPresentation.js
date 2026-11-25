import { parsePath } from 'react-router-dom'

// The only dynamic route families with a verified parent destination in the
// approved shell scope. Names come from existing localized route-role labels.
const VERIFIED_PARENT_ROUTES = [
    { pattern: /^\/products\/new\/edit$/, parentPathname: '/products', currentKey: 'products.addProduct' },
    { pattern: /^\/products\/[^/]+\/edit$/, parentPathname: '/products', currentKey: 'products.editProduct' },
    { pattern: /^\/treasury\/account\/[^/]+$/, parentPathname: '/treasury/hub', currentKey: 'treasury.accountName' },
]

// Legacy aliases render the same guarded screens; resolve their presentation
// through the canonical destination, never through a new permission.
export function canonicalPresentationPath(pathname) {
    return /^\/admin\/commercehub\/(earnings|payments)$/.test(pathname)
        ? pathname.replace('/admin/commercehub/', '/commercehub/')
        : pathname
}

// Match only declared menu selectors. Unrelated or reordered query parameters
// must not change presentation context and are never written to storage.
export function declaredQueryMatches(declaredSearch, locationSearch) {
    const declared = [...new URLSearchParams(declaredSearch || '')]
    if (!declared.length) {
        return true
    }
    const actual = new URLSearchParams(locationSearch || '')
    return declared.every(([key, value]) => actual.get(key) === value)
}

export function menuDestinationMatches(declaredPath, pathname, search) {
    const declared = parsePath(declaredPath || '')
    return declared.pathname === pathname && declaredQueryMatches(declared.search, search)
}

// Depth-first, tree-order search: the first eligible occurrence is canonical.
export function findFirstMenuMatch(nodes, predicate, ancestors = []) {
    for (const node of nodes) {
        if (predicate(node)) {
            return { node, ancestors }
        }
        if (node.children) {
            const match = findFirstMenuMatch(node.children, predicate, [...ancestors, node])
            if (match) {
                return match
            }
        }
    }
    return null
}

export function resolveNavigationEntry(entries, pathname, search) {
    const canonicalPath = canonicalPresentationPath(pathname)
    const candidates = entries.filter(entry => entry.to.pathname === canonicalPath
        && declaredQueryMatches(entry.to.search, search))
    // Mirrors resolveNavigationVisit: a declared selectors match wins over the
    // bare fallback, and registry order keeps the first eligible occurrence.
    return candidates.find(entry => entry.to.search) || candidates[0] || null
}

export function dynamicParentPathname(pathname) {
    return VERIFIED_PARENT_ROUTES.find(route => route.pattern.test(pathname))?.parentPathname || null
}

export function resolveDynamicParentContext(entries, pathname) {
    const rule = VERIFIED_PARENT_ROUTES.find(route => route.pattern.test(pathname))
    if (!rule) {
        return null
    }
    const parent = entries.find(entry => entry.to.pathname === rule.parentPathname && !entry.to.search)
    // A missing parent means the actor cannot reach it; never fabricate a trail.
    return parent ? { parent, currentKey: rule.currentKey } : null
}

export function resolveBreadcrumbContext(entries, pathname, search) {
    const entry = resolveNavigationEntry(entries, pathname, search)
    if (entry) {
        return { kind: 'entry', entry, currentKey: null }
    }
    const dynamic = resolveDynamicParentContext(entries, pathname)
    return dynamic ? { kind: 'parent', entry: dynamic.parent, currentKey: dynamic.currentKey } : null
}
