import { describe, expect, it, vi } from 'vitest'
import { render, screen } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import MarketplaceGovernancePage from './MarketplaceGovernancePage'

vi.mock('./marketplaceGovernanceService', () => ({
    getMarketplaceGovernanceDashboard: vi.fn().mockRejectedValue(new Error('SQL authority rejected')),
    getMarketplaceGovernanceListings: vi.fn().mockRejectedValue(new Error('Listing ownership could not be verified')),
}))

describe('governance query rejection presentation', () => {
    it('shows an authority error instead of a legitimate zero-listing state', async () => {
        const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
        render(<QueryClientProvider client={client}><MarketplaceGovernancePage /></QueryClientProvider>)
        expect(await screen.findByRole('alert')).toHaveTextContent('Governance data could not be verified')
        expect(screen.getByText('Listing count unavailable')).toBeInTheDocument()
        expect(screen.queryByText(/0 listings, page/)).not.toBeInTheDocument()
        expect(screen.queryByText('Suppress listing', { exact: true })).not.toBeInTheDocument()
    })
})
