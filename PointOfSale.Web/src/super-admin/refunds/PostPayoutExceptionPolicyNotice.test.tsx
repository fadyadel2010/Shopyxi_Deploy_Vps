import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { cleanup, render, screen } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import PostPayoutExceptionPolicyNotice from './PostPayoutExceptionPolicyNotice'

const mock = vi.hoisted(() => ({ get: vi.fn(), arabic: false }))
vi.mock('./postPayoutExceptionPolicy', () => ({ getPostPayoutExceptionPolicy: mock.get }))
vi.mock('../i18n/useSuperAdminLocale', () => ({ useSuperAdminLocale: () => ({ tr: (en: string, ar: string) => mock.arabic ? ar : en }) }))

function mount() {
    return render(<QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
        <PostPayoutExceptionPolicyNotice />
    </QueryClientProvider>)
}

describe('post-payout exception policy notice', () => {
    beforeEach(() => { vi.resetAllMocks(); mock.arabic = false })
    afterEach(cleanup)
    it('explains the policy dependency and accounting without an initiation or execution button', async () => {
        mock.get.mockResolvedValue({ canInitiate: false, standardReturnWindowDays: 14, defectClaimWindowDays: 30 })
        const { container } = mount()
        expect(await screen.findByText(/No approved policy authorizes/)).toBeInTheDocument()
        expect(screen.getByText(/independent reviewer permissions/)).toBeInTheDocument()
        expect(screen.getByText(/creates vendor debt/)).toBeInTheDocument()
        expect(screen.getByText(/14-day no-reason returns; 30-day/)).toBeInTheDocument()
        expect(container.querySelector('button, form, details')).toBeNull()
    })
    it('keeps the exception unavailable when policy discovery fails', async () => {
        mock.get.mockRejectedValue(new Error('Network unavailable'))
        mount()
        expect(await screen.findByRole('alert')).toHaveTextContent('No exception can be initiated')
        expect(screen.queryByRole('button')).not.toBeInTheDocument()
    })
    it('keeps the exception unavailable during loading', () => {
        mock.get.mockReturnValue(new Promise(() => {}))
        mount()
        expect(screen.getByText(/Checking exception policy/)).toBeInTheDocument()
        expect(screen.queryByRole('button')).not.toBeInTheDocument()
    })
    it('provides Arabic policy and financial consequences without a second guide', async () => {
        mock.arabic = true
        mock.get.mockResolvedValue({ canInitiate: false })
        const { container } = mount()
        expect(await screen.findByText(/لا توجد سياسة معتمدة/)).toBeInTheDocument()
        expect(screen.getByText(/المراجع المستقل/)).toBeInTheDocument()
        expect(screen.getByText(/دينًا على البائع/)).toBeInTheDocument()
        expect(container.querySelector('details, button')).toBeNull()
    })
})
