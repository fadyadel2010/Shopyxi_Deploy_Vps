import { readdirSync, readFileSync, statSync } from 'node:fs'
import { extname, join, relative, resolve } from 'node:path'
import { describe, expect, it } from 'vitest'

const webSrc = resolve(process.cwd(), 'src')
const primitiveDir = resolve(webSrc, 'design-system/primitives')
const tokenDir = resolve(webSrc, 'design-system/tokens')

function walk(dir: string): string[] {
    return readdirSync(dir).flatMap((entry) => {
        const full = join(dir, entry)
        return statSync(full).isDirectory() ? walk(full) : [full]
    })
}

const stripComments = (css: string) => css.replace(/\/\*[\s\S]*?\*\//g, '')

const primitiveFiles = walk(primitiveDir)

const cssFiles = primitiveFiles.filter((file) => extname(file) === '.css')
cssFiles.push(resolve(webSrc, 'design-system/providers/posProviderBridge.css'))
const jsxFiles = primitiveFiles.filter((file) => ['.jsx', '.js'].includes(extname(file)))

const read = (file: string) => readFileSync(file, 'utf8')

const tokenCss = ['pos-primitives.css', 'pos-semantic.css', 'pos-components.css', 'pos-layout.css']
    .map((file) => read(resolve(tokenDir, file)))
    .join('\n')

const definedTokens = new Set(
    [...stripComments(tokenCss + cssFiles.map(read).join('\n')).matchAll(/(--pos-[\w-]+)\s*:/g)].map((match) => match[1]),
)

const stripKeyframes = (css: string) => css.replace(/@keyframes[^{]+\{(?:[^{}]*\{[^{}]*\})*[^{}]*\}/g, '')

const selectors = cssFiles.flatMap((file) => {
    const source = stripKeyframes(stripComments(read(file)))
    return [...source.matchAll(/([^{}]+)\{/g)]
        .map((match) => match[1].trim())
        .filter((selector) => selector && !selector.startsWith('@'))
        .flatMap((selector) => selector.split(',').map((part) => ({ file, selector: part.trim() })))
})

describe('POS primitive layer architecture', () => {
    it('keeps every primitive style scoped to the POS product boundary', () => {
        expect(selectors.length).toBeGreaterThan(0)

        const unscoped = selectors
            .filter(({ selector }) => !selector.includes('[data-product="pos"]'))
            .map(({ file, selector }) => `${relative(webSrc, file)} :: ${selector}`)

        expect(unscoped).toEqual([])
    })

    it('defines every token referenced by primitive styles', () => {
        const missing: string[] = []

        for (const file of cssFiles) {
            for (const match of stripComments(read(file)).matchAll(/var\((--pos-[\w-]+)/g)) {
                if (!definedTokens.has(match[1])) {
                    missing.push(`${relative(webSrc, file)} -> ${match[1]}`)
                }
            }
        }

        expect(missing).toEqual([])
    })

    it('uses no raw color literals or physical direction properties in primitive styles', () => {
        for (const file of cssFiles) {
            const source = stripKeyframes(stripComments(read(file)))
            expect(source, `${relative(webSrc, file)} contains a raw color literal`).not.toMatch(/#[0-9a-f]{3,8}\b/i)
            expect(source, `${relative(webSrc, file)} contains rgb()/hsl()`).not.toMatch(/\b(rgb|hsl)a?\(/)
            expect(source, `${relative(webSrc, file)} uses a physical margin/padding/border side`).not.toMatch(
                /(margin|padding|border)-(left|right)\s*:/,
            )
            expect(source, `${relative(webSrc, file)} uses a physical left/right position`).not.toMatch(
                /(^|\n)\s*(left|right)\s*:/,
            )
        }
    })

    it('keeps primitive modules inside the design-system and free of app services', () => {
        const forbidden = jsxFiles
            .filter((file) => !relative(primitiveDir, file).includes('test'))
            .flatMap((file) => {
                const source = read(file)
                return [...source.matchAll(/from\s+['"]([^'"]+)['"]/g)]
                    .map((match) => match[1])
                    .filter((specifier) => specifier.startsWith('.'))
                    .map((specifier) => ({ file, specifier }))
            })
            .filter(({ file, specifier }) => {
                const target = resolve(file, '..', specifier)
                const insidePrimitives = !relative(primitiveDir, target).startsWith('..')
                const insideDesignSystem = !relative(resolve(webSrc, 'design-system'), target).startsWith('..')
                return !insidePrimitives && !insideDesignSystem
            })
            .map(({ file, specifier }) => `${relative(webSrc, file)} -> ${specifier}`)

        expect(forbidden).toEqual([])
    })

    it('does not hardcode Tailwind status colors inside primitive modules', () => {
        const offenders = jsxFiles
            .filter((file) => !relative(primitiveDir, file).includes('test'))
            .filter((file) => /\b(bg|text|border)-(green|red|yellow|amber|blue|sky|indigo|slate|gray)-\d{2,3}\b/.test(read(file)))
            .map((file) => relative(webSrc, file))

        expect(offenders).toEqual([])
    })

    it('imports a stylesheet from every styled primitive module', () => {
        const modules = jsxFiles.filter((file) => {
            const name = file.replace(/\\/g, '/')
            return /(Button|IconButton|Field|Input|Select|Card|Badge|Table|Dialog|Tabs|Tooltip)\.jsx$/.test(name)
        })
        expect(modules.length).toBeGreaterThanOrEqual(11)

        for (const file of modules) {
            const expected = file.replace(/\.jsx$/, '.css').replace(/\\/g, '/')
            expect(cssFiles.map((css) => css.replace(/\\/g, '/'))).toContain(expected)
        }
    })
})
