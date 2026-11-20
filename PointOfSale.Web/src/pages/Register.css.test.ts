import { readFileSync } from 'node:fs'
import { resolve } from 'node:path'
import postcss from 'postcss'
import { describe, expect, it } from 'vitest'

const css = readFileSync(resolve('src/pages/Register.css'), 'utf8')
const root = postcss.parse(css)
const tokens = ['primitives', 'semantic', 'components', 'layout']
    .map(name => readFileSync(resolve(`src/design-system/tokens/pos-${name}.css`), 'utf8')).join('\n')
const defined = new Set([...tokens.matchAll(/(--pos-[\w-]+)\s*:/g)].map(match => match[1]))

describe('Register stylesheet governance', () => {
    it('scopes every selector to POS and the Register namespace', () => {
        root.walkRules(rule => {
            for (const selector of rule.selectors) {
                expect(selector).toMatch(/^\[data-product="pos"\](?:\.pos-register|\s+\.pos-register__)/)
            }
        })
    })
    it('uses existing tokens, logical properties and no raw palette or overrides', () => {
        root.walkDecls(declaration => {
            expect(declaration.prop).not.toMatch(/^--|^(?:left|right)$|(?:^|-)(?:left|right)(?:-|$)/)
            expect(declaration.value).not.toMatch(/#[0-9a-f]{3,8}\b|\b(?:rgb|rgba|hsl|hsla)\(/i)
            expect(declaration.important).toBeFalsy()
            for (const [, name] of declaration.value.matchAll(/var\((--pos-[\w-]+)/g)) {
                expect(defined.has(name), name).toBe(true)
            }
        })
        expect(css).not.toMatch(/@import|\[dir=|gradient|overflow:\s*hidden/)
    })
})
