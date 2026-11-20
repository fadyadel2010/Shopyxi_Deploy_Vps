import { act, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import { ConfirmDialogProvider } from '../../common/confirm/ConfirmDialogProvider'
import i18n from '../../i18n/config'
import { MemoryRouter } from 'react-router-dom'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

const service = vi.hoisted(() => ({
    deletePage: vi.fn(),
    getListItemVersion: (page: { draftVersion?: number | null; publishedVersion?: number | null } | null) => page?.draftVersion ?? page?.publishedVersion ?? null,
    isConcurrencyConflict: (error: { status?: number; code?: string } | null) => (
        error?.status === 409 && error?.code === 'MARKETPLACE_CMS_CONCURRENCY_CONFLICT'
    ),
    listPages: vi.fn(),
    previewPage: vi.fn(),
    publishPage: vi.fn(),
}))

vi.mock('./marketplaceCmsService', () => ({ marketplaceCmsService: service }))

import { MarketplaceCmsPagesPage } from './MarketplaceCmsPagesPage'

const pageId = '11111111-1111-4111-8111-111111111111'

function listItem(draftVersion: number | null, publishedVersion: number | null = null) {
    return {
        id: pageId,
        slug: 'authority',
        title: 'Authority',
        contentType: 'RichText',
        hasDraft: draftVersion != null,
        hasPublished: publishedVersion != null,
        hasPendingChanges: draftVersion != null,
        status: draftVersion != null ? 'Draft' : 'Published',
        draftVersion,
        publishedVersion,
        updatedAtUtc: '2026-08-03T00:00:00Z',
        publishedAtUtc: publishedVersion == null ? null : '2026-08-03T00:00:00Z',
    }
}

function publishedResponse(version: number) {
    return {
        ...listItem(null, version),
        draft: null,
        published: { version },
    }
}

function renderPage() {
    return render(
        <MemoryRouter>
            <ConfirmDialogProvider><MarketplaceCmsPagesPage /></ConfirmDialogProvider>
        </MemoryRouter>,
    )
}

describe('MarketplaceCmsPagesPage version adoption', () => {
    beforeEach(async () => {
        vi.clearAllMocks()
        await i18n.changeLanguage('en')
    })

    afterEach(() => {
        vi.restoreAllMocks()
    })

    it('adopts the publish response version before delete', async () => {
        service.listPages.mockResolvedValue([listItem(3)])
        service.publishPage.mockResolvedValue(publishedResponse(3))
        service.deletePage.mockResolvedValue({ id: pageId, deleted: true })
        renderPage()

        expect(await screen.findByText('Authority')).toBeInTheDocument()
        fireEvent.click(screen.getByTitle('Publish'))
        fireEvent.click(within(await screen.findByRole('dialog')).getByRole('button', { name: 'Publish' }))
        await waitFor(() => expect(service.publishPage).toHaveBeenCalledWith(pageId, 3))
        await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull())
        fireEvent.click(screen.getByTitle('Delete'))
        fireEvent.click(within(await screen.findByRole('dialog')).getByRole('button', { name: 'Delete' }))
        await waitFor(() => expect(service.deletePage).toHaveBeenCalledWith(pageId, 3))

        expect(screen.queryByText('Authority')).not.toBeInTheDocument()
    })

    it('uses the latest version returned by refresh for the next mutation', async () => {
        service.listPages.mockResolvedValueOnce([listItem(1)]).mockResolvedValueOnce([listItem(2)])
        service.publishPage.mockResolvedValue(publishedResponse(2))
        renderPage()

        expect(await screen.findByText('Authority')).toBeInTheDocument()
        fireEvent.click(screen.getByRole('button', { name: 'Refresh' }))
        await waitFor(() => expect(service.listPages).toHaveBeenCalledTimes(2))
        fireEvent.click(screen.getByTitle('Publish'))
        fireEvent.click(within(await screen.findByRole('dialog')).getByRole('button', { name: 'Publish' }))
        await waitFor(() => expect(service.publishPage).toHaveBeenCalledWith(pageId, 2))
    })

    it('Cancel performs no writes and pending publish cannot be dismissed or submitted twice', async () => {
        service.listPages.mockResolvedValue([listItem(3)])
        let settle!: (value: unknown) => void
        service.publishPage.mockReturnValue(new Promise(resolve => { settle = resolve }))
        renderPage()
        await screen.findByText('Authority')
        fireEvent.click(screen.getByTitle('Delete'))
        await act(async () => { fireEvent.click(within(screen.getByRole('dialog')).getByRole('button', { name: 'Cancel' })) })
        expect(service.deletePage).not.toHaveBeenCalled()
        fireEvent.click(screen.getByTitle('Publish'))
        const dialog = await screen.findByRole('dialog')
        const publish = within(dialog).getByRole('button', { name: 'Publish' })
        act(() => { fireEvent.click(publish); fireEvent.click(publish) })
        expect(service.publishPage).toHaveBeenCalledTimes(1)
        fireEvent.keyDown(window, { key: 'Escape' })
        expect(dialog).toBeInTheDocument()
        expect(within(dialog).getByRole('button', { name: 'Cancel' })).toBeDisabled()
        await act(async () => { settle(publishedResponse(3)) })
        await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull())
    })
})
