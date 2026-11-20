import { readFileSync } from 'node:fs'
import { resolve } from 'node:path'
import { describe, expect, it } from 'vitest'
import i18n from '../i18n/config'
import en from '../i18n/locales/en.json'
import ar from '../i18n/locales/ar.json'

const read = (name: string) => readFileSync(resolve(process.cwd(), 'src', name), 'utf8')
function leaves(value: unknown, prefix = ''): Record<string, string> {
    if (typeof value === 'string') return { [prefix]: value }
    if (!value || typeof value !== 'object') return {}
    return Object.assign({}, ...Object.entries(value).map(([key, item]) => leaves(item, prefix ? `${prefix}.${key}` : key)))
}
describe('Group 6 bounded frontend contracts', () => {
    it('preserves Web EN/AR leaf keys and interpolation placeholders', () => {
        const english = leaves(en)
        const arabic = leaves(ar)
        expect(Object.keys(english).sort()).toEqual(Object.keys(arabic).sort())
        for (const key of Object.keys(english)) {
            const params = (text: string) => [...text.matchAll(/\{\{\s*([^},]+)(?:,[^}]+)?\s*\}\}/g)].map(match => match[1].trim()).sort()
            expect(params(english[key]), key).toEqual(params(arabic[key]))
        }
    })
    it.each([0, 1, 2, 3, 11, 100])('resolves the two previously partial Arabic plural families for %s', count => {
        for (const key of ['commerceHub.variantCount', 'servicesMarketplace.review.ratingOption']) {
            const result = i18n.getFixedT('ar')(key, { count, formattedCount: String(count) })
            expect(result).toMatch(/[\u0600-\u06ff]/)
            expect(result).not.toMatch(/variants|stars/)
        }
    })
    it('uses logical Categories spacing without mirroring financial trend geometry', () => {
        const categories = read('pages/Categories.jsx')
        expect(categories).toContain("'ms-6 border-s-2 border-primary-200'")
        expect(categories).not.toMatch(/isRTL\s*\?\s*'m[lr]-/)
        const trend = read('components/dashboard/DashboardSalesTrend.jsx')
        expect(trend).toContain('dir="ltr"')
        expect(trend).not.toContain('scaleX(-1)')
    })
    it('preserves semantic recent-customer, role and explicit-currency contracts', () => {
        const dashboard = read('components/PosDashboardContent.jsx')
        expect(dashboard).toContain('sale.isWalkInCustomer === true')
        expect(dashboard).toContain("hasPosPermission(user, 'finance.view')")
        expect(dashboard).toContain("hasPosPermission(user, 'sales.view')")
        expect(read('utils/dateFormat.js')).not.toContain("currency || DEFAULT_CURRENCY_CODE")
        expect(read('contexts/SettingsContext.jsx')).toContain("setCurrencyScope(null)")
    })
})
