import { createRequire } from 'node:module'
import { readFileSync } from 'node:fs'
import { describe, expect, it } from 'vitest'

describe('low-level HTTP transport import authority', () => {
    it('keeps the exception exact and all ordinary imports prohibited', () => {
        const require = createRequire(import.meta.url)
        const config = require('../../.eslintrc.cjs')
        expect(config.rules['no-restricted-imports'][0]).toBe('error')
        expect(config.rules['no-restricted-imports'][1].paths).toContainEqual(expect.objectContaining({ name: 'axios' }))
        const adapter = config.overrides.find((entry: { files: string[] }) => entry.files.includes('src/core/httpTransport.ts'))
        expect(adapter.files).toEqual(['src/core/httpTransport.ts'])
        expect(adapter.rules['no-restricted-imports'][0]).toBe('error')
        expect(adapter.rules['no-restricted-imports'][1].paths).toContainEqual(expect.objectContaining({ name: '@microsoft/signalr' }))
        expect(adapter.rules['no-restricted-imports'][1].patterns).toContainEqual(expect.objectContaining({ group: ['**/api/marketplaceApi'] }))
        expect(readFileSync('src/core/httpTransport.ts', 'utf8')).toContain("export { default } from 'axios'")
    })
})
