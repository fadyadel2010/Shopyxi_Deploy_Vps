import { readFileSync } from 'node:fs'
import { resolve } from 'node:path'
import { describe, expect, it } from 'vitest'
const read = (file: string) => readFileSync(resolve(process.cwd(), 'src', file), 'utf8')

describe('Dashboard bounded presentation contract', () => {
    it('uses existing formatting/primitives and no shared canvas defaults', () => {
        const source = read('components/PosDashboardContent.jsx')
        expect(source).toContain('useSettings')
        expect(source).toContain('formatDateTime(sale.invoiceDate, locale)')
        expect(source).not.toMatch(/import (LineChart|BarChart|DonutChart)/)
        expect(source).not.toMatch(/stats\?\..*\|\| 0/)
        expect(source.indexOf("label={copy('todaySales')}")).toBeLessThan(source.indexOf("label={copy('saleJournals')}"))
        expect(source.indexOf("label={copy('saleJournals')}")).toBeLessThan(source.indexOf("label={copy('lowStockPositions')}"))
    })
    it('has page-owned scoped selectors, requested 768/1280 tiers and no raw colors/token declarations', () => {
        const css = read('components/dashboard/PosDashboard.css')
        expect(css).toContain('@media (min-width: 768px)')
        expect(css).toContain('@media (min-width: 1280px)')
        expect(css).not.toMatch(/#[0-9a-f]{3,8}\b|rgba?\(|hsla?\(|--pos-[\w-]+\s*:/i)
        for (const selector of css.matchAll(/(?:^|\n)([^@{}\n]+)\s*\{/g)) expect(selector[1].trim()).toMatch(/^\[data-product="pos"\] \.pos-dashboard/)
    })
    it('only adds Dashboard translations and keeps EN/AR keys aligned', () => {
        const en = JSON.parse(read('i18n/locales/en.json')).dashboard.phase42
        const ar = JSON.parse(read('i18n/locales/ar.json')).dashboard.phase42
        const keys = (obj: Record<string, unknown>, prefix = ''): string[] => Object.entries(obj).flatMap(([key, value]) => typeof value === 'object' ? keys(value as Record<string, unknown>, `${prefix}${key}.`) : [`${prefix}${key}`])
        expect(keys(en)).toEqual(keys(ar))
    })
    it('keeps root Storefront opt-in and direct/default consumers unopted', () => {
        expect(read('pages/Dashboard.jsx')).toContain('presentation="dashboard"')
        expect(read('pages/StorefrontDashboard.jsx')).not.toContain('presentation="dashboard"')
        expect(read('App.jsx')).not.toContain('presentation="dashboard"')
        expect(read('pages/StorefrontAnalyticsDashboard.jsx')).toContain("presentation === 'dashboard' ? <RootStorefrontAnalytics /> : <DefaultStorefrontAnalyticsDashboard key=")
        expect(read('components/StorefrontLiveMonitoring.jsx')).toContain("presentation === 'dashboard' ? <RootStorefrontLiveMonitoring /> : <DefaultStorefrontLiveMonitoring key=")
    })
})
