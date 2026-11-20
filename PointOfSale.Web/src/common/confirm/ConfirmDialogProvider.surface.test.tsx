import { useState } from 'react'
import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { ConfirmDialogProvider, useConfirmDialog } from './ConfirmDialogProvider'
import Dialog from '../../design-system/primitives/Dialog/Dialog'
import { PosSurfaceContext } from '../../design-system/overlays/posOverlayContext'
import { resetOverlayLayerStackForTests } from '../../design-system/overlays/overlayLayerStack'
import { renderPrimitive } from '../../design-system/test/renderPrimitive'

const mockT = vi.fn((key: string) => key)

vi.mock('react-i18next', () => ({
    useTranslation: () => ({
        t: mockT,
        i18n: { language: 'en', resolvedLanguage: 'en' },
    }),
}))

function ConfirmHarness({ kind = 'confirm' }: { kind?: 'confirm' | 'prompt' }) {
    const { confirm, prompt } = useConfirmDialog()
    const [result, setResult] = useState<string>('idle')

    return (
        <div>
            <button
                type="button"
                onClick={() => {
                    const call = kind === 'prompt'
                        ? prompt({ title: 'Enter note', inputLabel: 'Note' })
                        : confirm({ title: 'Delete cart', description: 'This cannot be undone.' })
                    void call.then((value) => setResult(String(value)))
                }}
            >
                Open confirmation
            </button>
            <span>result:{result}</span>
        </div>
    )
}

beforeEach(() => {
    resetOverlayLayerStackForTests()
    mockT.mockClear()
})

describe('ConfirmDialogProvider surface bridge', () => {
    it('settles outstanding callers when the provider unmounts', async () => {
        const settled = vi.fn()
        function PendingCaller() {
            const { confirm } = useConfirmDialog()
            return <button onClick={() => void confirm({ title: 'Pending' }).then(settled)}>Open pending</button>
        }
        const view = render(<ConfirmDialogProvider><PendingCaller /></ConfirmDialogProvider>)
        fireEvent.click(screen.getByText('Open pending'))
        await screen.findByRole('dialog')
        view.unmount()
        await waitFor(() => expect(settled).toHaveBeenCalledWith(false))
    })
    it('applies the POS attribute and token classes only to POS-origin dialogs', async () => {
        render(
            <ConfirmDialogProvider>
                <PosSurfaceContext.Provider value="pos">
                    <ConfirmHarness />
                </PosSurfaceContext.Provider>
            </ConfirmDialogProvider>,
        )

        fireEvent.click(screen.getByRole('button', { name: 'Open confirmation' }))
        const dialog = await screen.findByRole('dialog')
        expect(dialog.className).toContain('pos-confirm-panel')
        expect(dialog.closest('[data-product="pos"]')).not.toBeNull()
        expect(document.querySelector('.pos-confirm-scrim')).not.toBeNull()

        fireEvent.click(screen.getByRole('button', { name: 'common.cancel' }))
        await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull())
        expect(screen.getByText('result:false')).toBeTruthy()
    })

    it('leaves non-POS dialogs unmarked and visually unchanged', async () => {
        render(
            <ConfirmDialogProvider>
                <ConfirmHarness />
            </ConfirmDialogProvider>,
        )

        fireEvent.click(screen.getByRole('button', { name: 'Open confirmation' }))
        const dialog = await screen.findByRole('dialog')
        expect(dialog.className).not.toContain('pos-confirm-panel')
        expect(dialog.closest('[data-product="pos"]')).toBeNull()
        expect(document.querySelector('[data-product="pos"]')).toBeNull()

        fireEvent.click(screen.getByRole('button', { name: 'common.cancel' }))
        await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull())
    })

    it('tokenizes the prompt input for POS surfaces only', async () => {
        render(
            <ConfirmDialogProvider>
                <PosSurfaceContext.Provider value="pos">
                    <ConfirmHarness kind="prompt" />
                </PosSurfaceContext.Provider>
            </ConfirmDialogProvider>,
        )

        fireEvent.click(screen.getByRole('button', { name: 'Open confirmation' }))
        await screen.findByRole('dialog')
        expect(screen.getByRole('textbox').className).toContain('pos-confirm-input')
        fireEvent.click(screen.getByRole('button', { name: 'common.cancel' }))
        await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull())
    })

    it('closes only the topmost layer when a POS confirm sits above a Radix dialog', async () => {
        const dialogChanges: boolean[] = []

        function CoexistenceHarness() {
            const [open, setOpen] = useState(false)
            const { confirm } = useConfirmDialog()
            return (
                <div>
                    <button type="button" onClick={() => setOpen(true)}>Open parent dialog</button>
                    <Dialog
                        open={open}
                        onOpenChange={(next) => {
                            dialogChanges.push(next)
                            setOpen(next)
                        }}
                        title="Parent dialog"
                        closeLabel="Close parent"
                        trigger={<button type="button">Open via trigger</button>}
                    >
                        <button
                            type="button"
                            onClick={() => {
                                void confirm({ title: 'Confirm above' })
                            }}
                        >
                            Ask confirm
                        </button>
                    </Dialog>
                </div>
            )
        }

        renderPrimitive(
            <ConfirmDialogProvider>
                <CoexistenceHarness />
            </ConfirmDialogProvider>,
        )

        fireEvent.click(screen.getByRole('button', { name: 'Open parent dialog' }))
        await screen.findByRole('dialog', { name: 'Parent dialog' })

        fireEvent.click(screen.getByRole('button', { name: 'Ask confirm' }))
        await screen.findByRole('dialog', { name: 'Confirm above' })

        fireEvent.keyDown(document, { key: 'Escape' })

        await waitFor(() => expect(screen.queryByRole('dialog', { name: 'Confirm above' })).toBeNull())
        expect(screen.getByRole('dialog', { name: 'Parent dialog' })).toBeTruthy()
        expect(dialogChanges.filter((value) => value === false)).toHaveLength(0)

        fireEvent.keyDown(document, { key: 'Escape' })
        await waitFor(() => expect(screen.queryByRole('dialog', { name: 'Parent dialog' })).toBeNull())
        expect(dialogChanges.filter((value) => value === false)).toHaveLength(1)
    })
})
