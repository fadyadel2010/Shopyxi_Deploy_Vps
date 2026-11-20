import { act, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import { ConfirmDialogProvider } from '../../common/confirm/ConfirmDialogProvider'
import i18n from '../../i18n/config'
import { MemoryRouter, Route, Routes } from 'react-router-dom'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

const service = vi.hoisted(() => ({
    createPage: vi.fn(),
    getPage: vi.fn(),
    getPageVersion: (page: { draft?: { version?: number } | null; published?: { version?: number } | null } | null) => page?.draft?.version ?? page?.published?.version ?? null,
    isConcurrencyConflict: (error: { status?: number; code?: string } | null) => (
        error?.status === 409 && error?.code === 'MARKETPLACE_CMS_CONCURRENCY_CONFLICT'
    ),
    previewPage: vi.fn(),
    publishPage: vi.fn(),
    translateContent: vi.fn(),
    updatePage: vi.fn(),
}))

vi.mock('./marketplaceCmsService', () => ({ marketplaceCmsService: service }))
vi.mock('@/utils/htmlSanitizer', () => ({ sanitizeRichTextHtml: (value: string) => value }))

import { MarketplaceCmsPageEditor } from './MarketplaceCmsPageEditor'

const pageId = '11111111-1111-4111-8111-111111111111'

function draftResponse(version: number) {
    return pageResponse({ draft: version, published: null })
}

function publishedResponse(version: number) {
    return pageResponse({ draft: null, published: version })
}

function pageResponse(versions: { draft: number | null, published: number | null }) {
    const createVersion = (version: number) => ({
        version,
        slug: 'authority',
        title: 'Authority',
        titleLocalized: { en: 'Authority', ar: '' },
        contentType: 'RichText',
        content: `<p>v${version}</p>`,
        localizedContent: { en: `<p>v${version}</p>` },
        seo: {},
    })

    return {
        id: pageId,
        slug: 'authority',
        title: 'Authority',
        titleLocalized: { en: 'Authority', ar: '' },
        contentType: 'RichText',
        hasPendingChanges: versions.draft != null,
        draft: versions.draft == null ? null : createVersion(versions.draft),
        published: versions.published == null ? null : createVersion(versions.published),
    }
}

function renderEditor(entry: string) {
    return render(
        <MemoryRouter initialEntries={[entry]}>
            <ConfirmDialogProvider><Routes>
                <Route path="/super-admin/marketplace/pages/:pageId" element={<MarketplaceCmsPageEditor />} />
            </Routes></ConfirmDialogProvider>
        </MemoryRouter>,
    )
}

describe('MarketplaceCmsPageEditor version adoption', () => {
    beforeEach(async () => {
        vi.clearAllMocks()
        await i18n.changeLanguage('en')
    })

    afterEach(() => {
        vi.restoreAllMocks()
    })

    it('uses create response version evidence for an immediate publish', async () => {
        const created = draftResponse(1)
        service.createPage.mockResolvedValue(created)
        service.getPage.mockResolvedValue(created)
        service.publishPage.mockResolvedValue(publishedResponse(1))
        const { container } = renderEditor('/super-admin/marketplace/pages/new')
        const inputs = container.querySelectorAll('input')

        fireEvent.change(inputs[0], { target: { value: 'Authority' } })
        fireEvent.change(inputs[1], { target: { value: 'authority' } })
        fireEvent.click(screen.getByRole('button', { name: 'Publish' }))
        fireEvent.click(within(await screen.findByRole('dialog')).getByRole('button', { name: 'Publish' }))

        await waitFor(() => expect(service.createPage).toHaveBeenCalledTimes(1))
        await waitFor(() => expect(service.publishPage).toHaveBeenCalledWith(pageId, 1))
    })

    it('adopts each update response before repeated update and publish', async () => {
        service.getPage.mockResolvedValue(draftResponse(1))
        service.updatePage
            .mockResolvedValueOnce(draftResponse(2))
            .mockResolvedValueOnce(draftResponse(3))
        service.publishPage.mockResolvedValue(publishedResponse(3))
        renderEditor(`/super-admin/marketplace/pages/${pageId}`)

        const slug = await screen.findByDisplayValue('authority')
        fireEvent.change(slug, { target: { value: 'authority-two' } })
        fireEvent.click(screen.getByRole('button', { name: 'Save Draft' }))
        await waitFor(() => expect(service.updatePage).toHaveBeenCalledTimes(1))
        fireEvent.change(screen.getByDisplayValue('authority'), { target: { value: 'authority-three' } })
        fireEvent.click(screen.getByRole('button', { name: 'Save Draft' }))
        await waitFor(() => expect(service.updatePage).toHaveBeenCalledTimes(2))
        fireEvent.click(screen.getByRole('button', { name: 'Publish' }))
        fireEvent.click(within(await screen.findByRole('dialog')).getByRole('button', { name: 'Publish' }))
        await waitFor(() => expect(service.publishPage).toHaveBeenCalledTimes(1))

        expect(service.updatePage.mock.calls[0][2]).toBe(1)
        expect(service.updatePage.mock.calls[1][2]).toBe(2)
        expect(service.publishPage).toHaveBeenCalledWith(pageId, 3)
    })

    it('refreshes and adopts the authoritative version after a conflict', async () => {
        const conflict = Object.assign(new Error('This page was modified by another admin.'), {
            status: 409,
            code: 'MARKETPLACE_CMS_CONCURRENCY_CONFLICT',
        })
        service.getPage.mockResolvedValueOnce(draftResponse(1)).mockResolvedValueOnce(draftResponse(4))
        service.updatePage.mockRejectedValueOnce(conflict).mockResolvedValueOnce(draftResponse(5))
        renderEditor(`/super-admin/marketplace/pages/${pageId}`)

        const slug = await screen.findByDisplayValue('authority')
        fireEvent.change(slug, { target: { value: 'my-draft' } })
        fireEvent.click(screen.getByRole('button', { name: 'Save Draft' }))
        await screen.findByText(/latest page version has been loaded/i)
        fireEvent.click(screen.getByRole('button', { name: 'Save Draft' }))
        await waitFor(() => expect(service.updatePage).toHaveBeenCalledTimes(2))

        expect(service.getPage).toHaveBeenCalledTimes(2)
        expect(service.updatePage.mock.calls[0][2]).toBe(1)
        expect(service.updatePage.mock.calls[1][2]).toBe(4)
    })

    it('keeps preview isolated while unsaved changes are present', async () => {
        service.getPage.mockResolvedValue(draftResponse(1))
        renderEditor(`/super-admin/marketplace/pages/${pageId}`)

        const slug = await screen.findByDisplayValue('authority')
        fireEvent.change(slug, { target: { value: 'preview-draft' } })
        expect(screen.getByText('Unsaved changes')).toBeInTheDocument()
        fireEvent.click(screen.getByRole('button', { name: 'Preview' }))

        expect(await screen.findByText(/Preview never saves or changes versions/)).toBeInTheDocument()
        expect(service.previewPage).not.toHaveBeenCalled()
        expect(service.updatePage).not.toHaveBeenCalled()
    })

    it.each(['new', pageId])('publish Cancel never creates or updates a dirty draft (%s)', async id => {
        service.getPage.mockResolvedValue(draftResponse(1))
        const { container } = renderEditor(`/super-admin/marketplace/pages/${id}`)
        if (id !== 'new') await screen.findByDisplayValue('authority')
        const inputs = container.querySelectorAll('input')
        fireEvent.change(inputs[0], { target: { value: 'Unsaved' } })
        fireEvent.change(inputs[1], { target: { value: 'unsaved' } })
        fireEvent.click(screen.getByRole('button', { name: 'Publish' }))
        const dialog = await screen.findByRole('dialog')
        expect(service.createPage).not.toHaveBeenCalled()
        expect(service.updatePage).not.toHaveBeenCalled()
        fireEvent.click(within(dialog).getByRole('button', { name: 'Cancel' }))
        expect(service.publishPage).not.toHaveBeenCalled()
        expect(screen.getByDisplayValue('Unsaved')).toBeInTheDocument()
    })

    it('holds the same publish confirmation through save and publish settlement', async () => {
        service.getPage.mockResolvedValue(draftResponse(1))
        let settle!: (value: unknown) => void
        service.updatePage.mockReturnValue(new Promise(resolve => { settle = resolve }))
        service.publishPage.mockResolvedValue(publishedResponse(2))
        renderEditor(`/super-admin/marketplace/pages/${pageId}`)
        fireEvent.change(await screen.findByDisplayValue('authority'), { target: { value: 'updated' } })
        const publish = screen.getByRole('button', { name: 'Publish' })
        act(() => { fireEvent.click(publish); fireEvent.click(publish) })
        const dialog = await screen.findByRole('dialog')
        const confirm = within(dialog).getByRole('button', { name: 'Publish' })
        act(() => { fireEvent.click(confirm); fireEvent.click(confirm) })
        expect(service.updatePage).toHaveBeenCalledTimes(1)
        fireEvent.keyDown(window, { key: 'Escape' })
        expect(dialog).toBeInTheDocument()
        await act(async () => { settle(draftResponse(2)) })
        await waitFor(() => expect(service.publishPage).toHaveBeenCalledWith(pageId, 2))
        await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull())
    })

    it('retains the canonical created draft after publish failure, then edits by versioned update instead of another create', async () => {
        service.createPage.mockResolvedValue(draftResponse(1))
        service.publishPage.mockRejectedValue(new Error('SQL private diagnostic'))
        service.updatePage.mockResolvedValue(draftResponse(2))
        const { container } = renderEditor('/super-admin/marketplace/pages/new')
        const inputs = container.querySelectorAll('input')
        fireEvent.change(inputs[0], { target: { value: 'Authority' } })
        fireEvent.change(inputs[1], { target: { value: 'authority' } })
        fireEvent.click(screen.getByRole('button', { name: 'Publish' }))
        fireEvent.click(within(await screen.findByRole('dialog')).getByRole('button', { name: 'Publish' }))
        await screen.findByText(/current draft is retained/i)
        expect(screen.queryByText(/SQL private/)).toBeNull()
        await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull())
        fireEvent.change(screen.getByDisplayValue('authority'), { target: { value: 'recovered-draft' } })
        fireEvent.click(screen.getByRole('button', { name: 'Save Draft' }))
        await waitFor(() => expect(service.updatePage).toHaveBeenCalledWith(pageId, expect.anything(), 1))
        expect(service.createPage).toHaveBeenCalledTimes(1)
    })
})
