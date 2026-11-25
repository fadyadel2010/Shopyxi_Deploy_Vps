export function registerOverlayLayer(token?: symbol): symbol
export function unregisterOverlayLayer(token: symbol | null | undefined): void
export function isTopOverlayLayer(token: symbol | null | undefined): boolean
export function ownsOverlayEscape(event: Event, token: symbol): boolean
export function resetOverlayLayerStackForTests(): void
