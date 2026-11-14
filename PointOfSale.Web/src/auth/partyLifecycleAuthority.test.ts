import { describe, expect, it } from 'vitest'
import { canManagePartyLifecycle } from './partyLifecycleAuthority'

describe('party lifecycle presentation authority', () => {
    it.each(['Owner', 'Admin', 'Manager'])('admits current %s with the exact management permission', role => {
        expect(canManagePartyLifecycle({ role, effectivePermissions: ['customers.manage'] }, 'customers')).toBe(true)
        expect(canManagePartyLifecycle({ role, effectivePermissions: ['suppliers.manage'] }, 'suppliers')).toBe(true)
        expect(canManagePartyLifecycle({ role, effectivePermissions: [] }, 'customers')).toBe(false)
    })
    it.each(['Cashier', 'Warehouse', 'SuperAdmin', undefined])('denies %s despite a supplied management permission', role => {
        expect(canManagePartyLifecycle({ role, effectivePermissions: ['customers.manage', 'suppliers.manage'] }, 'customers')).toBe(false)
        expect(canManagePartyLifecycle({ role, effectivePermissions: ['customers.manage', 'suppliers.manage'] }, 'suppliers')).toBe(false)
    })
    it('fails closed for missing authority or unknown party kind', () => {
        expect(canManagePartyLifecycle(null, 'customers')).toBe(false)
        expect(canManagePartyLifecycle({ role: 'Owner', effectivePermissions: ['customers.manage'] }, 'users')).toBe(false)
    })
})
