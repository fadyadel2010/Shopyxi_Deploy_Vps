import DOMPurify from 'dompurify'
import { describe, expect, it } from 'vitest'
import { sanitizeRichTextHtml } from './htmlSanitizer'
import { sanitizeRichTextHtml as sanitizeStorefrontHtml } from '../../../PointOfSale.Storefront/src/utils/htmlSanitizer'

describe('npm sanitizer security compatibility', () => {
    // GHSA-cg2g-xh64-v4q7: never evaluate the resulting content. Checking the
    // split delimiter is sufficient to exercise the patched shadow-root walk.
    it.each([
        ['$', '{securityRegressionSentinel}'],
        ['{', '{securityRegressionSentinel}}'],
        ['<', '%securityRegressionSentinel%>'],
    ])('scrubs template fragments across removed shadow-root elements (%s)', (left, right) => {
        const host = document.createElement('div')
        const shadow = host.attachShadow({ mode: 'open' })
        shadow.append(document.createTextNode(left), document.createElement('object'), document.createTextNode(right))

        DOMPurify.sanitize(host, { IN_PLACE: true, SAFE_FOR_TEMPLATES: true })

        expect(shadow.textContent).not.toContain(`${left}${right}`)
        expect(shadow.querySelector('object')).toBeNull()
    })

    for (const [name, sanitize] of [['Web', sanitizeRichTextHtml], ['Storefront', sanitizeStorefrontHtml]] as const) {
        it(`${name} retains safe content and refuses executable markup with the installed sanitizer`, () => {
            const result = sanitize('<p>Safe text</p><a href="/catalog" target="_blank">Catalog</a><img src=x onerror=alert(1)><script>alert(1)</script>')
            expect(result).toContain('<p>Safe text</p>')
            expect(result).toContain('href="/catalog"')
            expect(result).toContain('noopener')
            expect(result).toContain('noreferrer')
            expect(result).not.toContain('onerror')
            expect(result).not.toContain('<script')
            expect(result).not.toContain('<img')
        })
    }
})
