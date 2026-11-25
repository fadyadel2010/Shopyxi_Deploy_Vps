import type { NavigationEntry } from './navigation.types'
export interface ApplicationMenuNode {
    key?: string
    application?: string
    path?: string
    placeholder?: boolean
    children?: ApplicationMenuNode[]
}
export type ApplicationId = 'pos' | 'storefront' | 'commercehub' | 'commission'
export interface Application {
    readonly id: ApplicationId
    readonly rootKey: string | null
    readonly labelKey: string
    readonly landingPath: string
}
export const APPLICATION_IDS: Readonly<{ POS: 'pos'; STOREFRONT: 'storefront'; COMMERCEHUB: 'commercehub'; COMMISSION: 'commission' }>
export const APPLICATIONS: readonly Application[]
export function menuApplicationId(node: ApplicationMenuNode): ApplicationId
export function scopeMenuToApplication<T extends ApplicationMenuNode>(menuItems: T[] | null | undefined, applicationId: ApplicationId): T[]
export function resolveApplicationFromLocation(pathname: string, search?: string): ApplicationId
export function resolveAuthorizedLandingPath(application: Application, filteredMenuItems: ApplicationMenuNode[]): string | null
export function navigationEntryApplication(entry: Pick<NavigationEntry, 'to'>): ApplicationId
