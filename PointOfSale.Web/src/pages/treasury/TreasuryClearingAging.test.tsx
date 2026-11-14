import { describe, expect, it, vi, beforeEach } from 'vitest'
import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import TreasuryClearingAging from './TreasuryClearingAging.jsx'

const finApiMock = vi.hoisted(() => ({ getClearingAging: vi.fn() }))
const navigateMock = vi.hoisted(() => vi.fn())
const toastMocks = vi.hoisted(() => ({ showError: vi.fn(), showSuccess: vi.fn() }))
const translateMock = vi.hoisted(() => (key: string, fallback?: string) => fallback || key)
const auth = vi.hoisted(() => ({ user: { effectivePermissions: [] as string[] } }))

vi.mock('../../services/financialReportsApi', () => finApiMock)
vi.mock('react-router-dom', () => ({ useNavigate: () => navigateMock }))
vi.mock('react-i18next', () => ({
    useTranslation: () => ({ t: translateMock, i18n: { language: 'en' } }),
}))
vi.mock('../../contexts/SettingsContext', () => ({
    useSettings: () => ({ formatCurrency: (v: number) => `EGP ${Number(v).toFixed(2)}` }),
}))
vi.mock('../../components/Toast', () => ({ useToast: () => toastMocks }))
vi.mock('../../components/ReportTruthBadge', () => ({ default: () => null }))
vi.mock('../../store/authStore', () => ({ useAuthStore: (select: (state: typeof auth) => unknown) => select(auth) }))
vi.mock('../../hooks/useRTL', () => ({ useRTL: () => ({ dir: 'ltr', locale: 'en' }) }))
vi.mock('../../utils/dateFormat', () => ({
    formatDate: (d: unknown) => String(d),
    formatDateInputValue: () => '2026-09-16',
    formatDateTime: (d: unknown) => String(d),
    parseDateInput: () => new Date(2026, 8, 16),
}))

describe('TreasuryClearingAging evidence boundary', () => {
    beforeEach(() => {
        vi.clearAllMocks()
        auth.user.effectivePermissions = []
        finApiMock.getClearingAging.mockResolvedValue({
            totalPending: 55,
            accountsWithBalance: 1,
            oldestAgeDays: 9,
            lines: [{
                accountId: 'c1', accountCode: '9003', accountName: 'Harbor Clearing',
                pendingAmount: 55, ageDays: 9, transactionCount: 2,
                current: 10, days8To14: 45, days15To30: 0, over30Days: 0,
            }],
        })
    })

    it('keeps clearing read-only and hides canonical settlement navigation without permission', async () => {
        render(<TreasuryClearingAging />)
        await waitFor(() => expect(finApiMock.getClearingAging).toHaveBeenCalled())
        expect(await screen.findByText('manualWalletSettlement.clearingBoundary')).toBeInTheDocument()
        expect(screen.getByText('manualWalletSettlement.providerBoundary')).toBeInTheDocument()
        expect(screen.queryByRole('button', { name: 'manualCod.title' })).not.toBeInTheDocument()
        expect(screen.queryByRole('button', { name: 'manualWalletSettlement.title' })).not.toBeInTheDocument()
        expect(screen.queryByRole('button', { name: 'carrierRemittance.title' })).not.toBeInTheDocument()
        expect(navigateMock).not.toHaveBeenCalled()
    })

    it('delegates authorized navigation to canonical workflows without posting money from the report', async () => {
        auth.user.effectivePermissions = ['storefront.payments.manage']
        render(<TreasuryClearingAging />)
        fireEvent.click(await screen.findByRole('button', { name: 'manualCod.title' }))
        expect(navigateMock).toHaveBeenLastCalledWith('/treasury/manual-cod-settlements')
        fireEvent.click(screen.getByRole('button', { name: 'manualWalletSettlement.title' }))
        expect(navigateMock).toHaveBeenLastCalledWith('/treasury/manual-wallet-settlements')
        // Carrier remittance additionally requires finance authority.
        expect(screen.queryByRole('button', { name: 'carrierRemittance.title' })).not.toBeInTheDocument()
        expect(finApiMock.getClearingAging).toHaveBeenCalledOnce()
    })
})
