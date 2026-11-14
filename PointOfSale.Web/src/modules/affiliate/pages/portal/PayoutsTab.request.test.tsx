import { act, fireEvent, render, screen, waitFor } from '@testing-library/react'
import type { HTMLAttributes, ReactNode } from 'react'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { LanguageProvider } from '../../i18n/LanguageContext'
import PayoutsTab from './PayoutsTab'

const mocks = vi.hoisted(() => ({ getPayouts: vi.fn(), requestPayout: vi.fn() }))
vi.mock('../../../../services/affiliateApi', () => ({ default: mocks }))
// Animation frames are not UI authority in jsdom. Keep visibility assertions on
// the rendered feedback while exercising real animations in headed acceptance.
vi.mock('framer-motion', () => ({
    motion: {
        div: ({ children, className }: HTMLAttributes<HTMLDivElement>) => <div className={className}>{children}</div>,
    },
    AnimatePresence: ({ children }: { children: ReactNode }) => <>{children}</>,
}))
const balance = { availableForWithdrawal: 850, totalApproved: 850, totalPaid: 0, totalPendingRequests: 0, currency: 'EGP', minimumThreshold: 100, canWithdraw: true, history: [] }
const mount = () => render(<LanguageProvider><PayoutsTab formatCurrency={(amount: number) => `EGP ${amount}`} /></LanguageProvider>)
async function submit(language = 'en') {
    fireEvent.click(await screen.findByRole('button', { name: language === 'en' ? 'Request Withdrawal' : 'طلب سحب' }))
    fireEvent.change(await screen.findByRole('spinbutton'), { target: { value: '850' } })
    fireEvent.click(screen.getByRole('button', { name: language === 'en' ? 'Confirm Withdrawal' : 'تأكيد السحب' }))
}
describe('Withdrawal admission and error guidance', () => {
    beforeEach(() => {
        vi.resetAllMocks(); localStorage.setItem('affiliate_lang', 'en')
        mocks.getPayouts.mockResolvedValue(balance)
    })
    it.each(['en', 'ar'])('explains missing wallet in %s and preserves the stable server code', async language => {
        localStorage.setItem('affiliate_lang', language)
        mocks.requestPayout.mockRejectedValue({ response: { status: 409, data: { code: 'AFFILIATE_PAYOUT_WALLET_REQUIRED', message: 'private raw exception' } } })
        mount(); await submit(language)
        await waitFor(() => expect(screen.getByText('AFFILIATE_PAYOUT_WALLET_REQUIRED')).toBeVisible())
        expect(screen.getByRole('link', { name: language === 'en' ? 'Open payout settings' : 'فتح إعدادات الدفع' })).toHaveAttribute('href', '/affiliate/portal?tab=profile')
        expect(screen.queryByText('private raw exception')).not.toBeInTheDocument()
        await waitFor(() => expect(mocks.getPayouts).toHaveBeenCalledTimes(2))
    })
    it('submits exactly the available 850 and locks repeated clicks until persisted readback', async () => {
        let resolve!: (v: unknown) => void
        mocks.requestPayout.mockReturnValue(new Promise(r => { resolve = r }))
        mount(); await submit()
        fireEvent.click(screen.getByRole('button', { name: 'Requesting...' }))
        expect(mocks.requestPayout).toHaveBeenCalledExactlyOnceWith(850)
        await act(async () => resolve({ id: 'committed-request' }))
        await waitFor(() => expect(mocks.getPayouts).toHaveBeenCalledTimes(2))
    })
    it.each(['AFFILIATE_PAYOUT_INSUFFICIENT_ELIGIBLE_FUNDS', 'AFFILIATE_CONCURRENCY_CONFLICT', 'AFFILIATE_UNRECOGNIZED_CODE'])('refreshes authoritative state after %s', async code => {
        mocks.requestPayout.mockRejectedValue({ response: { status: 409, data: { code } } })
        mocks.getPayouts.mockResolvedValueOnce(balance).mockResolvedValue({ ...balance, availableForWithdrawal: 0, totalPendingRequests: 850, canWithdraw: false })
        mount(); await submit()
        await screen.findByText(code)
        await waitFor(() => expect(mocks.getPayouts).toHaveBeenCalledTimes(2))
    })
})
