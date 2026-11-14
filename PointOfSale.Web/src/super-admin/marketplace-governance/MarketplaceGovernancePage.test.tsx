import { act } from 'react'
import { render, screen, waitFor } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter } from 'react-router-dom'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import MarketplaceGovernancePage from './MarketplaceGovernancePage'

const service = vi.hoisted(() => ({ dashboard: vi.fn(), listings: vi.fn() }))
vi.mock('./marketplaceGovernanceService', () => ({
    getMarketplaceGovernanceDashboard: service.dashboard,
    getMarketplaceGovernanceListings: service.listings,
    restoreMarketplaceListing: vi.fn(), restoreMarketplaceVendor: vi.fn(),
    suppressMarketplaceListing: vi.fn(), suspendMarketplaceVendor: vi.fn(),
}))
vi.mock('../i18n/useSuperAdminLocale', () => ({
    useSuperAdminLocale: () => ({
        isArabic: false, tr: (english: string) => english,
        formatNumber: (value: number) => String(value), formatDateTime: (value: string) => value,
    }),
}))

describe('MarketplaceGovernancePage current dashboard authority', () => {
    let client: QueryClient
    beforeEach(() => {
        client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
        service.dashboard.mockReset().mockResolvedValue({ totalListings: 987654 })
        service.listings.mockReset().mockResolvedValue({ listings: [], meta: { totalPages: 0, totalCount: 0 } })
    })
    afterEach(() => client.clear())
    const renderPage = () => render(
        <QueryClientProvider client={client}><MemoryRouter><MarketplaceGovernancePage /></MemoryRouter></QueryClientProvider>,
    )

    it('withholds a cached dashboard during revalidation and renders only the new accepted response', async () => {
        renderPage()
        expect(await screen.findByText('987654')).toBeInTheDocument()
        let complete!: (value: { totalListings: number }) => void
        service.dashboard.mockImplementationOnce(() => new Promise((resolve) => { complete = resolve }))
        let refresh!: Promise<void>
        act(() => { refresh = client.invalidateQueries({ queryKey: ['super-admin', 'marketplace-governance', 'dashboard'] }) })
        await waitFor(() => expect(screen.queryByText('987654')).not.toBeInTheDocument())
        await act(async () => { complete({ totalListings: 123456 }); await refresh })
        expect(await screen.findByText('123456')).toBeInTheDocument()
    })

    it('does not turn failed refresh into a cached success or an invented zero dashboard', async () => {
        renderPage()
        expect(await screen.findByText('987654')).toBeInTheDocument()
        service.dashboard.mockRejectedValueOnce(new Error('Authority unavailable'))
        await act(async () => { await client.invalidateQueries({ queryKey: ['super-admin', 'marketplace-governance', 'dashboard'] }) })
        expect(await screen.findByText('Governance data could not be verified')).toBeInTheDocument()
        expect(screen.queryByText('987654')).not.toBeInTheDocument()
        expect(screen.getAllByText('Unavailable').length).toBeGreaterThan(0)
    })
})
