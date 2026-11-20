import { createRef, useState } from 'react'
import { fireEvent, screen, waitFor } from '@testing-library/react'
import { describe, expect, it, vi } from 'vitest'
import Dialog from './Dialog'
import { expectNoAxeViolations } from '../../test/axe'
import { renderPrimitive } from '../../test/renderPrimitive'

function ControlledDialog({ initialOpen = false, onOpenChange, ...props }) {
    const [open, setOpen] = useState(initialOpen)
    return (
        <Dialog
            open={open}
            onOpenChange={(next) => {
                onOpenChange?.(next)
                setOpen(next)
            }}
            {...props}
        />
    )
}

describe('Dialog', () => {
    it('composes consumer Escape callbacks without bypassing the primitive dismissal policy', async () => {
        const callback = vi.fn()
        renderPrimitive(<ControlledDialog title="Guarded" closeOnEscape={false} onEscapeKeyDown={callback} trigger={<button>Open guarded</button>} />)
        fireEvent.click(screen.getByText('Open guarded'))
        await screen.findByRole('dialog')
        fireEvent.keyDown(document, { key: 'Escape' })
        expect(callback).toHaveBeenCalledTimes(1)
        expect(screen.getByRole('dialog')).toBeInTheDocument()
    })
    it('renders nothing while closed', () => {
        renderPrimitive(
            <Dialog open={false} onOpenChange={() => {}} title="Hidden">Body</Dialog>,
        )
        expect(screen.queryByRole('dialog')).toBeNull()
    })

    it('portals into the POS overlay host with explicit modal and naming semantics', () => {
        renderPrimitive(
            <ControlledDialog
                title="Refund sale"
                description="This returns the full amount."
                closeLabel="Close dialog"
                trigger={<button type="button">Open refund dialog</button>}
                footer={<button type="button">Apply</button>}
            >
                <p>Refund body</p>
            </ControlledDialog>,
        )

        fireEvent.click(screen.getByRole('button', { name: 'Open refund dialog' }))
        const dialog = screen.getByRole('dialog')
        const host = document.getElementById('pos-overlay-root')

        expect(host).not.toBeNull()
        expect(host.contains(dialog)).toBe(true)
        expect(dialog.getAttribute('aria-modal')).toBe('true')
        const labelledBy = dialog.getAttribute('aria-labelledby')
        expect(labelledBy).toBeTruthy()
        expect(document.getElementById(labelledBy)?.textContent).toBe('Refund sale')
        const describedBy = dialog.getAttribute('aria-describedby')
        expect(describedBy).toBeTruthy()
        expect(document.getElementById(describedBy)?.textContent).toBe('This returns the full amount.')
        expect(screen.getByText('Refund body')).toBeTruthy()
        expect(screen.getByRole('button', { name: 'Apply' })).toBeTruthy()
        expect(screen.getByRole('button', { name: 'Close dialog' })).toBeTruthy()
    })

    it('closes on Escape and restores focus to the registered trigger', async () => {
        const onOpenChange = vi.fn()
        renderPrimitive(
            <ControlledDialog
                title="Escape me"
                trigger={<button type="button">Open escape dialog</button>}
                onOpenChange={onOpenChange}
            />,
        )

        const trigger = screen.getByRole('button', { name: 'Open escape dialog' })
        trigger.focus()
        fireEvent.click(trigger)
        await screen.findByRole('dialog')

        fireEvent.keyDown(document, { key: 'Escape' })

        await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull())
        expect(onOpenChange).toHaveBeenCalledWith(false)
        await waitFor(() => expect(document.activeElement).toBe(trigger))
    })

    it('closes from the close button', async () => {
        renderPrimitive(
            <ControlledDialog
                title="Close button"
                closeLabel="Dismiss"
                trigger={<button type="button">Open close dialog</button>}
            />,
        )

        fireEvent.click(screen.getByRole('button', { name: 'Open close dialog' }))
        await screen.findByRole('dialog')
        fireEvent.click(screen.getByRole('button', { name: 'Dismiss' }))
        await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull())
    })

    it('does not close on Escape when closeOnEscape is false', async () => {
        renderPrimitive(
            <ControlledDialog
                title="Sticky dialog"
                closeOnEscape={false}
                trigger={<button type="button">Open sticky dialog</button>}
            />,
        )

        fireEvent.click(screen.getByRole('button', { name: 'Open sticky dialog' }))
        await screen.findByRole('dialog')
        fireEvent.keyDown(document, { key: 'Escape' })
        expect(screen.getByRole('dialog')).toBeTruthy()
    })

    it('restores focus to an explicit ref for programmatic dialogs without a trigger', async () => {
        const opener = createRef()
        function Harness() {
            const [open, setOpen] = useState(false)
            return (
                <div>
                    <button ref={opener} type="button" onClick={() => setOpen(true)}>
                        Programmatic opener
                    </button>
                    <Dialog
                        open={open}
                        onOpenChange={setOpen}
                        title="Programmatic dialog"
                        restoreFocusRef={opener}
                    />
                </div>
            )
        }
        renderPrimitive(<Harness />)

        const openerButton = screen.getByRole('button', { name: 'Programmatic opener' })
        openerButton.focus()
        fireEvent.click(openerButton)
        await screen.findByRole('dialog')
        fireEvent.keyDown(document, { key: 'Escape' })
        await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull())
        await waitFor(() => expect(document.activeElement).toBe(openerButton))
    })

    it('honours initialFocusRef', async () => {
        const target = createRef()
        renderPrimitive(
            <ControlledDialog
                title="Focus target"
                initialFocusRef={target}
                closeLabel="Close dialog"
                trigger={<button type="button">Open focus dialog</button>}
            >
                <button ref={target} type="button">Target action</button>
            </ControlledDialog>,
        )

        fireEvent.click(screen.getByRole('button', { name: 'Open focus dialog' }))
        await screen.findByRole('dialog')
        await waitFor(() => expect(document.activeElement).toBe(target.current))
    })

    it('marks the panel busy when requested', async () => {
        renderPrimitive(
            <ControlledDialog
                title="Busy dialog"
                busy
                trigger={<button type="button">Open busy dialog</button>}
            >
                Working
            </ControlledDialog>,
        )
        fireEvent.click(screen.getByRole('button', { name: 'Open busy dialog' }))
        const dialog = await screen.findByRole('dialog')
        expect(dialog.getAttribute('aria-busy')).toBe('true')
    })

    it('closes only the topmost nested dialog on a single Escape', async () => {
        const outerChange = vi.fn()
        const innerChange = vi.fn()
        function NestedHarness() {
            const [outer, setOuter] = useState(false)
            const [inner, setInner] = useState(false)
            return (
                <div>
                    <button type="button" onClick={() => setOuter(true)}>Open outer</button>
                    <Dialog
                        open={outer}
                        onOpenChange={(next) => {
                            outerChange(next)
                            setOuter(next)
                        }}
                        title="Outer dialog"
                    >
                        <button type="button" onClick={() => setInner(true)}>Open inner</button>
                        <Dialog
                            open={inner}
                            onOpenChange={(next) => {
                                innerChange(next)
                                setInner(next)
                            }}
                            title="Inner dialog"
                        />
                    </Dialog>
                </div>
            )
        }
        renderPrimitive(<NestedHarness />)

        fireEvent.click(screen.getByRole('button', { name: 'Open outer' }))
        await screen.findByRole('dialog', { name: 'Outer dialog' })
        fireEvent.click(screen.getByRole('button', { name: 'Open inner' }))
        await screen.findByRole('dialog', { name: 'Inner dialog' })

        fireEvent.keyDown(document, { key: 'Escape' })

        await waitFor(() => expect(screen.queryByRole('dialog', { name: 'Inner dialog' })).toBeNull())
        expect(screen.getByRole('dialog', { name: 'Outer dialog' })).toBeTruthy()
        expect(innerChange).toHaveBeenCalledWith(false)
        expect(outerChange).not.toHaveBeenCalledWith(false)
    })

    it('reports no axe violations while open', async () => {
        renderPrimitive(
            <ControlledDialog
                title="Accessible refund"
                description="Confirm the refund amount."
                closeLabel="Close dialog"
                trigger={<button type="button">Open accessible dialog</button>}
                footer={<button type="button">Confirm</button>}
            >
                <label htmlFor="refund-amount">Amount</label>
                <input id="refund-amount" inputMode="decimal" />
            </ControlledDialog>,
        )
        fireEvent.click(screen.getByRole('button', { name: 'Open accessible dialog' }))
        await screen.findByRole('dialog')

        await expectNoAxeViolations(document.getElementById('pos-overlay-root'))
    })
})
