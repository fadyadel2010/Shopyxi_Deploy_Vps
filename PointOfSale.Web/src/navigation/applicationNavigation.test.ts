import { describe, expect, it } from 'vitest'
import {
    APPLICATION_IDS,
    APPLICATIONS,
    menuApplicationId,
    navigationEntryApplication,
    resolveApplicationFromLocation,
    resolveAuthorizedLandingPath,
    scopeMenuToApplication,
} from './applicationNavigation'

const app = (id: string) => APPLICATIONS.find(application => application.id === id)!

describe('application ownership and route resolution', () => {
    it('resolves every application surface from the committed route', () => {
        expect(resolveApplicationFromLocation('/')).toBe(APPLICATION_IDS.POS)
        expect(resolveApplicationFromLocation('/treasury/hub')).toBe(APPLICATION_IDS.POS)
        expect(resolveApplicationFromLocation('/marketplace')).toBe(APPLICATION_IDS.POS)
        expect(resolveApplicationFromLocation('/storefront')).toBe(APPLICATION_IDS.STOREFRONT)
        expect(resolveApplicationFromLocation('/storefront/domains/synthetic-id')).toBe(APPLICATION_IDS.STOREFRONT)
        expect(resolveApplicationFromLocation('/ecommerce/orders/synthetic-id')).toBe(APPLICATION_IDS.STOREFRONT)
        expect(resolveApplicationFromLocation('/analytics/attribution/models')).toBe(APPLICATION_IDS.STOREFRONT)
        expect(resolveApplicationFromLocation('/admin/shipping/shipments')).toBe(APPLICATION_IDS.STOREFRONT)
        expect(resolveApplicationFromLocation('/settings/analytics')).toBe(APPLICATION_IDS.STOREFRONT)
        expect(resolveApplicationFromLocation('/settings', '?tab=storefront')).toBe(APPLICATION_IDS.STOREFRONT)
        expect(resolveApplicationFromLocation('/commercehub/orders/synthetic-id')).toBe(APPLICATION_IDS.COMMERCEHUB)
        expect(resolveApplicationFromLocation('/admin/commercehub/earnings')).toBe(APPLICATION_IDS.COMMERCEHUB)
        expect(resolveApplicationFromLocation('/admin/affiliates/synthetic-id')).toBe(APPLICATION_IDS.COMMISSION)
    })

    it('keeps unknown or unclassified authenticated routes in the POS fallback', () => {
        expect(resolveApplicationFromLocation('/genuinely-unregistered')).toBe(APPLICATION_IDS.POS)
        expect(resolveApplicationFromLocation('')).toBe(APPLICATION_IDS.POS)
    })

    it('uses the existing menu root application marker rather than a second registry', () => {
        expect(menuApplicationId({ key: 'storefront', application: 'storefront' })).toBe(APPLICATION_IDS.STOREFRONT)
        expect(menuApplicationId({ key: 'commercehub', application: 'commercehub' })).toBe(APPLICATION_IDS.COMMERCEHUB)
        expect(menuApplicationId({ key: 'affiliate-system' })).toBe(APPLICATION_IDS.COMMISSION)
        expect(menuApplicationId({ key: 'marketplace', application: 'services-marketplace' })).toBe(APPLICATION_IDS.POS)
        expect(menuApplicationId({ key: 'dashboard' })).toBe(APPLICATION_IDS.POS)
    })

    it('scopes a menu without flattening or reordering it', () => {
        const menu = [
            { key: 'dashboard' },
            { key: 'storefront', children: [{ key: 'design' }, { key: 'operations' }] },
            { key: 'commercehub', children: [{ key: 'orders' }] },
            { key: 'affiliate-system', children: [{ key: 'admin-affiliates' }] },
        ]
        expect(scopeMenuToApplication(menu, APPLICATION_IDS.POS).map(node => node.key)).toEqual(['dashboard'])
        expect(scopeMenuToApplication(menu, APPLICATION_IDS.STOREFRONT).map(node => node.key)).toEqual(['storefront'])
        expect(scopeMenuToApplication(menu, APPLICATION_IDS.COMMERCEHUB).map(node => node.key)).toEqual(['commercehub'])
        expect(scopeMenuToApplication(menu, APPLICATION_IDS.COMMISSION).map(node => node.key)).toEqual(['affiliate-system'])
    })

    it('prefers the canonical landing destination and otherwise uses the first permitted destination', () => {
        const allowed = [
            { key: 'storefront', children: [{ path: '/storefront' }, { path: '/ecommerce/overview' }] },
        ]
        expect(resolveAuthorizedLandingPath(app(APPLICATION_IDS.STOREFRONT), allowed)).toBe('/storefront')
        expect(resolveAuthorizedLandingPath(app(APPLICATION_IDS.STOREFRONT), [
            { key: 'storefront', children: [{ path: '/ecommerce/overview' }] },
        ])).toBe('/ecommerce/overview')
        expect(resolveAuthorizedLandingPath(app(APPLICATION_IDS.STOREFRONT), [
            { key: 'storefront', children: [{ path: '#', placeholder: true }] },
        ])).toBeNull()
    })

    it('maps a navigation registry entry back to its application for favorites scoping', () => {
        expect(navigationEntryApplication({ to: { pathname: '/storefront', search: '' } })).toBe(APPLICATION_IDS.STOREFRONT)
        expect(navigationEntryApplication({ to: { pathname: '/admin/affiliates', search: '' } })).toBe(APPLICATION_IDS.COMMISSION)
        expect(navigationEntryApplication({ to: { pathname: '/marketplace', search: '' } })).toBe(APPLICATION_IDS.POS)
    })
})
