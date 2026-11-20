import type { Context, ReactElement, ReactNode } from 'react'
export type PosSurface = 'pos' | 'other'
export interface PosOverlay {
    element: HTMLElement | null
    registerHost(node: HTMLElement | null): void
}
export const PosSurfaceContext: Context<PosSurface>
export const PosOverlayContext: Context<PosOverlay | null>
export const PosNestedOverlayContext: Context<HTMLElement | null>
export function PosOverlayProvider(props: { children: ReactNode; surface?: PosSurface; tooltipDelayDuration?: number }): ReactElement
export function PosOverlayHost(props: { id?: string }): ReactElement
export function usePosOverlayElement(): HTMLElement | null
export function usePosSurface(): PosSurface
