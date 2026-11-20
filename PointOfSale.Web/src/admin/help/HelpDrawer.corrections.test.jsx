import { readFileSync, readdirSync } from 'node:fs'
import path from 'node:path'
import { act, cleanup, render, screen, waitFor } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import ReactMarkdown from 'react-markdown'
import HelpDrawer, { localeFirstHelpSections } from './HelpDrawer'
import i18n from '../../i18n/config'
import { renderPrimitive } from '../../design-system/test/renderPrimitive'

vi.mock('../../services/api', () => ({ default: { get: vi.fn(), post: vi.fn() } }))
const articleDir = path.resolve('src/admin/help/helpArticles')
const articles = readdirSync(articleDir).filter(f => f.endsWith('.md'))

afterEach(async () => {
    cleanup()
    await act(async () => { await i18n.changeLanguage('en') })
})

describe('Help live-review corrections', () => {
    it.each(['en', 'ar'])('omits raw route/query metadata and gives each block its own direction in %s', async lang => {
        await act(async () => { await i18n.changeLanguage(lang) })
        renderPrimitive(<HelpDrawer isOpen onClose={() => {}} articleKey="dashboard" pathname="/?technicalId=123" />)
        await waitFor(() => expect(document.querySelectorAll('.pos-help__md-p')).toHaveLength(10))
        expect(document.querySelector('.pos-help__meta')).toBeNull()
        expect(screen.queryByText('/?technicalId=123')).toBeNull()
        const md = document.querySelector('.pos-help__markdown')
        for (const el of md.querySelectorAll('h1,h2,h3,p,li,ul,ol')) expect(el).toHaveAttribute('dir', 'auto')
        expect([...md.querySelectorAll('h2')].map(e => e.textContent)).toEqual(lang === 'ar' ? ['العربية', 'English'] : ['English', 'العربية'])
    })

    it.each(articles)('reorders only complete AST sections without losing any rendered content: %s', file => {
        const source = readFileSync(`${articleDir}/${file}`, 'utf8')
        const view = render(<ReactMarkdown>{source}</ReactMarkdown>)
        const original = [...view.container.children].map(e => e.outerHTML)
        view.rerender(<ReactMarkdown remarkPlugins={[[localeFirstHelpSections, { locale: 'ar' }]]}>{source}</ReactMarkdown>)
        const reordered = [...view.container.children].map(e => e.outerHTML)
        expect([...view.container.querySelectorAll('h2')].map(e => e.textContent)).toEqual(['العربية', 'English'])
        expect([...reordered].sort()).toEqual([...original].sort())
        expect(view.container.firstElementChild.outerHTML).toEqual(original[0])
        expect(view.container.querySelectorAll('hr')).toHaveLength(1)
        view.rerender(<ReactMarkdown remarkPlugins={[[localeFirstHelpSections, { locale: 'en' }]]}>{source}</ReactMarkdown>)
        expect([...view.container.children].map(e => e.outerHTML)).toEqual(original)
        view.unmount()
    })

    it.each(['en', 'ar'])('keeps real bilingual lists and Latin control names readable in %s', async lang => {
        await act(async () => { await i18n.changeLanguage(lang) })
        renderPrimitive(<HelpDrawer isOpen onClose={() => {}} articleKey="bank-statement" pathname="/reports/bank-statement" />)
        await waitFor(() => expect(document.querySelectorAll('.pos-help__markdown li').length).toBeGreaterThan(0))
        const md = document.querySelector('.pos-help__markdown')
        for (const el of md.querySelectorAll('ol,ul,li')) expect(el).toHaveAttribute('dir', 'auto')
        expect([...md.querySelectorAll('code')].filter(e => e.textContent === 'Generate Report')).toHaveLength(2)
        expect(md.textContent).toContain('اضغط')
        expect(md.textContent).toContain('Click')
    })

    it.each([
        '# Title\n\n## English\n\nText.\n\n## العربية\n\nنص.',
        '# Title\n\n## English\n\nText.\n\n---\n\n## العربية\n\nنص.\n\n## Extra\n\nRetain me.',
        '# Title\n\n## English\n\nText.\n\n---\n\n## العربية\n\nنص.\n\n---\n\nMore.',
        '# Title\n\n## Other\n\nText.\n\n---\n\n## العربية\n\nنص.',
        '# Title\n\n```\n## English\n---\n## العربية\n```\n',
    ])('leaves unknown or ambiguous structures exactly as rendered originally', source => {
        const view = render(<ReactMarkdown>{source}</ReactMarkdown>)
        const original = view.container.innerHTML
        view.rerender(<ReactMarkdown remarkPlugins={[[localeFirstHelpSections, { locale: 'ar' }]]}>{source}</ReactMarkdown>)
        expect(view.container.innerHTML).toEqual(original)
    })
})
