import type { NavigationEntry } from './navigation.types'
import type { ApplicationMenuNode } from './applicationNavigation'
export function canonicalPresentationPath(pathname: string): string
export function declaredQueryMatches(declaredSearch?: string, locationSearch?: string): boolean
export function menuDestinationMatches(declaredPath: string, pathname: string, search?: string): boolean
export function findFirstMenuMatch<T extends ApplicationMenuNode>(nodes: T[], predicate: (node: T) => boolean, ancestors?: T[]): { node: T; ancestors: T[] } | null
export function resolveNavigationEntry<T extends Pick<NavigationEntry, 'to'>>(entries: T[], pathname: string, search?: string): T | null
export function dynamicParentPathname(pathname: string): string | null
export function resolveDynamicParentContext<T extends Pick<NavigationEntry, 'to'>>(entries: T[], pathname: string): { parent: T; currentKey: string } | null
export function resolveBreadcrumbContext<T extends Pick<NavigationEntry, 'to'>>(entries: T[], pathname: string, search?: string):
    { kind: 'entry'; entry: T; currentKey: null } | { kind: 'parent'; entry: T; currentKey: string } | null
