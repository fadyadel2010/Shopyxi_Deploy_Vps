import { readFileSync } from 'node:fs'
import { resolve } from 'node:path'
import postcss from 'postcss'
import { describe, expect, it } from 'vitest'

const css = readFileSync(resolve('src/pages/Login.css'), 'utf8')
const root = postcss.parse(css)
const tokens = ['primitives', 'semantic', 'components', 'layout']
    .map(name => readFileSync(resolve(`src/design-system/tokens/pos-${name}.css`), 'utf8')).join('\n')
const defined = new Set([...tokens.matchAll(/(--pos-[\w-]+)\s*:/g)].map(match => match[1]))

describe('Login stylesheet isolation and governance', () => {
    it('scopes every selector to the POS boundary and Login class namespace', () => {
        root.walkRules(rule => {
            for (const selector of rule.selectors) {
                expect(selector).toMatch(/^\[data-product="pos"\](?:\.pos-login|\s+\.pos-login__)/)
            }
        })
    })

    it('uses existing tokens without redefining them or adding raw colors', () => {
        root.walkDecls(declaration => {
            expect(declaration.prop).not.toMatch(/^--/)
            expect(declaration.value).not.toMatch(/#[0-9a-f]{3,8}\b|\b(?:rgb|rgba|hsl|hsla)\(/i)
            for (const [, name] of declaration.value.matchAll(/var\((--pos-[\w-]+)/g)) {
                expect(defined.has(name), `Undefined token ${name}`).toBe(true)
            }
            if (/(?:color|background)$/.test(declaration.prop)) {
                expect(declaration.value).toMatch(/var\(--pos-|^transparent$|^currentColor$/)
            }
        })
    })

    it('keeps logical positioning and mirrors only the decorative diagonal and action icon', () => {
        root.walkDecls(declaration => {
            expect(declaration.prop).not.toMatch(/^(?:left|right)$|(?:^|-)(?:left|right)(?:-|$)/)
            expect(declaration.important).toBeFalsy()
        })
        expect(css).not.toContain('[dir=')
        expect(css).toContain('.pos-login__brand:dir(rtl)::before')
        expect(css).toContain('.pos-login__submit:dir(rtl) .pos-button__icon')
    })
})
