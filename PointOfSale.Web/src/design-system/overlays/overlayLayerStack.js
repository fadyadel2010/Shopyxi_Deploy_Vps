// Tracks the open overlay layers in mount order so only the topmost layer
// reacts to Escape and outside-pointer dismissal. Legacy ConfirmModal and the
// Radix Dialog primitive both register here; without this, one Escape event
// can close two independent dialogs.
let layers = []
const escapeOwners = new WeakMap()

export function registerOverlayLayer(token = Symbol('pos-overlay-layer')) {
    if (!layers.includes(token)) {
        layers = [...layers, token]
    }
    return token
}

export function unregisterOverlayLayer(token) {
    if (!token) {
        return
    }
    layers = layers.filter((entry) => entry !== token)
}

export function isTopOverlayLayer(token) {
    return Boolean(token) && layers.length > 0 && layers[layers.length - 1] === token
}

// Snapshot ownership for the whole native event. A synchronous close/unmount
// must not let the same Escape fall through to the newly exposed lower modal.
export function ownsOverlayEscape(event, token) {
    if (!escapeOwners.has(event)) {
        if (event.defaultPrevented) {
            return false
        }
        escapeOwners.set(event, layers[layers.length - 1])
    }
    return escapeOwners.get(event) === token
}

export function resetOverlayLayerStackForTests() {
    layers = []
}
