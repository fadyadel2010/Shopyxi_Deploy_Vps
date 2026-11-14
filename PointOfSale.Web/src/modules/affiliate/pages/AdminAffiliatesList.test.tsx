import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { MemoryRouter } from 'react-router-dom'
import AdminAffiliatesList from './AdminAffiliatesList'
import i18n from '../../../i18n/config'

const list = vi.hoisted(() => vi.fn())
vi.mock('../../../services/adminAffiliateApi', () => ({ default: { listAffiliates: list } }))
vi.mock('../../../contexts/SettingsContext', () => ({ useSettings: () => ({ formatCurrency: String }) }))
vi.mock('../../../hooks/useAffiliateReadiness', () => ({ useAffiliateReadiness: () => ({ loading: false, isReady: true }) }))

describe('Merchant affiliate visibility', () => {
    beforeEach(async () => { list.mockReset(); await i18n.changeLanguage('en') })
    it('explains the merchant earnings scope instead of claiming no global enrollments', async () => {
        list.mockResolvedValue({ affiliates: [], totalCount: 0, totalPages: 0 })
        render(<MemoryRouter><AdminAffiliatesList /></MemoryRouter>)
        expect(await screen.findByText('No Affiliate Earnings for This Store Yet')).toBeInTheDocument()
        expect(screen.getByText(/Enrollment and promotions in other stores do not add/)).toBeInTheDocument()
        expect(screen.queryByText('No Affiliates Yet')).not.toBeInTheDocument()
    })
    it('shows failed requests as retryable errors, never an empty registry', async () => {
        list.mockRejectedValueOnce(new Error('unavailable'))
        render(<MemoryRouter><AdminAffiliatesList /></MemoryRouter>)
        expect(await screen.findByRole('alert')).toHaveTextContent('Unable to load')
        expect(screen.queryByText('No Affiliate Earnings for This Store Yet')).not.toBeInTheDocument()
        list.mockResolvedValue({ affiliates: [], totalCount: 0, totalPages: 0 })
        fireEvent.click(screen.getByRole('button', { name: 'Retry' }))
        expect(await screen.findByText('No Affiliate Earnings for This Store Yet')).toBeInTheDocument()
    })
    it('sends submitted name/referral search and status to the authority while labeling page-only counts', async () => {
        list.mockResolvedValue({ affiliates: [{ affiliateAccountId: 'owned', displayName: 'Owned Affiliate', referralCode: 'OWNED', status: 'Active', totalOrders: 7,
            earningsByCurrency: [{ currency: 'EGP', totalEarned: 123, totalApproved: 45, totalPaid: 6, netEarnings: 117 }] }], totalCount: 31, totalPages: 2 })
        render(<MemoryRouter><AdminAffiliatesList /></MemoryRouter>)
        const link = await screen.findByRole('link', { name: 'Owned Affiliate' })
        expect(link).toHaveAttribute('href', '/admin/affiliates/owned')
        expect(screen.getByText('Active (this page)')).toBeInTheDocument()
        expect(screen.getByText('31')).toBeInTheDocument()
        expect(screen.getByText('123')).toBeInTheDocument()
        expect(screen.getByText('45')).toBeInTheDocument()
        expect(screen.getByText('6')).toBeInTheDocument()
        fireEvent.change(screen.getByRole('textbox'), { target: { value: ' OWNED ' } })
        fireEvent.click(screen.getByRole('button', { name: 'Search' }))
        await waitFor(() => expect(list).toHaveBeenLastCalledWith(1, 20, '', 'OWNED'))
        fireEvent.change(screen.getByRole('combobox'), { target: { value: 'Suspended' } })
        await waitFor(() => expect(list).toHaveBeenLastCalledWith(1, 20, 'Suspended', 'OWNED'))
    })
    it('exposes loading before a successful empty response', async () => {
        let resolve!: (value: unknown) => void
        list.mockReturnValue(new Promise(done => { resolve = done }))
        render(<MemoryRouter><AdminAffiliatesList /></MemoryRouter>)
        expect(screen.getByRole('table')).toHaveAttribute('aria-busy', 'true')
        resolve({ affiliates: [], totalCount: 0, totalPages: 0 })
        await screen.findByText('No Affiliate Earnings for This Store Yet')
        expect(screen.getByRole('table')).toHaveAttribute('aria-busy', 'false')
    })
})
