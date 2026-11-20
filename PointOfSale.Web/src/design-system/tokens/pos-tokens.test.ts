import { readFileSync } from 'node:fs'
import { resolve } from 'node:path'
import { describe, expect, it } from 'vitest'

const tokenDir = resolve(process.cwd(), 'src/design-system/tokens')
const webSrc = resolve(process.cwd(), 'src')

const tokenFiles = {
  primitives: 'pos-primitives.css',
  semantic: 'pos-semantic.css',
  components: 'pos-components.css',
  layout: 'pos-layout.css',
  entry: 'pos-tokens.css',
} as const

type TokenFileKey = keyof typeof tokenFiles

type Declaration = {
  file: TokenFileKey
  name: string
  value: string
  context: string[]
}

const read = (path: string) => readFileSync(path, 'utf8')
const readToken = (key: TokenFileKey) => read(resolve(tokenDir, tokenFiles[key]))
const stripComments = (css: string) => css.replace(/\/\*[\s\S]*?\*\//g, '')

/**
 * Minimal CSS block reader for the token files. It records custom-property
 * declarations with their selector and at-rule context so scoped overrides
 * (media queries and :lang) are distinguishable from duplicate definitions.
 */
function parseDeclarations(file: TokenFileKey, css: string): Declaration[] {
  const source = stripComments(css)
  const declarations: Declaration[] = []
  const stack: string[] = []
  let buffer = ''

  const record = (statement: string) => {
    if (statement.startsWith('@import')) {
      return
    }

    const match = statement.match(/^(--pos-[\w-]+)\s*:\s*([\s\S]+)$/)
    if (!match) {
      return
    }

    declarations.push({
      file,
      name: match[1],
      value: match[2].trim(),
      context: [...stack],
    })
  }

  for (const char of source) {
    if (char === '{') {
      stack.push(buffer.trim())
      buffer = ''
    } else if (char === '}') {
      const statement = buffer.trim()
      if (statement) {
        record(statement)
      }
      buffer = ''
      stack.pop()
    } else if (char === ';') {
      const statement = buffer.trim()
      if (statement) {
        record(statement)
      }
      buffer = ''
    } else {
      buffer += char
    }
  }

  return declarations
}

const layers = (Object.keys(tokenFiles) as TokenFileKey[]).filter((key) => key !== 'entry')
const declarations = layers.flatMap((key) => parseDeclarations(key, readToken(key)))
const definedTokens = new Set(declarations.map((declaration) => declaration.name))

const findToken = (name: string) => declarations.find((declaration) => declaration.name === name)

describe('SHOPYXI POS design tokens', () => {
  it('keeps every token declaration scoped beneath the POS product boundary', () => {
    expect(declarations.length).toBeGreaterThan(0)

    for (const declaration of declarations) {
      expect(
        declaration.context.some((context) => context.includes('[data-product="pos"]')),
        `${declaration.file}:${declaration.name} is not scoped to [data-product="pos"]`,
      ).toBe(true)
    }

    for (const key of layers) {
      expect(readToken(key)).not.toMatch(/(^|\n)\s*:root\b/)
    }
  })

  it('contains no duplicate token definitions in the same context', () => {
    const seen = new Map<string, Declaration>()
    const duplicates: string[] = []

    for (const declaration of declarations) {
      const key = `${declaration.context.join(' > ')}|${declaration.name}`
      if (seen.has(key)) {
        duplicates.push(`${declaration.file}:${declaration.name} (${declaration.context.join(' > ') || 'top level'})`)
      }
      seen.set(key, declaration)
    }

    expect(duplicates).toEqual([])
  })

  it('defines every referenced token', () => {
    const missing: string[] = []

    for (const declaration of declarations) {
      for (const match of declaration.value.matchAll(/var\((--pos-[\w-]+)/g)) {
        if (!definedTokens.has(match[1])) {
          missing.push(`${declaration.file}:${declaration.name} -> ${match[1]}`)
        }
      }
    }

    expect(missing).toEqual([])
  })

  it('represents the approved primary brand color exactly', () => {
    expect(findToken('--pos-blue-600')?.value.toLowerCase()).toBe('#356ae6')
    expect(findToken('--pos-color-action-primary')?.value).toBe('var(--pos-blue-600)')
    expect(findToken('--pos-button-primary-bg')?.value).toBe('var(--pos-color-action-primary)')
  })

  it('keeps raw color literals inside the primitive layer only', () => {
    for (const declaration of declarations.filter((entry) => entry.file !== 'primitives')) {
      expect(
        declaration.value,
        `${declaration.file}:${declaration.name} contains a raw color literal`,
      ).not.toMatch(/#[0-9a-f]{3,8}\b/i)
    }
  })

  it('does not reference unavailable font assets', () => {
    const primitives = stripComments(readToken('primitives'))

    expect(primitives).not.toMatch(/\bInter\b/)
    expect(primitives).not.toMatch(/IBM Plex Sans Arabic/)
  })

  it('keeps the required semantic and component token contracts present', () => {
    const required = [
      '--pos-color-bg-canvas',
      '--pos-color-surface',
      '--pos-color-surface-elevated',
      '--pos-color-text-primary',
      '--pos-color-text-secondary',
      '--pos-color-text-muted',
      '--pos-color-border-subtle',
      '--pos-color-border-default',
      '--pos-color-border-strong',
      '--pos-color-action-primary',
      '--pos-color-action-primary-hover',
      '--pos-color-action-primary-active',
      '--pos-color-focus-ring',
      '--pos-color-success',
      '--pos-color-warning',
      '--pos-color-danger',
      '--pos-color-info',
      '--pos-button-primary-bg',
      '--pos-button-primary-bg-hover',
      '--pos-button-secondary-bg',
      '--pos-button-danger-bg',
      '--pos-input-bg',
      '--pos-input-border',
      '--pos-input-border-focus',
      '--pos-input-border-invalid',
      '--pos-card-bg',
      '--pos-card-border',
      '--pos-nav-bg',
      '--pos-sidebar-bg',
      '--pos-sidebar-item-active-bg',
      '--pos-sidebar-item-active-text',
      '--pos-table-header-bg',
      '--pos-table-row-hover-bg',
      '--pos-dialog-bg',
      '--pos-dialog-overlay',
      '--pos-badge-success-bg',
      '--pos-badge-warning-bg',
      '--pos-badge-danger-bg',
      '--pos-badge-info-bg',
      '--pos-font-family-ui',
      '--pos-font-size-body',
      '--pos-line-height-body',
      '--pos-font-size-data',
      '--pos-font-variant-numeric',
      '--pos-header-height',
      '--pos-sidebar-width',
      '--pos-content-inset',
    ]

    expect(required.filter((name) => !definedTokens.has(name))).toEqual([])
  })

  it('imports the token layers in dependency order', () => {
    const entry = readToken('entry')

    expect(entry.indexOf("@import './pos-primitives.css';")).toBeGreaterThanOrEqual(0)
    expect(entry.indexOf("@import './pos-primitives.css';")).toBeLessThan(entry.indexOf("@import './pos-semantic.css';"))
    expect(entry.indexOf("@import './pos-semantic.css';")).toBeLessThan(entry.indexOf("@import './pos-components.css';"))
    expect(entry.indexOf("@import './pos-components.css';")).toBeLessThan(entry.indexOf("@import './pos-layout.css';"))
  })

  it('avoids physical-direction properties in the foundation layer', () => {
    for (const key of layers) {
      const source = stripComments(readToken(key))

      expect(source).not.toMatch(/(margin|padding|border)-(left|right)\s*:/)
      expect(source).not.toMatch(/(^|\n)\s*(left|right)\s*:/)
      expect(source).not.toMatch(/\btext-(left|right)\b/)
    }
  })

  it('keeps the POS root markers and stylesheet import in place', () => {
    const layout = read(resolve(webSrc, 'components/Layout.jsx'))
    const login = read(resolve(webSrc, 'pages/Login.jsx'))
    const main = read(resolve(webSrc, 'main.jsx'))

    expect(layout).toContain('data-product="pos"')
    expect(login).toContain('data-product="pos"')
    expect(main.indexOf("import './design-system/tokens/pos-tokens.css'"))
      .toBeGreaterThan(main.indexOf("import './index.css'"))
  })
})
