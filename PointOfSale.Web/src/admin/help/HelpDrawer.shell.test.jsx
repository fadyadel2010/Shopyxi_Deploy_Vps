import { useRef, useState } from 'react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { cleanup, fireEvent, screen, waitFor, within } from '@testing-library/react'
import { act } from 'react'
import HelpDrawer from './HelpDrawer'
import i18n from '../../i18n/config'
import { renderPrimitive } from '../../design-system/test/renderPrimitive'

const apiMock = vi.hoisted(() => ({ get: vi.fn(), post: vi.fn() }))
vi.mock('../../services/api', () => ({ default: apiMock }))

function Harness({ initialOpen = true, articleKey = 'dashboard', pathname = '/', onClose }) {
    const [open, setOpen] = useState(initialOpen)
    const triggerRef = useRef(null)
    return (
        <>
            <button ref={triggerRef} type="button" onClick={() => setOpen(true)}>Open help</button>
            <HelpDrawer
                isOpen={open}
                onClose={() => { onClose?.(); setOpen(false) }}
                articleKey={articleKey}
                pathname={pathname}
                restoreFocusRef={triggerRef}
            />
        </>
    )
}

const mount = (props = {}) => renderPrimitive(<Harness {...props} />)

describe('shell help drawer', () => {
    afterEach(async () => {
        cleanup()
        apiMock.get.mockReset()
        apiMock.post.mockReset()
        await act(async () => { await i18n.changeLanguage('en') })
    })

    it('renders no focusable help surface while closed', () => {
        mount({ initialOpen: false })
        expect(screen.queryByRole('dialog')).toBeNull()
        expect(document.querySelector('.pos-help__content')).toBeNull()
        expect(screen.queryByRole('textbox')).toBeNull()
    })

    it('composes the modal help surface, loads the existing article and never submits the assistant', async () => {
        mount()
        const dialog = await screen.findByRole('dialog', { name: i18n.t('common.help') }, { timeout: 5000 })
        expect(dialog).toHaveAttribute('aria-modal', 'true')
        expect(dialog).toHaveClass('pos-help-drawer')

        await waitFor(() => {
            expect(document.querySelector('.pos-help__markdown')?.textContent?.trim().length).toBeGreaterThan(0)
        })
        expect(within(dialog).getByText('Ask the assistant')).toBeInTheDocument()
        expect(within(dialog).getByRole('textbox', { name: 'Assistant question' })).toBeInTheDocument()
        expect(within(dialog).getByRole('button', { name: 'Ask' })).toBeDisabled()
        expect(apiMock.post).not.toHaveBeenCalled()
    })

    it('closes on Escape, restores focus to the help trigger and renders nothing afterwards', async () => {
        const onClose = vi.fn()
        mount({ onClose })
        await screen.findByRole('dialog', { name: i18n.t('common.help') }, { timeout: 5000 })
        fireEvent.keyDown(document, { key: 'Escape' })

        await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull())
        expect(onClose).toHaveBeenCalledOnce()
        await waitFor(() => expect(screen.getByRole('button', { name: 'Open help' })).toHaveFocus())
        expect(screen.queryByRole('textbox')).toBeNull()
    })

    it('closes from the dialog close button', async () => {
        mount()
        const dialog = await screen.findByRole('dialog', { name: i18n.t('common.help') }, { timeout: 5000 })
        fireEvent.click(within(dialog).getByRole('button', { name: i18n.t('common.close') }))
        await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull())
    })

    it('keeps the existing Arabic assistant copy and labels', async () => {
        await act(async () => { await i18n.changeLanguage('ar') })
        mount()
        const dialog = await screen.findByRole('dialog', { name: i18n.t('common.help') }, { timeout: 5000 })
        await waitFor(() => {
            expect(document.querySelector('.pos-help__markdown')?.textContent?.trim().length).toBeGreaterThan(0)
        })
        expect(within(dialog).getByText('اسأل المساعد')).toBeInTheDocument()
        expect(within(dialog).getByRole('textbox', { name: 'سؤال المساعد' })).toBeInTheDocument()
    })
})
