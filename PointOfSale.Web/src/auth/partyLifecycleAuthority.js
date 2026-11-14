import { hasPosPermission } from './posPermissions'

// Lifecycle endpoints additionally require these current staff roles; ordinary
// Cashier customer-management authority must not expose an Owner workflow.
export const canManagePartyLifecycle = (user, party) =>
    ['Owner', 'Admin', 'Manager'].includes(user?.role)
    && ['customers', 'suppliers'].includes(party)
    && hasPosPermission(user, `${party}.manage`)
