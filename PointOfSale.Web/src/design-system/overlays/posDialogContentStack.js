// Tracks open POS Radix dialog content elements in mount order. The legacy
// ConfirmModal portals a POS-origin confirmation into the topmost element so
// Radix's focus trap and hideOthers treat it as part of the modal layer
// instead of hiding it from assistive technology and stealing focus back.
let stack = []
const listeners = new Set()

function notify() {
    listeners.forEach((listener) => listener())
}

export function registerPosDialogContent(element) {
    if (!element) {
        return
    }
    if (stack.includes(element)) {
        return
    }
    stack = [...stack, element]
    notify()
}

export function unregisterPosDialogContent(element) {
    if (!element) {
        return
    }
    stack = stack.filter((entry) => entry !== element)
    notify()
}

export function getTopPosDialogContent() {
    return stack.length > 0 ? stack[stack.length - 1] : null
}

export function subscribePosDialogContent(listener) {
    listeners.add(listener)
    return () => listeners.delete(listener)
}

export function resetPosDialogContentStackForTests() {
    stack = []
}
