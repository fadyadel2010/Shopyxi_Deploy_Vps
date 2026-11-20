import { act, fireEvent, render, renderHook, screen, waitFor } from '@testing-library/react'
import { beforeEach, expect, it, vi } from 'vitest'
import AttributionSummaryCards from './components/AttributionSummaryCards'
import CampaignPerformanceTable from './components/CampaignPerformanceTable'
import { useAttributionSummary } from './hooks/useAttribution'
import { useRoiByCampaign } from './hooks/useCampaignCost'
import { useAuthStore } from '../../../store/authStore'
import { navigationOwner } from '../../../navigation/navigation.fixture'
import i18n from '../../../i18n/config'
import userEvent from '@testing-library/user-event'
import { MemoryRouter } from 'react-router-dom'
import AttributionOverviewPage from './pages/AttributionOverviewPage'
import RevenueBySourceChart from './components/RevenueBySourceChart'
import '../../../design-system/test/radixJsdomPolyfills'

const api = vi.hoisted(() => ({ get: vi.fn() }))
vi.mock('../../../services/api', () => ({ default: api }))
vi.mock('../../../contexts/SettingsContext', () => ({ useSettings: () => ({
    formatCurrency: (value: unknown) => typeof value === 'number' && Number.isFinite(value) ? `MONEY ${value}` : '—',
}) }))
const summary = { totalRevenue: -100, recognizedRevenue: 80, totalOrders: 1, revenueBySource: [] }
const roi = { totalCost: 20, totalRevenue: -100, totalProfit: -120, totalOrders: 0, overallRoi: -600, campaigns: [] }
beforeEach(async () => {
    vi.clearAllMocks()
    await i18n.changeLanguage('en')
    useAuthStore.setState({ user: navigationOwner, isAuthenticated: true, hasHydrated: true })
    api.get.mockResolvedValue({ data: summary })
})

it('renders gross-recognition AOV while retaining signed net revenue, never net/order AOV', () => {
    render(<AttributionSummaryCards data={summary} loading={false} />)
    expect(screen.getByText('MONEY -100')).toBeInTheDocument()
    expect(screen.getByText('MONEY 80')).toBeInTheDocument()
})

it('does not infer a zero AOV when the gross-recognition field is absent', () => {
    render(<AttributionSummaryCards data={{ ...summary, recognizedRevenue: undefined }} loading={false} />)
    expect(screen.getByText('—')).toBeInTheDocument()
    expect(screen.queryByText('MONEY -100', { selector: '.text-2xl' })).toBeInTheDocument()
})

it('uses recognized order count even in a refund-only period', () => {
    render(<AttributionSummaryCards data={{ ...summary, recognizedRevenue: 0, totalOrders: 0 }} loading={false} />)
    expect(screen.getByText('MONEY 0')).toBeInTheDocument()
    expect(screen.getByText('MONEY -100')).toBeInTheDocument()
})

it('hides the old actor and old filter data synchronously and rejects a late old request', async () => {
    const hook = renderHook(({ period }) => useAttributionSummary('LastTouch', period), { initialProps: { period: 'today' } })
    await waitFor(() => expect(hook.result.current.data).toEqual(summary))
    let complete!: (value: unknown) => void
    api.get.mockReturnValue(new Promise(resolve => { complete = resolve }))
    hook.rerender({ period: 'last_month' })
    expect(hook.result.current.data).toBeNull()
    act(() => { useAuthStore.setState({ user: { ...navigationOwner, id: 'new-actor', effectivePermissions: [] } }) })
    expect(hook.result.current.data).toBeNull()
    await act(async () => { complete({ data: { ...summary, totalRevenue: 999 } }) })
    expect(hook.result.current.data?.totalRevenue).not.toBe(-100)
})

it.each([401, 403, 500])('clears successful analytics on HTTP %s and localizes the error instead of server diagnostics', async status => {
    const hook = renderHook(() => useAttributionSummary())
    await waitFor(() => expect(hook.result.current.data).toEqual(summary))
    api.get.mockRejectedValue({ response: { status, data: { message: 'SQL private detail' } } })
    await act(async () => { await hook.result.current.refetch() })
    expect(hook.result.current.data).toBeNull()
    expect(hook.result.current.error).not.toContain('SQL')
    expect(hook.result.current.error).toBeTruthy()
})

it('rejects malformed ROI as unavailable, not empty campaign success', async () => {
    api.get.mockResolvedValue({ data: { totalCost: 20 } })
    const hook = renderHook(() => useRoiByCampaign())
    await waitFor(() => expect(hook.result.current.loading).toBe(false))
    expect(hook.result.current.data).toBeNull()
    expect(hook.result.current.error).toBeTruthy()
    api.get.mockResolvedValue({ data: roi })
    await act(async () => { await hook.result.current.refetch() })
    expect(hook.result.current.data).toEqual(roi)
})

it('campaign sorting is keyboard accessible and sorts names lexically', async () => {
    render(<CampaignPerformanceTable loading={false} data={{ campaigns: [
        { campaign: 'Alpha', source: 'one', revenue: 10, orders: 1, averageOrderValue: 10 },
        { campaign: 'Zulu', source: 'two', revenue: 5, orders: 1, averageOrderValue: 5 },
    ] }} />)
    const sort = screen.getByRole('button', { name: i18n.t('attribution.campaign') })
    fireEvent.click(sort)
    const rows = screen.getAllByRole('row')
    expect(rows[1]).toHaveTextContent('Zulu')
    expect(sort.closest('th')).toHaveAttribute('aria-sort', 'descending')
    expect(sort).toHaveAttribute('type', 'button')
})

it.each(['en', 'ar'])('attribution filters contain keyboard selection and restore focus on Escape in %s', async language => {
    await i18n.changeLanguage(language)
    api.get.mockImplementation((url: string) => Promise.resolve({ data: url.endsWith('/campaigns') ? { campaigns: [] } : summary }))
    render(<MemoryRouter><AttributionOverviewPage /></MemoryRouter>)
    const trigger = screen.getByRole('button', { name: i18n.t('attribution.lastTouch') })
    trigger.focus()
    await userEvent.setup().keyboard('{Enter}{ArrowDown}')
    expect(await screen.findByRole('menu')).toHaveAttribute('dir', language === 'ar' ? 'rtl' : 'ltr')
    await userEvent.setup().keyboard('{Escape}')
    await waitFor(() => expect(trigger).toHaveFocus())
    expect(screen.queryByRole('menu')).toBeNull()
})

it('renders signed revenue around an unmirrored zero axis with zero-width real zeros', () => {
    const { container } = render(<RevenueBySourceChart loading={false} data={{ revenueBySource: [
        { source: 'refunds', revenue: -10, orders: 0, percentageOfTotal: -100 },
        { source: 'zero', revenue: 0, orders: 0, percentageOfTotal: 0 },
        { source: 'sales', revenue: 20, orders: 1, percentageOfTotal: 200 },
    ] }} />)
    expect(screen.getByText('MONEY -10')).toBeInTheDocument()
    const bars = container.querySelectorAll<HTMLElement>('[style*="width:"]')
    expect(bars[0].style.width).toBe('25%')
    expect(bars[0].style.left).toBe('25%')
    expect(bars[1].style.width).toBe('0%')
    expect(bars[2].style.left).toBe('50%')
    for (const bar of bars) expect(bar.parentElement).toHaveAttribute('dir', 'ltr')
})
