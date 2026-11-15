import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { MemoryRouter } from 'react-router-dom'
import DiscoverTabEnhanced from './DiscoverTabEnhanced'

const discover = vi.hoisted(() => vi.fn())
vi.mock('../../../../services/affiliateApi', () => ({ default: {
    discoverProductsIntelligence: discover, getDiscoverCategories: async () => [],
} }))
vi.mock('../../i18n/LanguageContext', () => ({ useLanguage: () => ({ t: (key: string) => key, formatNumber: String }) }))
vi.mock('./SortDropdown', () => ({ default: () => null }))
vi.mock('./ProductCard', () => ({
    default: ({ product, onClick }: any) => <button onClick={onClick}>{product.name}</button>,
    ProductCardSkeleton: () => <span>Checking current offers</span>,
}))
vi.mock('./ProductDrawer', () => ({ default: () => <div role="dialog">Offer detail</div> }))

describe('live Affiliate catalog readiness', () => {
    beforeEach(() => { discover.mockReset() })
    it.each(['focus', 'online', 'visibilitychange'])('retires stale cards and open offers on %s', async event => {
        discover.mockResolvedValueOnce({ items: [{ productId: 'product', name: 'Current offer' }], totalCount: 1 })
            .mockResolvedValue({ items: [], totalCount: 0 })
        const view = render(<MemoryRouter><DiscoverTabEnhanced /></MemoryRouter>)
        fireEvent.click(await screen.findByRole('button', { name: 'Current offer' }))
        expect(await screen.findByRole('dialog')).toBeInTheDocument()
        fireEvent(event === 'visibilitychange' ? document : window, new Event(event))
        await waitFor(() => expect(discover).toHaveBeenCalledTimes(2))
        await waitFor(() => expect(screen.queryByRole('button', { name: 'Current offer' })).not.toBeInTheDocument())
        await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())
        view.unmount()
        fireEvent(window, new Event('focus'))
        expect(discover).toHaveBeenCalledTimes(2)
    })
})
