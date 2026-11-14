import { fireEvent, render, screen, waitFor, cleanup } from '@testing-library/react'
import { beforeEach, afterEach, describe, expect, it, vi } from 'vitest'
import { MemoryRouter, Route, Routes, useLocation } from 'react-router-dom'
import { LanguageProvider } from '../i18n/LanguageContext'
import { ThemeProvider } from '../theme/ThemeContext'
import AffiliateLanding from './AffiliateLanding'
import AffiliateCategoryPage from './AffiliateCategoryPage'
import AffiliateLogin from './AffiliateLogin'
import AffiliateRegister from './AffiliateRegister'
import AffiliateResetPassword from './AffiliateResetPassword'
import AffiliateVerifyEmail from './AffiliateVerifyEmail'
import CategoriesSection from './landing/CategoriesSection'
import translations from '../i18n/translations'
import { categoryDisplayName } from '../lib/categoryDisplayName'

const mocks = vi.hoisted(() => ({ products: vi.fn(), login: vi.fn(), register: vi.fn() }))
vi.mock('../lib/publicDiscoverApi', async original => ({ ...await original(), discoverPublicProducts: mocks.products }))
vi.mock('../../../services/affiliateAuthApi', async original => ({ ...await original(), affiliateAuthApi: {
    restoreSession: () => Promise.reject({ response: { status: 401 } }), login: mocks.login, register: mocks.register,
} }))
vi.mock('framer-motion', () => ({ motion: Object.fromEntries(['div', 'h1', 'p'].map(tag => [tag,
    ({ children, ...props }) => {
        const Tag = tag
        const native = Object.fromEntries(Object.entries(props).filter(([key]) => !['variants', 'initial', 'animate', 'whileInView', 'viewport', 'custom'].includes(key)))
        return <Tag {...native}>{children}</Tag>
    }])) }))
const category = { id: '11111111-1111-1111-1111-111111111111', name: 'Electronics', slug: 'electronics', productCount: 2, isActive: true }
const product = { productId: 'p1', name: 'Phone', price: 1234.5, currency: 'EGP', categoryName: 'Electronics', categorySlug: 'electronics' }
const Probe = () => <output data-testid="url">{useLocation().pathname}{useLocation().search}</output>
function show(component, route = '/affiliate', path = '/affiliate') {
    return render(<ThemeProvider><LanguageProvider><MemoryRouter initialEntries={[route]}><Routes>
        <Route path={path} element={<>{component}<Probe /></>} />
    </Routes></MemoryRouter></LanguageProvider></ThemeProvider>)
}
const switchToArabic = () => fireEvent.click(screen.getByRole('button', { name: /العربية/ }))
const switchToEnglish = () => fireEvent.click(screen.getByRole('button', { name: /English/ }))
beforeEach(() => {
    localStorage.clear()
    localStorage.setItem('affiliate_theme', 'light')
    mocks.products.mockReset().mockResolvedValue({ items: [product], totalCount: 2, totalPages: 2 })
    mocks.login.mockReset().mockRejectedValue({ response: { status: 401 } })
    mocks.register.mockReset()
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue({ ok: true, json: async () => [category, { id: 'empty', name: 'Empty', slug: 'empty', productCount: 0 }] }))
})
afterEach(() => { cleanup(); vi.unstubAllGlobals() })

