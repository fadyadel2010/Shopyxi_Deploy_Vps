import { createRef } from 'react'
import { fireEvent, render, screen } from '@testing-library/react'
import { describe, expect, it, vi } from 'vitest'
import { Badge, Button, Field, Input, Select, Table, TableBody, TableCaption, TableCell, TableRow } from './index'
import { expectNoAxeViolations } from '../test/axe'

describe('native primitive production contracts', () => {
    it('preserves explicit control ids and composes caller descriptions with help and errors', () => {
        render(
            <>
                <p id="external-help">External help</p>
                <Input id="sku" label="SKU" description="Receipt code" error="Required code" aria-describedby="external-help" />
                <Select id="payment" label="Payment" description="Choose one" aria-describedby="external-help">
                    <option>Cash</option>
                </Select>
            </>,
        )
        const input = screen.getByLabelText('SKU')
        expect(input).toHaveAttribute('id', 'sku')
        expect(input).toHaveAccessibleDescription('External help Receipt code Required code')
        expect(input).toHaveAttribute('type', 'text')
        const select = screen.getByLabelText('Payment')
        expect(select).toHaveAttribute('id', 'payment')
        expect(select).toHaveAccessibleDescription('External help Choose one')
    })

    it('exposes bare Select invalidity programmatically, not just by color', () => {
        render(<Select aria-label="Payment" invalid><option>Cash</option></Select>)
        expect(screen.getByRole('combobox')).toHaveAttribute('aria-invalid', 'true')
    })

    it('supports explicit Field ids for externally composed native controls', () => {
        render(<Field id="quantity" label="Quantity" required>{({ id, required }) => <input id={id} required={required} />}</Field>)
        expect(screen.getByRole('textbox', { name: 'Quantity' })).toHaveAttribute('id', 'quantity')
        expect(screen.getByRole('textbox', { name: 'Quantity' })).toBeRequired()
    })

    it('keeps disabled and read-only native behavior and forwards control refs', () => {
        const ref = createRef()
        const change = vi.fn()
        render(<><Input label="Receipt" readOnly defaultValue="R1" ref={ref} /><Button disabled onClick={change}>Charge</Button></>)
        expect(ref.current).toHaveAttribute('readonly')
        fireEvent.click(screen.getByRole('button'))
        expect(change).not.toHaveBeenCalled()
    })

    it('supports decorative badge dots and size without leaking non-native attributes', () => {
        render(<Badge dot size="sm">Paid</Badge>)
        expect(screen.getByText('Paid')).not.toHaveAttribute('dot')
        expect(screen.getByText('Paid').querySelector('[aria-hidden="true"]')).not.toBeNull()
    })

    it('uses selected-row styling without inventing grid ARIA semantics', () => {
        render(<Table><TableCaption>Sales</TableCaption><TableBody><TableRow selected><TableCell>R1</TableCell></TableRow></TableBody></Table>)
        const row = screen.getByRole('row')
        expect(row).toHaveAttribute('data-selected', 'true')
        expect(row).not.toHaveAttribute('aria-selected')
        expect(screen.getByRole('table').parentElement).not.toHaveAttribute('tabindex')
    })

    it('checks all native form semantics in both directions with axe', async () => {
        for (const dir of ['ltr', 'rtl']) {
            const view = render(<div dir={dir}><Input label="الاسم / Name" required error="مطلوب / Required" /><Select label="الدفع / Payment" invalid><option>Cash</option></Select><Button>Save</Button></div>)
            await expectNoAxeViolations(view.container)
            view.unmount()
        }
    })
})
