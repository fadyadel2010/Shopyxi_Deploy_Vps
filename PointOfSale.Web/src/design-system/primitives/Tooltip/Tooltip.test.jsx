import { fireEvent, screen, waitFor } from '@testing-library/react'
import { describe, expect, it } from 'vitest'
import { Tooltip } from './Tooltip'
import { expectNoAxeViolations } from '../../test/axe'
import { renderPrimitive } from '../../test/renderPrimitive'

describe('Tooltip', () => {
    it('opens on keyboard focus and links the content as a description', async () => {
        renderPrimitive(
            <Tooltip label="Delete the selected rows">
                <button type="button" aria-label="Delete rows">Del</button>
            </Tooltip>,
        )

        const trigger = screen.getByRole('button', { name: 'Delete rows' })
        trigger.focus()

        const tooltip = await screen.findByRole('tooltip')
        expect(tooltip.textContent).toContain('Delete the selected rows')
        const describedBy = trigger.getAttribute('aria-describedby')
        expect(describedBy).toBeTruthy()
        expect(describedBy.split(' ')).toContain(tooltip.id)

        const host = document.getElementById('pos-overlay-root')
        expect(host.contains(tooltip)).toBe(true)
    })

    it('keeps the trigger accessible name independent from the tooltip text', async () => {
        renderPrimitive(
            <Tooltip label="Supplementary hint">
                <button type="button" aria-label="Save draft">S</button>
            </Tooltip>,
        )
        const trigger = screen.getByRole('button', { name: 'Save draft' })
        trigger.focus()
        await screen.findByRole('tooltip')
        expect(trigger.getAttribute('aria-label')).toBe('Save draft')
    })

    it('dismisses on Escape', async () => {
        renderPrimitive(
            <Tooltip label="Transient hint">
                <button type="button">Hover target</button>
            </Tooltip>,
        )

        const trigger = screen.getByRole('button', { name: 'Hover target' })
        trigger.focus()
        await screen.findByRole('tooltip')

        fireEvent.keyDown(document, { key: 'Escape' })
        await waitFor(() => expect(screen.queryByRole('tooltip')).toBeNull())
    })

    it('renders the trigger unchanged when disabled', () => {
        renderPrimitive(
            <Tooltip label="Never shown" disabled>
                <button type="button">Plain button</button>
            </Tooltip>,
        )
        expect(screen.getByRole('button', { name: 'Plain button' })).toBeTruthy()
        expect(screen.queryByRole('tooltip')).toBeNull()
    })

    it('reports no axe violations with the tooltip open', async () => {
        const { container } = renderPrimitive(
            <Tooltip label="Helpful context">
                <button type="button" aria-label="Explain">?</button>
            </Tooltip>,
        )
        screen.getByRole('button', { name: 'Explain' }).focus()
        await screen.findByRole('tooltip')
        await expectNoAxeViolations(container)
    })
})
