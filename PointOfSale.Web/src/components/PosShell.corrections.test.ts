import { readFileSync } from 'node:fs'
import { resolve } from 'node:path'
import { describe, expect, it } from 'vitest'

const css = readFileSync(resolve('src/components/PosShell.css'), 'utf8')
const bridge = readFileSync(resolve('src/design-system/providers/posProviderBridge.css'), 'utf8')

describe('live correction source boundaries', () => {
    it('keeps the bridge conditional on an actual POS-origin toast', () => {
        expect(bridge).toContain(':has(> [data-product="pos"] > .pos-toast)')
        expect(bridge).not.toMatch(/(^|\n)(?:body|:root|\.fixed)\s*\{/)
        expect(bridge).not.toContain('!important')
    })

    it('keeps chrome below overlays without raising its accepted stacking layer', () => {
        expect(css).toMatch(/\.pos-shell__topbar\s*\{[^}]*z-index:\s*30;/)
        expect(css).toMatch(/\.pos-sidebar\s*\{[^}]*z-index:\s*40;/)
        expect(bridge).toMatch(/:has\(> \[data-product="pos"\] > \.pos-toast\)\s*\{[^}]*z-index:\s*20;/)
    })

    it('separates logout and retains the mobile identity group without a new menu', () => {
        expect(css).toMatch(/\.pos-shell__user\s*\{\s*display:\s*flex;/)
        expect(css).toMatch(/\.pos-shell__logout\s*\{[^}]*margin-inline-start:\s*var\(--pos-space-2\);/)
        expect(css).toMatch(/\.pos-shell__logout\s*\{[^}]*border-inline-start-color:/)
    })

    it('uses content-driven text direction, leaving the drawer chrome untouched', () => {
        expect(css).toMatch(/\.pos-help__markdown \[dir="auto"\]\s*\{[^}]*unicode-bidi:\s*plaintext;/)
        expect(css).not.toMatch(/\.pos-help-drawer\s*\{[^}]*direction:/)
    })
})
