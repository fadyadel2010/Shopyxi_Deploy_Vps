import { fireEvent, screen, waitFor } from '@testing-library/react'
import { describe, expect, it, vi } from 'vitest'
import { Tabs, TabsContent, TabsList, TabsTrigger } from './Tabs'
import { expectNoAxeViolations } from '../../test/axe'
import { renderPrimitive } from '../../test/renderPrimitive'

function SimpleTabs({ onValueChange, dir, defaultValue = 'sales' }) {
    return (
        <Tabs defaultValue={defaultValue} onValueChange={onValueChange} dir={dir}>
            <TabsList aria-label="Register sections">
                <TabsTrigger value="sales">Sales</TabsTrigger>
                <TabsTrigger value="returns">Returns</TabsTrigger>
                <TabsTrigger value="disabled" disabled>Disabled</TabsTrigger>
            </TabsList>
            <TabsContent value="sales">Sales panel</TabsContent>
            <TabsContent value="returns">Returns panel</TabsContent>
            <TabsContent value="disabled">Disabled panel</TabsContent>
        </Tabs>
    )
}

describe('Tabs', () => {
    it('exposes tab semantics and the selected panel relationship', () => {
        renderPrimitive(<SimpleTabs />)

        expect(screen.getByRole('tablist', { name: 'Register sections' })).toBeTruthy()
        const salesTab = screen.getByRole('tab', { name: 'Sales' })
        const returnsTab = screen.getByRole('tab', { name: 'Returns' })
        expect(salesTab.getAttribute('aria-selected')).toBe('true')
        expect(returnsTab.getAttribute('aria-selected')).toBe('false')

        const panel = screen.getByRole('tabpanel')
        expect(panel.textContent).toBe('Sales panel')
        expect(panel.id).toBe(salesTab.getAttribute('aria-controls'))
        expect(salesTab.id).toBe(panel.getAttribute('aria-labelledby'))
        expect(screen.queryByText('Returns panel')).toBeNull()
    })

    it('switches panels on pointer activation and reports the value change', () => {
        const onValueChange = vi.fn()
        renderPrimitive(<SimpleTabs onValueChange={onValueChange} />)

        fireEvent.mouseDown(screen.getByRole('tab', { name: 'Returns' }), { button: 0 })
        expect(onValueChange).toHaveBeenCalledWith('returns')
        expect(screen.getByRole('tabpanel').textContent).toBe('Returns panel')
    })

    it('moves focus and selection with arrow keys', async () => {
        renderPrimitive(<SimpleTabs />)

        const salesTab = screen.getByRole('tab', { name: 'Sales' })
        salesTab.focus()
        fireEvent.keyDown(salesTab, { key: 'ArrowRight' })

        await waitFor(() => expect(document.activeElement).toBe(screen.getByRole('tab', { name: 'Returns' })))
        expect(screen.getByRole('tab', { name: 'Returns' }).getAttribute('aria-selected')).toBe('true')
        expect(screen.getByRole('tabpanel').textContent).toBe('Returns panel')

        fireEvent.keyDown(document.activeElement, { key: 'Home' })
        await waitFor(() => expect(document.activeElement).toBe(screen.getByRole('tab', { name: 'Sales' })))
        expect(screen.getByRole('tab', { name: 'Sales' }).getAttribute('aria-selected')).toBe('true')

        fireEvent.keyDown(document.activeElement, { key: 'End' })
        // End must skip the disabled trigger and stop on the last focusable tab.
        await waitFor(() => expect(document.activeElement).toBe(screen.getByRole('tab', { name: 'Returns' })))
        expect(screen.getByRole('tab', { name: 'Disabled' }).getAttribute('tabindex')).toBe('-1')
    })

    it('skips disabled triggers when navigating and cannot activate them', async () => {
        renderPrimitive(<SimpleTabs />)

        fireEvent.mouseDown(screen.getByRole('tab', { name: 'Returns' }), { button: 0 })
        const returnsTab = screen.getByRole('tab', { name: 'Returns' })
        expect(returnsTab.getAttribute('aria-selected')).toBe('true')
        returnsTab.focus()
        fireEvent.keyDown(returnsTab, { key: 'End' })
        await waitFor(() => expect(document.activeElement).toBe(returnsTab))

        const disabledTab = screen.getByRole('tab', { name: 'Disabled' })
        expect(disabledTab.disabled).toBe(true)
        fireEvent.mouseDown(disabledTab, { button: 0 })
        fireEvent.click(disabledTab)
        expect(screen.getByRole('tab', { name: 'Returns' }).getAttribute('aria-selected')).toBe('true')
    })

    it('follows right-to-left arrow direction when dir is rtl', async () => {
        renderPrimitive(<SimpleTabs dir="rtl" />)

        const salesTab = screen.getByRole('tab', { name: 'Sales' })
        salesTab.focus()
        fireEvent.keyDown(salesTab, { key: 'ArrowLeft' })

        const returnsTab = screen.getByRole('tab', { name: 'Returns' })
        await waitFor(() => expect(document.activeElement).toBe(returnsTab))
        expect(returnsTab.getAttribute('aria-selected')).toBe('true')
    })

    it('renders the pills variant without changing semantics', () => {
        renderPrimitive(
            <Tabs defaultValue="one" variant="pills">
                <TabsList aria-label="Pills">
                    <TabsTrigger value="one">One</TabsTrigger>
                    <TabsTrigger value="two">Two</TabsTrigger>
                </TabsList>
                <TabsContent value="one">First</TabsContent>
                <TabsContent value="two">Second</TabsContent>
            </Tabs>,
        )

        const root = document.querySelector('.pos-tabs')
        expect(root.className).toContain('pos-tabs--pills')
        expect(screen.getByRole('tabpanel').textContent).toBe('First')
    })

    it('reports no axe violations', async () => {
        const { container } = renderPrimitive(<SimpleTabs />)
        await expectNoAxeViolations(container)
    })
})
