import { cleanup, render, screen } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import FinanceKpiCards from './FinanceKpiCards'
import FinanceReconciliationPanel from './FinanceReconciliationPanel'
import type { SuperAdminFinancialIntegrityCheck, SuperAdminFinancialOverview } from './financeService'

let language = 'en'
vi.mock('../i18n/useSuperAdminLocale', () => ({
    useSuperAdminLocale: () => ({
        tr: (en: string, ar: string) => language === 'ar' ? ar : en,
        locale: language === 'ar' ? 'ar-EG-u-nu-latn' : 'en-US',
        formatCurrency: (n: number) => `EGP ${n.toFixed(2)}`,
        formatNumber: (n: number) => String(n),
        formatPercent: (n: number) => `${(n * 100).toFixed(1)}%`,
        formatDateTime: (s: string) => s || '—',
    }),
}))

const overview = {
    currency: 'EGP', totalGrossCommission: 35, totalPromoCost: 2.4,
    totalNetCommission: 32.6, netRevenue: 25.6, totalRefundAmount: 77,
    averageOrderValue: 128.33, totalOrdersCount: 3, refundedOrdersCount: 2,
    refundRate: 0.6667, refundImpactOnRevenue: 7,
} as SuperAdminFinancialOverview

const integrity = {
    status: 'OK', executedAtUtc: '2026-11-17T00:00:00Z', totalIssueCount: 0,
    debitCreditParityStatus: 'NOT_APPLICABLE',
    debitCreditParityNote: 'English-only server note',
    scopeDescription: 'English-only scoped server description',
    includesGlobalLedgerChecks: false, issues: [],
} as unknown as SuperAdminFinancialIntegrityCheck

afterEach(() => { cleanup(); language = 'en' })

describe('Finance authoritative value rendering', () => {
    it('renders every supplied metric without recalculating money', () => {
        render(<FinanceKpiCards data={overview} />)
        for (const value of ['35.00', '2.40', '32.60', '25.60', '77.00', '128.33', '7.00']) {
            expect(screen.getByText(`EGP ${value}`)).toBeInTheDocument()
        }
        expect(screen.getByText('66.7%')).toBeInTheDocument()
    })

    it('distinguishes unavailable data from genuine zero', () => {
        const { container } = render(<FinanceKpiCards />)
        expect(container.textContent).not.toContain('EGP 0.00')
        expect(container.textContent).not.toContain('0.0%')
    })

    it('renders a successfully retrieved zero population as zero', () => {
        render(<FinanceKpiCards data={{ ...overview, totalOrdersCount: 0, totalGrossCommission: 0 }} />)
        expect(screen.getByText('EGP 0.00')).toBeInTheDocument()
        expect(screen.getByText('0')).toBeInTheDocument()
    })

    it('does not present non-EGP authority as EGP', () => {
        render(<FinanceKpiCards data={{ ...overview, currency: 'USD' }} />)
        expect(screen.getAllByText('CommerceHub currency blocked')).toHaveLength(7)
    })

    it('renders equivalent Arabic metric definitions', () => {
        language = 'ar'
        render(<FinanceKpiCards data={overview} />)
        expect(screen.getByText('إجمالي العمولة')).toBeInTheDocument()
        expect(screen.getByText('تكلفة العروض')).toBeInTheDocument()
        expect(screen.getByText('صافي الإيراد')).toBeInTheDocument()
        expect(screen.getByText('EGP 25.60')).toBeInTheDocument()
    })
})

describe('Finance integrity coverage presentation', () => {
    it('does not advertise complete financial integrity for a limited scan', () => {
        render(<FinanceReconciliationPanel data={integrity} />)
        expect(screen.queryByText('OK', { exact: true })).not.toBeInTheDocument()
        expect(screen.getByText(/Selected checks passed/)).toBeInTheDocument()
        expect(screen.getByText(/Global balance and duplicate-pair checks are not included/)).toBeInTheDocument()
    })

    it('does not retain a passing badge after a refresh failure', () => {
        render(<FinanceReconciliationPanel data={integrity} errorMessage="Request failed" />)
        expect(screen.queryByText(/Selected checks passed/)).not.toBeInTheDocument()
        expect(screen.getByText('Integrity check unavailable')).toBeInTheDocument()
    })

    it('identifies a no-records check without asserting integrity', () => {
        render(<FinanceReconciliationPanel data={{ ...integrity, status: 'NO_RECORDS' }} />)
        expect(screen.getByText('No records checked')).toBeInTheDocument()
    })

    it('does not render raw enums or English server prose in Arabic', () => {
        language = 'ar'
        const { container } = render(<FinanceReconciliationPanel data={integrity} />)
        expect(container.textContent).not.toMatch(/NOT_APPLICABLE|English-only|\bOK\b/)
        expect(screen.getByText('غير منطبق')).toBeInTheDocument()
    })
})
