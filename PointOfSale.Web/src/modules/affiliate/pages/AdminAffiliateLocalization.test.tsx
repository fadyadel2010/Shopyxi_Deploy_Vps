import { act, fireEvent, render, screen } from '@testing-library/react'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { MemoryRouter, Route, Routes } from 'react-router-dom'
import i18n from '../../../i18n/config'
import AdminAffiliatesList from './AdminAffiliatesList'
// @ts-expect-error Existing JavaScript detail page has no declaration.
import AdminAffiliateDetail from './AdminAffiliateDetail'

const api = vi.hoisted(() => ({ listAffiliates: vi.fn(), getAffiliateDetail: vi.fn(), getAffiliateOrders: vi.fn(), getAffiliateEarnings: vi.fn(), getAffiliateWithdrawals: vi.fn(), suspendAffiliate: vi.fn() }))
vi.mock('../../../services/adminAffiliateApi', () => ({ default: api }))
vi.mock('../../../hooks/useAffiliateReadiness', () => ({ useAffiliateReadiness: () => ({ loading: false, isReady: true }) }))
vi.mock('../../../contexts/SettingsContext', () => ({ useSettings: () => ({ formatCurrency: (value: number, currency: string) => new Intl.NumberFormat(i18n.language === 'ar' ? 'ar-EG' : 'en-US', { style: 'currency', currency }).format(value) }) }))

const affiliate = { affiliateAccountId: 'p53c-affiliate', displayName: 'P53C Test', referralCode: 'P53CTEST', status: 'Active', totalOrders: 12,
    earningsByCurrency: [{ currency: 'EGP', totalEarned: 1234.5, totalApproved: 100, totalPaid: 0, netEarnings: 100 }] }
const openDetail = () => render(<MemoryRouter initialEntries={['/admin/affiliates/p53c-affiliate']}><Routes><Route path="/admin/affiliates/:id" element={<AdminAffiliateDetail />} /></Routes></MemoryRouter>)

describe('Affiliate Admin English/Arabic localization', () => {
    beforeEach(async () => {
        vi.clearAllMocks(); await i18n.changeLanguage('en')
        api.listAffiliates.mockResolvedValue({ affiliates: [affiliate], totalCount: 1, totalPages: 1 })
        api.getAffiliateDetail.mockResolvedValue(affiliate)
        api.getAffiliateOrders.mockResolvedValue({ orders: [], totalCount: 0 })
        api.getAffiliateEarnings.mockResolvedValue([])
        api.getAffiliateWithdrawals.mockResolvedValue({ items: [], totalCount: 0 })
    })

    it('switches the mounted merchant list, headers, status and amounts without discarding search input', async () => {
        render(<MemoryRouter><AdminAffiliatesList /></MemoryRouter>)
        await screen.findByText('P53C Test')
        fireEvent.change(screen.getByRole('textbox'), { target: { value: 'unsent search' } })
        await act(() => i18n.changeLanguage('ar'))
        expect(screen.getByRole('heading', { name: 'إدارة المسوقين بالعمولة' })).toBeVisible()
        expect(screen.getByRole('textbox')).toHaveValue('unsent search')
        expect(screen.getByRole('columnheader', { name: 'رمز الإحالة' })).toBeVisible()
        expect(screen.getAllByText('نشط').length).toBeGreaterThan(0)
        expect(screen.queryByText('Affiliate Management')).not.toBeInTheDocument()
        expect(document.documentElement.dir).toBe('rtl')
        expect(screen.getByText(/١٬٢٣٤/)).toBeVisible()
        await act(() => i18n.changeLanguage('en'))
        expect(screen.getByRole('textbox')).toHaveValue('unsent search')
        expect(screen.getByRole('heading', { name: 'Affiliate Management' })).toBeVisible()
    })

    it.each(['en', 'ar'])('keeps load failure distinct from an empty registry in %s', async language => {
        await i18n.changeLanguage(language); api.listAffiliates.mockRejectedValueOnce(new Error('English backend message'))
        render(<MemoryRouter><AdminAffiliatesList /></MemoryRouter>)
        expect(await screen.findByRole('alert')).toHaveTextContent(i18n.t('adminAffiliates.loadError'))
        expect(screen.queryByText('English backend message')).not.toBeInTheDocument()
        api.listAffiliates.mockResolvedValue({ affiliates: [], totalCount: 0, totalPages: 0 })
        fireEvent.click(screen.getByRole('button', { name: i18n.t('adminAffiliates.retry') }))
        expect(await screen.findByText(i18n.t('adminAffiliates.empty'))).toBeVisible()
    })

    it('updates an open detail confirmation and validation while retaining unsaved reason and tab', async () => {
        openDetail(); await screen.findByRole('heading', { name: 'P53C Test' })
        fireEvent.click(screen.getByRole('button', { name: 'Suspend' }))
        const reason = screen.getByRole('textbox'); fireEvent.change(reason, { target: { value: 'P53C unsent reason' } })
        await act(() => i18n.changeLanguage('ar'))
        expect(screen.getByRole('textbox')).toHaveValue('P53C unsent reason')
        expect(screen.getByText(i18n.t('adminAffiliateDetail.modals.suspend.title'))).toBeVisible()
        expect(screen.queryByText('Suspend Affiliate')).not.toBeInTheDocument()
        fireEvent.click(screen.getByRole('button', { name: i18n.t('adminAffiliateDetail.actions.cancel') }))
        fireEvent.click(screen.getByRole('button', { name: /💸/ }))
        expect(await screen.findByText(i18n.t('adminAffiliateDetail.withdrawals.empty.title'))).toBeVisible()
        expect(api.suspendAffiliate).not.toHaveBeenCalled()
    })

    it.each(['orders', 'earnings', 'settlement', 'withdrawals'])('shows translated %s API failures with retry, not empty financial data', async tab => {
        api.getAffiliateOrders.mockRejectedValue(new Error('unavailable'))
        api.getAffiliateEarnings.mockRejectedValue(new Error('unavailable'))
        api.getAffiliateWithdrawals.mockRejectedValue(new Error('unavailable'))
        openDetail(); await screen.findByRole('heading', { name: 'P53C Test' })
        fireEvent.click(screen.getByRole('button', { name: new RegExp(i18n.t(`adminAffiliateDetail.tabs.${tab}`)) }))
        await screen.findByRole('alert'); await act(() => i18n.changeLanguage('ar'))
        expect(screen.getByRole('alert')).toHaveTextContent(i18n.t('adminAffiliates.detailLoadError'))
        expect(screen.getByRole('button', { name: i18n.t('adminAffiliates.retry') })).toBeVisible()
    })
})
