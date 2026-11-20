import { createRef } from 'react'
import { fireEvent, render, screen } from '@testing-library/react'
import { describe, expect, it, vi } from 'vitest'
import {
    Badge,
    Button,
    Card,
    IconButton,
    Input,
    Select,
    Table,
    TableBody,
    TableCaption,
    TableCell,
    TableEmpty,
    TableHead,
    TableHeader,
    TableRow,
} from './index'
import { expectNoAxeViolations } from '../test/axe'

describe('Button', () => {
    it('defaults to type=button and forwards clicks plus refs', () => {
        const onClick = vi.fn()
        const ref = createRef()
        render(<Button ref={ref} onClick={onClick}>Save sale</Button>)

        const button = screen.getByRole('button', { name: 'Save sale' })
        expect(button.getAttribute('type')).toBe('button')
        expect(button.className).toContain('pos-button--primary')
        expect(button.className).toContain('pos-button--md')
        expect(ref.current).toBe(button)

        fireEvent.click(button)
        expect(onClick).toHaveBeenCalledTimes(1)
    })

    it('blocks interaction and reports busy while loading', () => {
        const onClick = vi.fn()
        render(<Button loading onClick={onClick}>Charge</Button>)

        const button = screen.getByRole('button', { name: 'Charge' })
        expect(button).toBeDisabled()
        expect(button.getAttribute('aria-busy')).toBe('true')
        fireEvent.click(button)
        expect(onClick).not.toHaveBeenCalled()
    })

    it('honours variant, size and full width props', () => {
        render(<Button variant="danger" size="lg" fullWidth>Delete</Button>)
        const button = screen.getByRole('button', { name: 'Delete' })
        expect(button.className).toContain('pos-button--danger')
        expect(button.className).toContain('pos-button--lg')
        expect(button.className).toContain('pos-button--full')
    })
})

describe('IconButton', () => {
    it('exposes an accessible name and hides the decorative icon', () => {
        const onClick = vi.fn()
        const ref = createRef()
        render(
            <IconButton
                ref={ref}
                label="Close dialog"
                icon={<svg data-testid="glyph" />}
                onClick={onClick}
            />,
        )

        const button = screen.getByRole('button', { name: 'Close dialog' })
        expect(ref.current).toBe(button)
        expect(button.getAttribute('type')).toBe('button')
        expect(button.querySelector('[aria-hidden="true"]')).not.toBeNull()
        expect(screen.getByTestId('glyph').closest('[aria-hidden="true"]')).not.toBeNull()
        fireEvent.click(button)
        expect(onClick).toHaveBeenCalledTimes(1)
    })

    it('disables while loading', () => {
        render(<IconButton label="Sync" loading icon={<svg />} />)
        expect(screen.getByRole('button', { name: 'Sync' })).toBeDisabled()
    })
})

describe('Field composition through Input and Select', () => {
    it('associates label, description and error text with the input', () => {
        render(
            <Input
                label="Customer phone"
                description="Digits only"
                error="Phone is required"
                required
            />,
        )

        const input = screen.getByLabelText(/Customer phone/)
        const describedBy = input.getAttribute('aria-describedby')
        expect(describedBy).toBeTruthy()
        const describedIds = describedBy.split(' ')
        expect(describedIds.some((id) => document.getElementById(id)?.textContent === 'Digits only')).toBe(true)
        expect(describedIds.some((id) => document.getElementById(id)?.textContent === 'Phone is required')).toBe(true)
        expect(input.getAttribute('aria-invalid')).toBe('true')
        expect(input.required).toBe(true)
    })

    it('renders a bare input when no label is supplied and marks invalidity', () => {
        render(<Input aria-label="Search products" invalid />)
        const input = screen.getByRole('textbox', { name: 'Search products' })
        expect(input.getAttribute('aria-invalid')).toBe('true')
        expect(input.className).toContain('pos-input--invalid')
        expect(document.querySelector('.pos-field')).toBeNull()
    })

    it('binds the select label and invalid state', () => {
        render(
            <Select label="Payment method" error="Choose a method" defaultValue="">
                <option value="">Select...</option>
                <option value="cash">Cash</option>
            </Select>,
        )

        const select = screen.getByLabelText(/Payment method/)
        expect(select.tagName).toBe('SELECT')
        expect(select.getAttribute('aria-invalid')).toBe('true')
        expect(select.getAttribute('aria-describedby')).toBeTruthy()
        expect(select.className).toContain('pos-select__control--invalid')

        fireEvent.change(select, { target: { value: 'cash' } })
        expect(select.value).toBe('cash')
    })
})

describe('Card, Badge and Table', () => {
    it('renders a tokenized card surface with padding variants', () => {
        const { container } = render(<Card padding="sm" data-testid="card">Content</Card>)
        const card = screen.getByTestId('card')
        expect(card.className).toContain('pos-card')
        expect(card.className).toContain('pos-card--padding-sm')
        expect(container.querySelector('.pos-card')?.textContent).toBe('Content')
    })

    it('maps badge variants to scoped classes', () => {
        render(<Badge variant="success">Paid</Badge>)
        expect(screen.getByText('Paid').className).toContain('pos-badge--success')
    })

    it('renders a semantic table with caption, column scopes and empty state', () => {
        render(
            <Table>
                <TableCaption>Today&apos;s sales</TableCaption>
                <TableHeader>
                    <TableRow>
                        <TableHead>Item</TableHead>
                        <TableHead>Total</TableHead>
                    </TableRow>
                </TableHeader>
                <TableBody>
                    <TableEmpty colSpan={2}>No sales yet</TableEmpty>
                </TableBody>
            </Table>,
        )

        const table = screen.getByRole('table', { name: "Today's sales" })
        expect(table.tagName).toBe('TABLE')
        const headers = screen.getAllByRole('columnheader')
        expect(headers).toHaveLength(2)
        headers.forEach((header) => expect(header.getAttribute('scope')).toBe('col'))
        expect(screen.getByText('No sales yet').getAttribute('colspan')).toBe('2')
    })
})

describe('axe-core primitive coverage', () => {
    it('reports no violations for a composed form, card and table', async () => {
        const { container } = render(
            <div>
                <Card>
                    <Input label="Product name" description="Shown on the receipt" required />
                    <Select label="Category" defaultValue="retail">
                        <option value="retail">Retail</option>
                        <option value="service">Service</option>
                    </Select>
                    <Badge variant="info">Draft</Badge>
                    <Button>Save</Button>
                    <IconButton label="Remove product" icon={<svg />} variant="danger" />
                </Card>
                <Table>
                    <TableCaption>Cart</TableCaption>
                    <TableHeader>
                        <TableRow>
                            <TableHead>Item</TableHead>
                            <TableHead>Qty</TableHead>
                        </TableRow>
                    </TableHeader>
                    <TableBody>
                        <TableRow>
                            <TableCell>Notebook</TableCell>
                            <TableCell>2</TableCell>
                        </TableRow>
                    </TableBody>
                </Table>
            </div>,
        )

        await expectNoAxeViolations(container)
    })
})
