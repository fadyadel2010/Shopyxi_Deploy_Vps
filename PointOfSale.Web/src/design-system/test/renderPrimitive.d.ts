import type { ReactNode } from 'react'
import type { RenderResult } from '@testing-library/react'
import type { PosSurface } from '../overlays/posOverlayContext'
export function renderPrimitive(ui: ReactNode, options?: {
    surface?: PosSurface
    product?: string
    dir?: 'ltr' | 'rtl'
    tooltipDelayDuration?: number
}): RenderResult