describe('P54B public branding and reactive localization', () => {
    it('translates the entire landing, counts, accessibility and canonical labels without remount/refetch or fabricated metrics', async () => {
        const { container } = show(<AffiliateLanding />)
        expect(await screen.findByRole('link', { name: /Browse Electronics/ })).toHaveAttribute('href', '/affiliate/category/electronics')
        expect(screen.queryByText('Empty')).not.toBeInTheDocument()
        fireEvent.change(screen.getByRole('textbox'), { target: { value: 'retained search' } })
        const requests = mocks.products.mock.calls.length
        switchToArabic()
        expect(screen.getByRole('heading', { name: 'كيف يعمل؟' })).toBeInTheDocument()
        expect(screen.getByRole('heading', { name: 'تصفح الفئات' })).toBeInTheDocument()
        for (const key of ['step1Desc', 'step2Desc', 'step3Desc', 'whySubtitle', 'ctaDesc']) expect(screen.getByText(translations.ar.landing[key])).toBeInTheDocument()
        expect(screen.getByRole('link', { name: /تصفح إلكترونيات/ })).toHaveAttribute('href', '/affiliate/category/electronics')
        expect(screen.getByRole('textbox')).toHaveValue('retained search')
        expect(document.documentElement.dir).toBe('rtl')
        expect(container.textContent).toContain('ج.م.')
        expect(container.textContent).not.toMatch(/150K|2,500|98%|Paid automatically|دفع فوري|آلاف المسوّقين/)
        switchToEnglish(); switchToArabic(); switchToEnglish()
        expect(document.documentElement.dir).toBe('ltr')
        expect(mocks.products).toHaveBeenCalledTimes(requests)
        expect(container.textContent).not.toMatch(/Affiliate Hub|Affiliate Marketplace/)
        expect(screen.getAllByText('Shopixy Affiliate').length).toBeGreaterThanOrEqual(2)
    })
    it('keeps category URL, sort, search, selection and products across locale changes', async () => {
        show(<AffiliateCategoryPage />, '/affiliate/category/electronics?sort=price_asc', '/affiliate/category/:slug')
        await screen.findByText('Phone')
        fireEvent.change(screen.getByRole('textbox'), { target: { value: 'Phone' } })
        await waitFor(() => expect(mocks.products).toHaveBeenLastCalledWith(expect.objectContaining({ search: 'Phone' })))
        switchToArabic()
        expect(screen.getByRole('heading', { name: 'إلكترونيات' })).toBeInTheDocument()
        expect(screen.getByRole('combobox', { name: 'ترتيب المنتجات' })).toHaveValue('price_asc')
        expect(screen.getByTestId('url')).toHaveTextContent('/affiliate/category/electronics?sort=price_asc')
        expect(document.title).toBe('إلكترونيات | Shopixy Affiliate')
        expect(screen.getByRole('textbox')).toHaveValue('Phone')
        expect(screen.getByText('Phone')).toBeInTheDocument()
        expect(screen.getByText('٢ من المنتجات')).toBeInTheDocument()
        switchToEnglish()
        expect(screen.getByRole('heading', { name: 'Electronics' })).toBeInTheDocument()
    })
    it('localizes category loading, retry/error and empty states', async () => {
        localStorage.setItem('affiliate_lang', 'ar')
        fetch.mockRejectedValueOnce(new Error('offline'))
        const { rerender } = show(<CategoriesSection />)
        expect(screen.getByRole('status', { name: 'جاري التحميل...' })).toBeInTheDocument()
        expect(await screen.findByText('تعذر تحميل الفئات')).toBeInTheDocument()
        fetch.mockResolvedValue({ ok: true, json: async () => [] })
        fireEvent.click(screen.getByRole('button', { name: 'إعادة المحاولة' }))
        expect(await screen.findByText('لا توجد فئات متاحة حالياً')).toBeInTheDocument()
        expect(rerender).toBeDefined()
    })
    it('localizes empty/ineligible category routes and never substitutes unrelated products', async () => {
        localStorage.setItem('affiliate_lang', 'ar')
        mocks.products.mockResolvedValue({ items: [], totalCount: 0, totalPages: 0 })
        show(<AffiliateCategoryPage />, '/affiliate/category/empty', '/affiliate/category/:slug')
        expect(await screen.findByText(translations.ar.public.categoryEmpty)).toBeInTheDocument()
        expect(screen.queryByText('Phone')).not.toBeInTheDocument()
        expect(mocks.products).toHaveBeenCalledWith(expect.objectContaining({ categorySlug: 'empty' }))
    })
    it('localizes category product errors, retry, search misses and pagination', async () => {
        mocks.products.mockRejectedValueOnce(new Error('offline'))
        show(<AffiliateCategoryPage />, '/affiliate/category/electronics', '/affiliate/category/:slug')
        expect(await screen.findByText(translations.en.public.productsError)).toBeInTheDocument()
        switchToArabic()
        expect(screen.getByText(translations.ar.public.productsError)).toBeInTheDocument()
        fireEvent.click(screen.getByRole('button', { name: 'إعادة المحاولة' }))
        await screen.findByText('Phone')
        expect(screen.getByText('الصفحة ١ من ٢')).toBeInTheDocument()
        fireEvent.click(screen.getByRole('button', { name: 'التالي' }))
        await waitFor(() => expect(mocks.products).toHaveBeenLastCalledWith(expect.objectContaining({ page: 2 })))
        mocks.products.mockResolvedValue({ items: [], totalCount: 0, totalPages: 0 })
        fireEvent.change(screen.getByRole('textbox'), { target: { value: 'missing product' } })
        expect(await screen.findByText('لا توجد منتجات تطابق «missing product»')).toBeInTheDocument()
    })
    it('retranslates a registration validation error without creating an account', async () => {
        const { container } = show(<AffiliateRegister />)
        fireEvent.submit(container.querySelector('form'))
        expect(screen.getByText(translations.en.register.nameRequired)).toBeInTheDocument()
        switchToArabic()
        expect(screen.getByText(translations.ar.register.nameRequired)).toBeInTheDocument()
        expect(mocks.register).not.toHaveBeenCalled()
    })
    it.each([[AffiliateLogin, 'login'], [AffiliateRegister, 'register']])('brands entry points and retains form state', async (Page, section) => {
        show(<Page />)
        expect(screen.getByText('Shopixy Affiliate')).toBeInTheDocument()
        const email = screen.getByLabelText('Email')
        fireEvent.change(email, { target: { value: 'retained@example.invalid' } })
        switchToArabic()
        expect(screen.getByRole('heading', { name: translations.ar[section].title })).toBeInTheDocument()
        expect(screen.getByLabelText('البريد الإلكتروني')).toHaveValue('retained@example.invalid')
    })
    it('retranslates an already-mounted login error without sending another request', async () => {
        const { container } = show(<AffiliateLogin />)
        fireEvent.change(screen.getByLabelText('Email'), { target: { value: 'qa@example.invalid' } })
        fireEvent.change(screen.getByLabelText('Password'), { target: { value: 'invalid' } })
        fireEvent.submit(container.querySelector('form'))
        expect(await screen.findByText(translations.en.login.invalidCredentials)).toBeInTheDocument()
        switchToArabic()
        expect(screen.getByText(translations.ar.login.invalidCredentials)).toBeInTheDocument()
        expect(mocks.login).toHaveBeenCalledTimes(1)
    })
    it.each([[AffiliateResetPassword, 'reset'], [AffiliateVerifyEmail, 'verify']])('retranslates missing-token identity states', (Page, section) => {
        show(<Page />)
        expect(screen.getByText(translations.en.identity[section].missingToken)).toBeInTheDocument()
        switchToArabic()
        expect(screen.getByText(translations.ar.identity[section].missingToken)).toBeInTheDocument()
    })
    it('translates only canonical exact slugs and falls back safely for custom/missing labels', () => {
        const t = path => path.split('.').reduce((v, k) => v?.[k], translations.ar) ?? path
        expect(categoryDisplayName(category, t)).toBe('إلكترونيات')
        expect(categoryDisplayName({ slug: 'fashion', name: 'Fashion' }, t)).toBe('أزياء')
        expect(categoryDisplayName({ slug: 'merchant-electronics', name: 'Merchant custom name' }, t)).toBe('Merchant custom name')
        expect(categoryDisplayName({ slug: 'missing', name: '   ' }, t)).toBe('الفئة')
        expect(categoryDisplayName({ slug: 'electronics', name: 'Electronics' }, () => '')).toBe('Electronics')
    })
})
