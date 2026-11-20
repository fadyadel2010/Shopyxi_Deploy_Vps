import type { ReactElement, RefObject } from 'react';
import type { PosUser } from '../store/authStore';
import type { NavigationMenuNode } from '../navigation/navigation.types';
export function buildAdminMenu(t: (key: string, fallback?: string) => { key: string; fallback?: string }, user: PosUser | null): NavigationMenuNode[];
export default function Sidebar(props: { isOpen: boolean; onClose(): void; restoreFocusRef?: RefObject<HTMLElement> }): ReactElement;
