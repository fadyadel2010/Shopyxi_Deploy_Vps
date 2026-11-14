import { describe, expect, it, vi } from 'vitest'
import { render, screen } from '@testing-library/react'
import { MemoryRouter } from 'react-router-dom'
import PosAuthBootstrapBoundary from './PosAuthBootstrapBoundary'

vi.mock('./AuthBootstrap', () => ({ default: () => <div data-testid="pos-bootstrap" /> }))

describe('POS bootstrap application boundary', () => {
    it.each(['/super-admin', '/super-admin/login', '/super-admin/finance', '/affiliate/portal'])(
        'does not mount POS session authority on %s', (path) => {
            render(<MemoryRouter initialEntries={[path]}><PosAuthBootstrapBoundary /></MemoryRouter>)
            expect(screen.queryByTestId('pos-bootstrap')).not.toBeInTheDocument()
        },
    )
    it('keeps POS authority mounted on merchant routes', () => {
        render(<MemoryRouter initialEntries={['/commercehub/orders']}><PosAuthBootstrapBoundary /></MemoryRouter>)
        expect(screen.getByTestId('pos-bootstrap')).toBeInTheDocument()
    })
})
