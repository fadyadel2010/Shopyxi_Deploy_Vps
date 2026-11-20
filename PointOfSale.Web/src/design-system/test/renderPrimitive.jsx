import './radixJsdomPolyfills'
import { render } from '@testing-library/react'
import { PosOverlayHost, PosOverlayProvider } from '../overlays/posOverlayContext'

/**
 * Renders a primitive inside the real POS boundary: the token scope attribute,
 * the surface/overlay providers and the overlay host element that Dialog and
 * Tooltip portal into.
 */
export function renderPrimitive(ui, {
    surface = 'pos',
    product = 'pos',
    dir = 'ltr',
    tooltipDelayDuration = 0,
} = {}) {
    return render(
        <div data-product={product} dir={dir}>
            <PosOverlayProvider surface={surface} tooltipDelayDuration={tooltipDelayDuration}>
                {ui}
                <PosOverlayHost />
            </PosOverlayProvider>
        </div>,
    )
}
