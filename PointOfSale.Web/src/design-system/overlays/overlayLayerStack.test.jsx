import { afterEach, expect, it } from 'vitest'
import { isTopOverlayLayer, ownsOverlayEscape, registerOverlayLayer, resetOverlayLayerStackForTests, unregisterOverlayLayer } from './overlayLayerStack'
import { getTopPosDialogContent, registerPosDialogContent, resetPosDialogContentStackForTests, subscribePosDialogContent, unregisterPosDialogContent } from './posDialogContentStack'

afterEach(() => {
    resetOverlayLayerStackForTests()
    resetPosDialogContentStackForTests()
})

it('keeps one Escape owned by the original top layer after synchronous unmount', () => {
    const lower = registerOverlayLayer()
    const upper = registerOverlayLayer()
    const event = new KeyboardEvent('keydown', { key: 'Escape', cancelable: true })
    expect(ownsOverlayEscape(event, upper)).toBe(true)
    unregisterOverlayLayer(upper)
    expect(isTopOverlayLayer(lower)).toBe(true)
    expect(ownsOverlayEscape(event, lower)).toBe(false)
    expect(ownsOverlayEscape(new KeyboardEvent('keydown', { key: 'Escape' }), lower)).toBe(true)
})

it('does not override Escape already handled by an unregistered Radix layer', () => {
    const lower = registerOverlayLayer()
    const event = new KeyboardEvent('keydown', { key: 'Escape', cancelable: true })
    event.preventDefault()
    expect(ownsOverlayEscape(event, lower)).toBe(false)
})

it('deduplicates registrations and unsubscribes content lifecycle notifications', () => {
    const element = document.createElement('div')
    let notifications = 0
    const unsubscribe = subscribePosDialogContent(() => { notifications += 1 })
    registerPosDialogContent(element)
    registerPosDialogContent(element)
    expect(notifications).toBe(1)
    expect(getTopPosDialogContent()).toBe(element)
    unsubscribe()
    unregisterPosDialogContent(element)
    expect(getTopPosDialogContent()).toBeNull()
    expect(notifications).toBe(1)
    const token = registerOverlayLayer()
    registerOverlayLayer(token)
    unregisterOverlayLayer(token)
    expect(isTopOverlayLayer(token)).toBe(false)
})
