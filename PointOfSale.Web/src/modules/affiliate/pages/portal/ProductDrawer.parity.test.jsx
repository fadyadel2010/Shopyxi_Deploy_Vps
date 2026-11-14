import { act, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { LanguageProvider, useLanguage } from '../../i18n/LanguageContext'
import affiliateApi from '../../../../services/affiliateApi'
import ProductDrawer from './ProductDrawer'
import ProductCard from './ProductCard'

vi.mock('../../../../services/affiliateApi', () => ({ default: {
    getProductIntelligence: vi.fn(), listPromotionLinks: vi.fn(), createPromotionLink: vi.fn(),
} }))

const deferred = () => { let resolve; const promise = new Promise(r => { resolve = r }); return { promise, resolve } }
const product = id => ({ productId: id, name: `Product ${id}`, description: `Description ${id}`, images: [], currency: 'EGP', price: 25, variants: [] })
function Locale() { const { toggleLang } = useLanguage(); return <button onClick={toggleLang}>locale</button> }
const tree = id => <LanguageProvider><Locale /><div data-testid="animated-tab" style={{ transform: 'translateY(8px)' }}><ProductDrawer productId={id} onClose={() => {}} /></div></LanguageProvider>

describe('P5.7 drawer identity and media', () => {
    beforeEach(() => { vi.clearAllMocks(); localStorage.setItem('affiliate_lang', 'en'); affiliateApi.listPromotionLinks.mockResolvedValue([]) })
    it('keeps the latest selection visible when old requests finish after selection and locale changes', async () => {
        const a = deferred(), b = deferred()
        affiliateApi.getProductIntelligence.mockImplementation(id => id === 'A' ? a.promise : b.promise)
        const { rerender } = render(tree('A'))
        const firstSignal = affiliateApi.getProductIntelligence.mock.calls[0][1].signal
        rerender(tree('B'))
        expect(firstSignal.aborted).toBe(true)
        fireEvent.click(screen.getByText('locale'))
        await act(async () => b.resolve(product('B')))
        const heading = await screen.findByRole('heading', { name: 'Product B' })
        await act(async () => a.resolve(product('A')))
        expect(screen.getByRole('heading', { name: 'Product B' })).toBe(heading)
        expect(screen.queryByText('Product A')).not.toBeInTheDocument()
        fireEvent.click(screen.getByText('locale'))
        expect(screen.getByRole('heading', { name: 'Product B' })).toBe(heading)
        expect(affiliateApi.getProductIntelligence).toHaveBeenCalledTimes(2)
        expect(screen.getByTestId('animated-tab')).not.toContainElement(screen.getByRole('dialog'))
        expect(screen.getByRole('dialog').parentElement).toHaveAttribute('dir', 'ltr')
    })
    it('renders a localized missing-file fallback without retry and recovers for a different image', async () => {
        const { rerender } = render(<LanguageProvider><ProductCard product={{ ...product('A'), images: ['/api/affiliate/discover/public-products/aa/media/one.png'] }} onClick={() => {}} /></LanguageProvider>)
        fireEvent.error(screen.getByAltText('Product A'))
        expect(screen.queryByAltText('Product A')).not.toBeInTheDocument()
        expect(screen.getByRole('img', { name: 'No image' })).toBeInTheDocument()
        rerender(<LanguageProvider><ProductCard product={{ ...product('B'), images: ['/api/affiliate/discover/public-products/bb/media/two.png'] }} onClick={() => {}} /></LanguageProvider>)
        await waitFor(() => expect(screen.getByAltText('Product B')).toHaveAttribute('src', '/api/affiliate/discover/public-products/bb/media/two.png'))
    })
})
