import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { LanguageProvider, useLanguage } from '../../i18n/LanguageContext'
import DashboardTab from './DashboardTab'
import EarningsTab from './EarningsTab'
import PayoutsTab from './PayoutsTab'
import ProfileTab from './ProfileTab'
// @ts-expect-error Existing JavaScript tab has no declaration.
import LandingPagesTab from './LandingPagesTab'
// @ts-expect-error Existing JavaScript translation resource has no declaration.
import translations from '../../i18n/translations'
import LinksTab from './LinksTab'
// @ts-expect-error Existing JavaScript card has no declaration.
import ProductCard from './ProductCard'

const api = vi.hoisted(() => ({ getDashboard: vi.fn(), getAnalyticsSummary: vi.fn(), getAnalyticsTrend: vi.fn(), getOrders: vi.fn(), getCommissionActivity: vi.fn(), saveDesign: vi.fn(), getEarningsSummary: vi.fn(), getPayouts: vi.fn(), listPromotionLinks: vi.fn(), cancelPayout: vi.fn(), getProfile: vi.fn(), landingPages: vi.fn() }))
vi.mock('../../../../services/affiliateApi', () => ({ default: api }))
vi.mock('../../../../services/affiliateAuthApi', () => ({ affiliateAxios: { get: api.landingPages, put: api.saveDesign } }))
vi.mock('recharts', () => ({ ResponsiveContainer: () => null, BarChart: () => null, LineChart: () => null,
    Line: () => null, Bar: () => null, XAxis: () => null, YAxis: () => null, CartesianGrid: () => null, Tooltip: () => null }))

const summary = { currency: 'EGP', totalEarned: 1234.5, totalApproved: 500, totalPaid: 0, totalReversed: 0, netEarnings: 500 }
const pending = { id: 'p53c-request', requestedAmount: 150, remainingAmount: 100, approvedAmount: 120, currency: 'EGP', status: 'Pending', canCancel: true, requestedAtUtc: '2026-10-23T12:00:00Z' }
type Tab = typeof EarningsTab | typeof LinksTab
function Harness({ Component }: { Component: Tab }) {
    const { toggleLang, dir, formatMoney } = useLanguage()
    return <div dir={dir}><button onClick={toggleLang}>Switch</button><Component formatCurrency={formatMoney} /></div>
}
const open = (Component: Tab) => render(<LanguageProvider><Harness Component={Component} /></LanguageProvider>)

