import { act, cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { I18nextProvider } from 'react-i18next'
import i18n from '../i18n/config'
import api from '../services/api'
import CommerceHubSettings from './CommerceHubSettings'

const mocks = vi.hoisted(() => ({
    user: { id: 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa', tenantId: 'bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb' },
    showError: vi.fn(), showSuccess: vi.fn(),
    inventoryStatus: 'out_of_stock', categoryId: 'kitchen',
}))
vi.mock('../components/Toast', () => ({ useToast: () => mocks }))
vi.mock('../store/authStore', () => ({ useAuthStore: selector => selector({ user: mocks.user }) }))
vi.mock('../auth/posVendorSessionAuthority', () => ({
    deriveCommerceHubVendorEligibilityPresentation: () => ({ canManageCatalog: true }),
}))
vi.mock('../contexts/SettingsContext', () => ({ useSettings: () => ({ settings: { currencyCode: 'EGP' } }) }))
vi.mock('../services/pagedArrayLoader', () => ({
    fetchAllPagedArray: async () => [{
        tenantId: mocks.user.tenantId, productId: 'product', name: 'QA product',
        variants: [{
            variantId: 'variant', sku: 'QA-SKU', price: 10, productCurrency: 'EGP',
            isCommerceHubCurrencyEligible: true, isCommerceHubPublicationEligible: true,
            listing: { categoryId: 'kitchen', parentCategoryId: 'home', commerceHubPrice: 12, isPublished: true },
        }],
    }],
}))
vi.mock('../services/api', () => ({ default: { get: vi.fn(), post: vi.fn(), put: vi.fn(), delete: vi.fn() } }))

function setupApi() {
    vi.mocked(api.get).mockImplementation(async (url, options) => {
        if (url === '/commercehub/categories/tree') return { data: [
            { id: 'home', name: 'Home & Garden', nameAr: 'المنزل والحديقة' },
            { id: 'kitchen', parentId: 'home', name: 'Kitchen', nameAr: 'مطبخ' },
        ] }
        if (url === '/admin/commercehub/listings') return { data: [{
            tenantId: mocks.user.tenantId, listingId: 'listing', productName: 'QA product', sku: 'QA-SKU',
            categoryId: mocks.categoryId, categoryName: 'Kitchen', inventoryStatus: mocks.inventoryStatus,
            inventoryAvailable: 0, isPublished: true, overrideEnabled: true, commerceHubPrice: 12,
        }] }
        if (url.endsWith('/commission/resolve-preview')) {
            const price = options.params.pricingMode === 'source' ? 10 : 12
            return { data: {
                ruleId: 'server-rule', source: 'Global', priority: 0, rate: 0.1,
                sourceProductPrice: 10, marketplacePrice: price, commissionAmount: price / 10,
                merchantNet: price * 0.9,
            } }
        }
        if (url.endsWith('/kpis')) return { data: { tenantId: mocks.user.tenantId } }
        return { data: [] }
    })
}

beforeEach(() => {
    vi.clearAllMocks()
    mocks.inventoryStatus = 'out_of_stock'
    mocks.categoryId = 'kitchen'
    setupApi()
})
afterEach(cleanup)

async function mount(language = 'ar') {
    await act(async () => { await i18n.changeLanguage(language) })
    return render(<I18nextProvider i18n={i18n}><CommerceHubSettings /></I18nextProvider>)
}

describe('CommerceHub overview localization', () => {
    it.each([
        ['ar', 'مطبخ', 'نفد المخزون', 'منشور'],
        ['en', 'Kitchen', 'Out of stock', 'Published'],
    ])('localizes the persisted listing taxonomy and inventory in %s', async (lang, category, inventory, status) => {
        const { container } = await mount(lang)
        const table = await screen.findByRole('table')
        expect(within(table).getByText(category)).toBeInTheDocument()
        expect(table.textContent).toContain(inventory)
        expect(table.textContent).toContain(status)
        expect(table.textContent).not.toContain('out_of_stock')
        expect(container.firstChild).toHaveAttribute('dir', lang === 'ar' ? 'rtl' : 'ltr')
    })

    it.each([
        ['in_stock', 'متوفر'], ['out_of_stock', 'نفد المخزون'],
        ['unavailable', 'غير متاح'], ['new_backend_enum', 'غير متاح'], [null, 'غير متاح'],
    ])('uses known Arabic labels without leaking raw %s identifiers', async (status, label) => {
        mocks.inventoryStatus = status
        await mount()
        const table = await screen.findByRole('table')
        expect(table.textContent).toContain(label)
        if (status) expect(table.textContent).not.toContain(status)
    })

    it('does not invent an Arabic category from an unresolvable English row label', async () => {
        mocks.categoryId = 'missing-category'
        await mount()
        const table = await screen.findByRole('table')
        expect(table.textContent).not.toContain('Kitchen')
        expect(table.textContent).toContain(i18n.t('common.unavailable'))
    })

    it('completes Arabic source and custom numeric rendering from the real preview hook without writes', async () => {
        const { container } = await mount()
        fireEvent.click(await screen.findByRole('button', { name: /QA product/ }))
        const money = amount => new Intl.NumberFormat('ar-EG', { style: 'currency', currency: 'EGP' }).format(amount)
        const panel = () => container.querySelector('h4')?.closest('section')
        const values = () => Array.from(panel()?.querySelectorAll('dd') || []).map(el => el.textContent)
        await waitFor(() => expect(values()).toEqual([money(10), money(12), '١٠%', `− ${money(1.2)}`, money(10.8)]))
        fireEvent.click(screen.getByRole('radio', { name: /استخدام سعر المنتج المصدر/ }))
        expect(panel().querySelector('.animate-spin')).toBeInTheDocument()
        await waitFor(() => expect(values()).toEqual([money(10), money(10), '١٠%', `− ${money(1)}`, money(9)]))
        expect(panel().querySelector('.animate-spin')).not.toBeInTheDocument()
        expect(api.get).toHaveBeenLastCalledWith('/admin/commercehub/commission/resolve-preview',
            expect.objectContaining({ params: expect.objectContaining({ pricingMode: 'source', marketplacePrice: undefined }) }))
        fireEvent.click(screen.getByRole('radio', { name: /سعر مخصص للسوق/ }))
        await waitFor(() => expect(values()).toEqual([money(10), money(12), '١٠%', `− ${money(1.2)}`, money(10.8)]))
        expect(api.post).not.toHaveBeenCalled()
        expect(api.put).not.toHaveBeenCalled()
        expect(api.delete).not.toHaveBeenCalled()
    })
})
