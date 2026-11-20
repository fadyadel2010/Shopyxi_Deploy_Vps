/**
 * Group 5B — D-3 regression protection.
 *
 * Recent-sales customer identity is rendered from the semantic `isWalkInCustomer`
 * flag plus a localized key. A real customer name is shown unchanged, and a record
 * whose name is unavailable is never presented as a walk-in sale.
 */
import { cleanup, render, screen } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import PosDashboardContent from './PosDashboardContent'
import { POS_SOURCES } from './dashboard/usePosDashboardData'
import { useAuthStore } from '../store/authStore'
import en from '../i18n/locales/en.json'
import ar from '../i18n/locales/ar.json'

const platform = { catalog: en, rows: [] }

vi.mock('react-router-dom', () => ({ useNavigate: () => vi.fn() }))
vi.mock('react-i18next', () => ({
    useTranslation: () => ({
        t: (key, fallback) => key.split('.').reduce((value, part) => value?.[part], platform.catalog) || fallback || key,
    }),
}))
vi.mock('../hooks/useRTL', () => ({ useRTL: () => ({ locale: 'en-US', dir: 'ltr' }) }))
vi.mock('../contexts/SettingsContext', () => ({ useSettings: () => ({ formatCurrency: value => `EGP ${value}` }) }))
vi.mock('./StorefrontStatusCard', () => ({ default: () => <div>Storefront status widget</div> }))
vi.mock('./dashboard/usePosDashboardData', async () => {
    const actual = await vi.importActual('./dashboard/usePosDashboardData')
    return {
        ...actual,
        useDashboardActorScope: () => 'actor',
        default: () => {
            const missing = { data: null, pending: false, error: 'failed', retry: vi.fn() }
            return {
                stats: missing,
                trend: missing,
                products: missing,
                sales: { data: platform.rows, pending: false, error: null, retry: vi.fn() },
                stock: missing,
                categories: missing,
                payments: missing,
                refresh: vi.fn(),
            }
        },
    }
})

const walkInSale = {
    id: 's1',
    invoiceNumber: 'WALKIN-1',
    isWalkInCustomer: true,
    totalAmount: 120,
    invoiceDate: '2026-11-15T09:00:00Z',
}
const namedSale = {
    id: 's2',
    invoiceNumber: 'NAMED-1',
    isWalkInCustomer: false,
    customerName: 'شركة النيل للتجارة',
    totalAmount: 80,
    invoiceDate: '2026-11-15T08:00:00Z',
}
const unavailableNameSale = {
    id: 's3',
    invoiceNumber: 'ORPHAN-1',
    isWalkInCustomer: false,
    totalAmount: 40,
    invoiceDate: '2026-11-15T07:00:00Z',
}

beforeEach(() => {
    platform.catalog = en
    platform.rows = [walkInSale, namedSale, unavailableNameSale]
    useAuthStore.setState({ user: { id: 'a', role: 'Owner', effectivePermissions: ['dashboard.view', 'sales.view', 'finance.view'] } })
})

afterEach(cleanup)

describe('recent-sales customer identity', () => {
    it('localizes a genuine walk-in sale in English', () => {
        render(<PosDashboardContent />)

        expect(screen.getByText(en.dashboard.phase42.walkInCustomer)).toBeInTheDocument()
        expect(screen.getByText('Walk-in customer')).toBeInTheDocument()
    })

    it('localizes a genuine walk-in sale in Arabic', () => {
        platform.catalog = ar
        render(<PosDashboardContent />)

        expect(screen.getByText(ar.dashboard.phase42.walkInCustomer)).toBeInTheDocument()
        expect(screen.queryByText('Walk-in customer')).not.toBeInTheDocument()
        expect(screen.queryByText('Walk-in Customer')).not.toBeInTheDocument()
    })

    it('keeps a real customer name unchanged in both languages', () => {
        const { unmount } = render(<PosDashboardContent />)
        expect(screen.getByText('شركة النيل للتجارة')).toBeInTheDocument()
        expect(screen.queryByText(en.dashboard.phase42.walkInCustomer)).toBeInTheDocument()
        unmount()

        platform.catalog = ar
        render(<PosDashboardContent />)
        expect(screen.getByText('شركة النيل للتجارة')).toBeInTheDocument()
    })

    it('does not label a non-walk-in record with an unavailable name as walk-in', () => {
        render(<PosDashboardContent />)

        expect(screen.getByText(en.dashboard.phase42.customerUnavailable)).toBeInTheDocument()
        // Only the genuine walk-in row may carry the walk-in label.
        expect(screen.getAllByText(en.dashboard.phase42.walkInCustomer)).toHaveLength(1)
    })

    it.each([['English', en], ['Arabic', ar]])('keeps legacy API display text unchanged in %s without inventing identity', (_, catalog) => {
        platform.catalog = catalog
        platform.rows = [{
            id: 'legacy', invoiceNumber: 'LEGACY-1', customerName: 'Walk-in Customer',
            totalAmount: 5, invoiceDate: '2026-11-15T09:00:00Z',
        }]
        render(<PosDashboardContent />)

        // No semantic flag: this could also be a real stored customer name.
        expect(screen.getByText('Walk-in Customer')).toBeInTheDocument()
        expect(screen.queryByText(catalog.dashboard.phase42.walkInCustomer)).not.toBeInTheDocument()
    })

    it.each([['English', en], ['Arabic', ar]])('renders an unknown legacy association neutrally in %s', (_, catalog) => {
        platform.catalog = catalog
        platform.rows = [{
            id: 'unknown', invoiceNumber: 'UNKNOWN-1',
            totalAmount: 5, invoiceDate: '2026-11-15T09:00:00Z',
        }]
        render(<PosDashboardContent />)

        expect(screen.getByText(catalog.dashboard.phase42.customerUnavailable)).toBeInTheDocument()
        expect(screen.queryByText(catalog.dashboard.phase42.walkInCustomer)).not.toBeInTheDocument()
    })
})

describe('recent-sales response validation', () => {
    const valid = POS_SOURCES.sales.valid

    it('accepts a walk-in row that omits the customer name', () => {
        expect(valid([walkInSale])).toBe(true)
    })

    it('accepts named rows and legacy rows without the semantic flag', () => {
        expect(valid([namedSale])).toBe(true)
        expect(valid([{ id: 'l1', invoiceNumber: 'LEGACY-1', customerName: 'Legacy', totalAmount: 5, invoiceDate: '2026-11-15T09:00:00Z' }])).toBe(true)
    })

    it('still rejects rows that lose consumed financial fields', () => {
        expect(valid([{ ...walkInSale, totalAmount: null }])).toBe(false)
        expect(valid([{ ...walkInSale, invoiceDate: null }])).toBe(false)
        expect(valid([{ ...walkInSale, invoiceNumber: '' }])).toBe(false)
        expect(valid([{ ...walkInSale, isWalkInCustomer: 'yes' }])).toBe(false)
        expect(valid([{ ...walkInSale, customerName: 42 }])).toBe(false)
    })
})
