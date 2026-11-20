import { cleanup, fireEvent, render, screen, within } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import DashboardSalesTrend from './DashboardSalesTrend'
import DashboardRankedAmounts from './DashboardRankedAmounts'
import DashboardDataState from './DashboardDataState'
import en from '../../i18n/locales/en.json'
import { formatCurrency } from '../../utils/dateFormat'

vi.mock('react-i18next', () => ({ useTranslation: () => ({ t: key => key.split('.').reduce((value, part) => value?.[part], en) || key }) }))
vi.mock('../../hooks/useRTL', () => ({ useRTL: () => ({ locale: 'en-US' }) }))
afterEach(cleanup)
const money = value => formatCurrency(value, 'EGP', 'en-US')

describe('Dashboard accessible visualizations', () => {
    it.each([{ values: [0] }, { values: [-12.25] }, { values: [3, 0, -4, 7] }, { values: [0, 0] }])('renders every value in signed/zero/single series $values', ({ values }) => {
        const { container } = render(<DashboardSalesTrend data={values.map((amount, index) => ({ date: `2026-11-${10 + index}T00:00:00`, label: 'server', amount }))} formatCurrency={money} period="week" />)
        expect(screen.getByRole('img')).toHaveAccessibleName(/oldest to newest/)
        fireEvent.click(screen.getByText('View dates and exact amounts'))
        expect(screen.getAllByRole('row')).toHaveLength(values.length + 1)
        const rows = screen.getAllByRole('row').slice(1)
        values.forEach((value, index) => expect(within(rows[index]).getByText(money(value), { normalizer: text => text })).toBeInTheDocument())
        expect(container.querySelector('svg').innerHTML).not.toMatch(/NaN|Infinity/)
        expect(container.querySelectorAll('circle')).toHaveLength(values.length)
        expect(container.textContent).not.toContain('$')
    })
    it('retains complete mixed-script names and negative/zero ranked values', () => {
        const name = 'منتج طويل جداً / Extremely long product name — without truncation'
        render(<DashboardRankedAmounts data={[{ label: name, value: -123456789.12 }, { label: 'Zero', value: 0 }]} caption="Products" labelHeading="Product" valueHeading="Amount" formatValue={money} />)
        expect(screen.getByText(name)).toBeInTheDocument()
        expect(screen.getByText(money(-123456789.12), { normalizer: text => text })).toBeInTheDocument()
        expect(screen.getByText(money(0), { normalizer: text => text })).toBeInTheDocument()
        expect(screen.getAllByRole('row')).toHaveLength(3)
    })
    it('does not claim empty success on failure and offers persistent retry', () => {
        const retry = vi.fn()
        render(<DashboardDataState source={{ data: null, pending: false, error: 'failed', retry }} emptyMessage="All good">data</DashboardDataState>)
        expect(screen.queryByText('All good')).not.toBeInTheDocument()
        expect(screen.getByText(/not a confirmed zero/)).toBeInTheDocument()
        fireEvent.click(screen.getByRole('button', { name: 'Retry' }))
        expect(retry).toHaveBeenCalledOnce()
    })
    it('renders genuine empty success separately from pending and stale', () => {
        const { rerender } = render(<DashboardDataState source={{ data: [], pending: false, error: null }} emptyMessage="Empty success" />)
        expect(screen.getByText('Empty success')).toBeInTheDocument()
        rerender(<DashboardDataState source={{ data: null, pending: true, error: null }} />)
        expect(screen.getByRole('status')).toHaveTextContent(en.common.loading)
        expect(screen.queryByText('Empty success')).not.toBeInTheDocument()
    })
})