describe('Portal locale rendering and mounted state', () => {
    beforeEach(() => {
        localStorage.setItem('affiliate_lang', 'en'); vi.clearAllMocks()
        api.getDashboard.mockResolvedValue({ earningsByCurrency: [summary], totalOrders: 12 }); api.getAnalyticsSummary.mockResolvedValue(null)
        api.getEarningsSummary.mockResolvedValue([summary])
        api.getCommissionActivity.mockResolvedValue({ entries: [], totalPages: 0 })
        api.getAnalyticsTrend.mockResolvedValue([]); api.getOrders.mockResolvedValue({ orders: [] })
        api.saveDesign.mockResolvedValue({ data: { templateType: 'Storytelling:v1', hasSavedDesign: true } })
        api.getPayouts.mockResolvedValue({ ...summary, availableForWithdrawal: 350, totalPendingRequests: 150, minimumThreshold: 100, canWithdraw: true, history: [pending] })
        api.listPromotionLinks.mockResolvedValue([{ id: 'p53c-link', productName: 'P53C product', referralCode: 'P53CREF', isActive: true, promotionUrl: 'http://ahramstore.localhost:5174/products/test?ref=P53CREF', createdAt: '2026-10-23T12:00:00Z' }])
        api.getProfile.mockResolvedValue({ displayName: 'P53C profile', referralCode: 'P53CREF', status: 'Active', createdAt: '2026-10-23T12:00:00Z', payoutWalletType: 'Other', payoutWalletNumber: 'P53CNOTRANSFER' })
        api.landingPages.mockResolvedValue({ data: { affiliateCode: 'P53CREF', totalPages: 1, landingPages: [{ productId: 'p53c-product', productName: 'P53C product', price: 100, currency: 'EGP', estimatedCommission: 10, commissionRatePercentage: 10, absoluteLandingUrl: 'http://ahramstore.localhost:5174/p/P53CREF/product', templateType: 'Classic:v1' }] } })
    })

    it.each([DashboardTab, EarningsTab])('changes real EGP summary labels and digits without refetching or changing values', async Component => {
        open(Component); await screen.findAllByText(/1,234.50/)
        fireEvent.click(screen.getByRole('button', { name: 'Switch' }))
        await waitFor(() => expect(screen.getAllByText(/١٬٢٣٤٫٥٠/)[0]).toBeVisible())
        expect(localStorage.getItem('affiliate_lang')).toBe('ar')
        expect(api.getDashboard.mock.calls.length + api.getEarningsSummary.mock.calls.length).toBe(1)
    })

    it.each([DashboardTab, EarningsTab, LinksTab])('translates a mounted API error and retries through the real tab', async Component => {
        api.getDashboard.mockRejectedValue(new Error('Untranslated server prose'))
        api.getEarningsSummary.mockRejectedValue(new Error('Untranslated server prose'))
        api.listPromotionLinks.mockRejectedValue(new Error('Untranslated server prose'))
        open(Component); await screen.findByRole('alert')
        fireEvent.click(screen.getByRole('button', { name: 'Switch' }))
        expect(screen.getByRole('alert')).toHaveTextContent(/تعذر|فشل/)
        expect(screen.queryByText('Untranslated server prose')).not.toBeInTheDocument()
    })

    it('translates link dates and labels while preserving the authoritative destination', async () => {
        open(LinksTab); const link = await screen.findByRole('link')
        const destination = link.getAttribute('href'); fireEvent.click(screen.getByRole('button', { name: 'Switch' }))
        expect(screen.getByRole('link')).toHaveAttribute('href', destination)
        await waitFor(() => expect(screen.getByText(/٢٣/)).toBeVisible())
        expect(screen.queryByText('Active')).not.toBeInTheDocument()
    })

    it('retains cancellation reason and dynamically translates success feedback after switching', async () => {
        api.cancelPayout.mockResolvedValue({}); open(PayoutsTab)
        fireEvent.click((await screen.findAllByRole('button', { name: 'Cancel withdrawal' }))[0])
        fireEvent.change(screen.getByRole('textbox'), { target: { value: 'P53C unsent reason' } })
        fireEvent.click(screen.getByRole('button', { name: 'Switch' }))
        expect(screen.getByRole('textbox')).toHaveValue('P53C unsent reason')
        fireEvent.click(screen.getByRole('button', { name: 'تأكيد الإلغاء' }))
        await waitFor(() => expect(api.cancelPayout).toHaveBeenCalledWith(pending.id, 'P53C unsent reason'))
        await screen.findByText(/تم إلغاء/)
        fireEvent.click(screen.getByRole('button', { name: 'Switch' }))
        await waitFor(() => expect(screen.getByText(/Withdrawal cancelled/)).toBeVisible())
        expect(api.cancelPayout).toHaveBeenCalledTimes(1)
    })

    it('localizes product fallback text, commission percentage and unknown currency', () => {
        localStorage.setItem('affiliate_lang', 'ar')
        render(<LanguageProvider><ProductCard product={{ price: 25.75, commissionRate: .1, estimatedCommission: 2.575 }} onClick={() => {}} /></LanguageProvider>)
        expect(screen.getAllByText('العملة غير متاحة').length).toBe(2)
        expect(screen.getByText('١٠٪؜')).toBeInTheDocument()
        expect(screen.queryByText('Currency unavailable')).not.toBeInTheDocument()
    })

    it('has matching English and Arabic translation leaf keys', () => {
        const keys = (object: Record<string, unknown>, prefix = ''): string[] => Object.entries(object).flatMap(([key, value]) =>
            typeof value === 'object' && value !== null ? keys(value as Record<string, unknown>, `${prefix}${key}.`) : [`${prefix}${key}`])
        expect(keys(translations.ar).sort()).toEqual(keys(translations.en).sort())
    })

    it('localizes profile wallet labels and preserves unsent edits across switching', async () => {
        open(ProfileTab); fireEvent.click(await screen.findByRole('button', { name: 'Edit payout wallet' }))
        fireEvent.change(screen.getByLabelText('Wallet Number'), { target: { value: 'P53CUNSENT' } })
        fireEvent.click(screen.getByRole('button', { name: 'Switch' }))
        expect(screen.getByLabelText('رقم المحفظة')).toHaveValue('P53CUNSENT')
        expect(screen.getByRole('button', { name: 'حفظ المحفظة' })).toBeInTheDocument()
        expect(screen.queryByText('Payout Wallet')).not.toBeInTheDocument()
    })

    it('localizes landing templates without replacing the selected referral destination', async () => {
        open(LandingPagesTab); fireEvent.click(await screen.findByRole('button', { name: 'Storytelling' }))
        expect(screen.getByRole('button', { name: 'Storytelling' })).toHaveAttribute('aria-pressed', 'true')
        fireEvent.click(screen.getByRole('button', { name: 'Save design' }))
        await waitFor(() => expect(api.saveDesign).toHaveBeenCalledWith('/affiliate/landing-pages/p53c-product/design', { templateType: 'Storytelling:v1' }))
        fireEvent.click(screen.getByRole('button', { name: 'Switch' }))
        expect(screen.getByRole('button', { name: 'قصصي' })).toBeInTheDocument()
        expect(screen.getByText('http://ahramstore.localhost:5174/p/P53CREF/product')).toBeInTheDocument()
        expect(screen.queryByText('Landing Page Design')).not.toBeInTheDocument()
    })

    it.each(['en', 'ar'])('exposes translated loading status in %s', language => {
        localStorage.setItem('affiliate_lang', language); api.getDashboard.mockReturnValue(new Promise(() => {}))
        open(DashboardTab)
        expect(screen.getByRole('status')).toHaveAccessibleName(language === 'ar' ? 'جارٍ التحميل...' : 'Loading...')
    })
})
