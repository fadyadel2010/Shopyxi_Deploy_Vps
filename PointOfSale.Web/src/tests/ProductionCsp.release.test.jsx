// @vitest-environment node
import { readFileSync } from 'node:fs'
import { resolve } from 'node:path'
import { describe, expect, it } from 'vitest'
import config from '../../vite.config.js'

const html = () => readFileSync(resolve(process.cwd(), 'index.html'), 'utf8')
const settings = () => config({ mode: 'production', command: 'build' })
const transform = () => settings().plugins.find(plugin => plugin.name === 'local-merchant-media-csp').transformIndexHtml

describe('production CSP excludes local development origins', () => {
    it('keeps production HTML and preview headers HTTPS-only for frames', () => {
        expect(html()).not.toContain('http://*.localhost:5174')
        expect(settings().preview.headers['Content-Security-Policy']).not.toContain('http://*.localhost:5174')
        expect(transform()(html(), {})).not.toContain('http://*.localhost:5174')
    })

    it('retains the local merchant image/frame allowances only for the dev server', () => {
        const developmentHtml = transform()(html(), { server: {} })
        expect(developmentHtml).toContain("img-src 'self' data: blob: https: http://*.localhost:5174")
        expect(developmentHtml).toContain("frame-src 'self' https: http://*.localhost:5174")
        expect(settings().server.headers['Content-Security-Policy']).toContain('http://*.localhost:5174')
    })
})
