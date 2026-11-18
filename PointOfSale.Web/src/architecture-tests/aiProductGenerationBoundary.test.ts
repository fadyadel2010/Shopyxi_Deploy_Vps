import { readFileSync } from 'node:fs'
import { resolve } from 'node:path'
import { describe, expect, it } from 'vitest'

const readProjectFile = (path: string) => readFileSync(resolve(process.cwd(), path), 'utf8')

describe('AI product generation frontend boundary', () => {
    it('keeps the Cloudflare Web Analytics beacon out of the admin script policy', () => {
        const html = readProjectFile('index.html')
        const scriptSrc = html.match(/script-src[^;]*/)?.[0] ?? ''

        // The beacon's web-vitals build is the only source of the third-party
        // reportAllChanges/startTime failure. It must stay blocked on admin.
        expect(scriptSrc).not.toContain('static.cloudflareinsights.com')
        expect(scriptSrc).not.toContain('cloudflareinsights')
    })

    it('keeps reportAllChanges and startTime out of application diagnostics', () => {
        const diagnostics = readProjectFile('src/productionDiagnostics.js')
        const app = readProjectFile('src/App.jsx')
        const main = readProjectFile('src/main.jsx')
        const modal = readProjectFile('src/components/AiProductGeneratorModal.jsx')

        for (const source of [diagnostics, app, main, modal]) {
            expect(source).not.toContain('reportAllChanges')
            expect(source).not.toContain('startTime')
        }
    })

    it('sends generation only to the reviewed API endpoint', () => {
        const modal = readProjectFile('src/components/AiProductGeneratorModal.jsx')
        expect(modal).toContain("api.post('/admin/ai/products/generate'")
    })
})
