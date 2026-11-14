import { cleanup, fireEvent, render, screen } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { MemoryRouter, Route, Routes } from 'react-router-dom'
import { LanguageProvider } from '../i18n/LanguageContext'
import { ThemeProvider } from '../theme/ThemeContext'
import PublicProductImage from '../components/PublicProductImage'
import AffiliateLanding from './AffiliateLanding'
import AffiliateCategoryPage from './AffiliateCategoryPage'
import AffiliateProductDetail from './AffiliateProductDetail'
import AffiliateLandingDiscover from './landing/AffiliateLandingDiscover'

const mocks = vi.hoisted(() => ({ products: vi.fn(), detail: vi.fn() }))
vi.mock('../lib/publicDiscoverApi', async original => ({ ...await original(),
    discoverPublicProducts: mocks.products, discoverPublicProductDetail: mocks.detail,
}))
vi.mock('framer-motion', () => ({ motion: Object.fromEntries(['div', 'h1', 'p'].map(tag => [tag,
    ({ children, ...props }) => {
        const Tag = tag
        return <Tag {...Object.fromEntries(Object.entries(props).filter(([key]) =>
            !['variants', 'initial', 'animate', 'whileInView', 'viewport', 'custom'].includes(key)))}>{children}</Tag>
    }])) }))

const src = '/api/affiliate/discover/public-products/15e044bc-eaa1-c905-31a4-658aac4ad14b/media/2741f85507354b4b8d379e281d4d03a8.png'
const product = { productId: '15e044bc-eaa1-c905-31a4-658aac4ad14b', name: 'Public image product',
    images: [src], price: 25, currency: 'EGP', storeName: 'AhramStore', categorySlug: 'electronics' }

function show(component, path = '/affiliate', route = '/affiliate') {
    return render(<ThemeProvider><LanguageProvider><MemoryRouter initialEntries={[route]}><Routes>
        <Route path={path} element={component} />
    </Routes></MemoryRouter></LanguageProvider></ThemeProvider>)
}

beforeEach(() => {
    localStorage.clear()
    localStorage.setItem('affiliate_theme', 'light')
    mocks.products.mockReset().mockResolvedValue({ items: [product], totalCount: 1, totalPages: 1 })
    mocks.detail.mockReset().mockResolvedValue(product)
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue({ ok: true, json: async () => [
        { id: 'electronics', name: 'Electronics', slug: 'electronics', productCount: 1, isActive: true },
    ] }))
})
afterEach(() => { cleanup(); vi.unstubAllGlobals() })

describe('P54C canonical public media and missing-image fallback', () => {
    it.each([
        [AffiliateLanding, '/affiliate', '/affiliate'],
        [AffiliateCategoryPage, '/affiliate/category/:slug', '/affiliate/category/electronics'],
        [AffiliateProductDetail, '/affiliate/products/:slug', `/affiliate/products/${product.productId}`],
        [AffiliateLandingDiscover, '/affiliate', '/affiliate'],
    ])('uses backend-issued media on %s and replaces failed bytes with a localized fallback', async (Page, path, route) => {
        show(<Page />, path, route)
        const image = await screen.findByRole('img', { name: product.name })
        expect(image).toHaveAttribute('src', src)
        fireEvent.error(image)
        expect(screen.queryByRole('img', { name: product.name })).not.toBeInTheDocument()
        expect(screen.getByRole('img', { name: 'No image' })).toBeInTheDocument()
    })

    it('shows a fallback without making a media request when the product has no images', async () => {
        mocks.detail.mockResolvedValue({ ...product, images: [] })
        show(<AffiliateProductDetail />, '/affiliate/products/:slug', `/affiliate/products/${product.productId}`)
        expect(await screen.findByRole('img', { name: 'No image' })).toBeInTheDocument()
        expect(document.querySelector('img')).toBeNull()
    })

    it('does not retry a failed URL, but loads a newly selected source', () => {
        const wrapper = ({ children }) => <LanguageProvider>{children}</LanguageProvider>
        const { rerender } = render(<PublicProductImage src={src} alt="Product" />, { wrapper })
        fireEvent.error(screen.getByRole('img', { name: 'Product' }))
        rerender(<PublicProductImage src={src} alt="Product" />)
        expect(document.querySelector('img')).toBeNull()
        rerender(<PublicProductImage src={`${src}?new`} alt="Product" />)
        expect(screen.getByRole('img', { name: 'Product' })).toHaveAttribute('src', `${src}?new`)
    })

    it('uses the existing Arabic missing-image translation', () => {
        localStorage.setItem('affiliate_lang', 'ar')
        show(<PublicProductImage src="" alt="Product" />)
        expect(screen.getByRole('img')).toHaveAccessibleName('لا توجد صورة')
    })
})
