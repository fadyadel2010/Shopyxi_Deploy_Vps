import { describe, expect, it } from 'vitest'
import {
    canonicalPresentationPath,
    declaredQueryMatches,
    dynamicParentPathname,
    findFirstMenuMatch,
    menuDestinationMatches,
    resolveBreadcrumbContext,
    resolveDynamicParentContext,
    resolveNavigationEntry,
} from './navigationPresentation'

type Entry = { id: string; to: { pathname: string; search: string } }

const entry = (id: string, path: string): Entry => {
    const [pathname, query = ''] = path.split('?')
    return { id, to: { pathname, search: query ? `?${query}` : '' } }
}

describe('declared navigation query selectors', () => {
    it('ignores params the menu does not declare and never treats them as identity', () => {
        expect(declaredQueryMatches('', '?token=secret&tab=main')).toBe(true)
        expect(declaredQueryMatches('?tab=sub', '?tab=sub&shellAudit=1')).toBe(true)
        expect(declaredQueryMatches('?tab=sub', '?tab=main')).toBe(false)
        expect(declaredQueryMatches('?tab=sub', '')).toBe(false)
    })

    it('matches a destination by pathname and declared query only', () => {
        expect(menuDestinationMatches('/products', '/products', '?customer=private')).toBe(true)
        expect(menuDestinationMatches('/financial-accounts?tab=sub', '/financial-accounts', '?tab=sub&x=1')).toBe(true)
        expect(menuDestinationMatches('/financial-accounts?tab=sub', '/financial-accounts', '?tab=main')).toBe(false)
        expect(menuDestinationMatches('/financial-accounts', '/other', '')).toBe(false)
    })
})

describe('NavigationBreadcrumbs presentation resolver', () => {
    const entries = [
        entry('pos:nav.dashboard', '/'),
        entry('pos:nav.itemCard', '/products'),
        entry('pos:nav.treasury', '/treasury'),
        entry('pos:treasury.accountName', '/treasury/hub'),
        entry('pos:financial.mainAccounts', '/financial-accounts'),
        entry('pos:financial.subAccounts', '/financial-accounts?tab=sub'),
    ]

    it('prefers the declared query entry and ignores unrelated extra parameters', () => {
        expect(resolveNavigationEntry(entries, '/financial-accounts', '')?.id).toBe('pos:financial.mainAccounts')
        expect(resolveNavigationEntry(entries, '/financial-accounts', '?tab=sub')?.id).toBe('pos:financial.subAccounts')
        expect(resolveNavigationEntry(entries, '/financial-accounts', '?tab=sub&shellAudit=1')?.id).toBe('pos:financial.subAccounts')
        expect(resolveNavigationEntry(entries, '/financial-accounts', '?tab=main&token=secret')?.id).toBe('pos:financial.mainAccounts')
    })

    it('canonicalizes alias routes only for presentation lookups', () => {
        expect(canonicalPresentationPath('/admin/commercehub/earnings')).toBe('/commercehub/earnings')
        expect(canonicalPresentationPath('/admin/commercehub/payments')).toBe('/commercehub/payments')
        expect(canonicalPresentationPath('/admin/commercehub/settings')).toBe('/admin/commercehub/settings')
        expect(resolveNavigationEntry(
            entries,
            '/admin/commercehub/earnings',
            '',
        )).toBeNull()
    })

    it('resolves the first declared occurrence for duplicated destinations', () => {
        const duplicates = [
            entry('pos:first', '/treasury'),
            entry('pos:second', '/treasury'),
        ]
        expect(resolveNavigationEntry(duplicates, '/treasury', '')?.id).toBe('pos:first')
    })

    it('derives verified dynamic parent context and refuses to invent parents', () => {
        expect(dynamicParentPathname('/products/new/edit')).toBe('/products')
        expect(dynamicParentPathname('/products/9f2c1/edit')).toBe('/products')
        expect(dynamicParentPathname('/treasury/account/AUDIT')).toBe('/treasury/hub')
        expect(dynamicParentPathname('/products/9f2c1')).toBeNull()
        expect(dynamicParentPathname('/treasury/account')).toBeNull()

        expect(resolveDynamicParentContext(entries, '/products/new/edit')).toMatchObject({
            parent: { to: { pathname: '/products' } },
            currentKey: 'products.addProduct',
        })
        expect(resolveDynamicParentContext(entries, '/products/9f2c1/edit')?.currentKey).toBe('products.editProduct')
        expect(resolveDynamicParentContext(entries, '/treasury/account/AUDIT')).toMatchObject({
            parent: { id: 'pos:treasury.accountName' },
            currentKey: 'treasury.accountName',
        })
        expect(resolveDynamicParentContext(entries, '/products/9f2c1')).toBeNull()
        expect(resolveDynamicParentContext(entries.filter(item => item.to.pathname !== '/products'), '/products/9f2c1/edit')).toBeNull()
    })

    it('returns a single breadcrumb context: menu entry, verified parent, or nothing', () => {
        expect(resolveBreadcrumbContext(entries, '/products', '')).toMatchObject({ kind: 'entry', currentKey: null })
        expect(resolveBreadcrumbContext(entries, '/products/new/edit', '')).toMatchObject({
            kind: 'parent',
            currentKey: 'products.addProduct',
        })
        expect(resolveBreadcrumbContext(entries, '/unregistered', '')).toBeNull()
    })

    it('finds the canonical first eligible menu occurrence depth-first', () => {
        const tree = [
            { key: 'dashboard' },
            { key: 'catalog', children: [{ key: 'products' }, { key: 'units' }] },
        ]
        expect(findFirstMenuMatch(tree, node => node.key === 'products')).toMatchObject({
            node: { key: 'products' },
            ancestors: [{ key: 'catalog' }],
        })
        expect(findFirstMenuMatch(tree, node => node.key === 'missing')).toBeNull()
    })
})
