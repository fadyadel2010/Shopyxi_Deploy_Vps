import { act, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import AffiliateProducts from './AffiliateProducts'
import i18n from '../../../i18n/config'
import { adminCategoryDisplayName } from '../lib/adminCategoryDisplayName'

const api = vi.hoisted(() => ({ listProducts: vi.fn(), getCategoryTree: vi.fn() }))
vi.mock('../../../services/adminProductAffiliateApi', () => ({ default: api }))
vi.mock('../../../components/ProductAffiliateSettingsCard', () => ({ default: () => <div>Settings</div> }))
vi.mock('../../../common/confirm/ConfirmDialogProvider', () => ({ useConfirmDialog: () => ({ confirm: vi.fn() }) }))
vi.mock('../../../contexts/SettingsContext', () => ({ useSettings: () => ({ settings: { currencyCode: 'EGP' } }) }))

beforeEach(async () => {
    vi.clearAllMocks()
    await i18n.changeLanguage('en')
    api.getCategoryTree.mockResolvedValue([{ id: 'electronics-id', name: 'Electronics', slug: 'electronics', subcategories: [] }])
    api.listProducts.mockResolvedValue({ totalCount: 2, totalPages: 1, items: [
        { productId: 'draft', productName: 'Draft Product', productNameAr: 'منتج مسودة', isProductActive: true, publishStatus: 'Draft', hasAffiliatePolicy: true, isAffiliateEnabled: true, commissionRate: .123456, maxCommissionPerItem: 12.3456, affiliateCategoryId: 'electronics-id', affiliateCategoryName: 'Electronics' },
        { productId: 'plain', productName: 'Plain Product', isProductActive: true, publishStatus: 'Published', hasAffiliatePolicy: false, isAffiliateEnabled: false },
    ] })
})

describe('P54D product authority presentation and localization', () => {
    it('keeps unknown category names and identities unchanged', () => {
        const category = { id: 'custom', slug: 'task-custom', name: 'Canonical custom name' }
        expect(adminCategoryDisplayName(category, 'ar')).toBe('Canonical custom name')
        expect(category).toEqual({ id: 'custom', slug: 'task-custom', name: 'Canonical custom name' })
    })
    it('separates publication, policy and absent policies without rounding saved precision', async () => {
        render(<AffiliateProducts />)
        const draft = (await screen.findByText('Draft Product')).closest('tr')
        expect(draft).toHaveTextContent('Publication: Draft')
        expect(draft).toHaveTextContent('12.3456%')
        expect(draft).toHaveTextContent('12.3456')
        expect(draft).toHaveTextContent('EGP')
        expect(screen.getByText('Plain Product').closest('tr')).toHaveTextContent('No policy')
        expect(screen.getByText('Showing 1–2 of 2')).toBeVisible()
        expect(screen.getByText(/not necessarily eligible/)).toBeVisible()
    })
    it('switches mounted product names, status labels, percentages and counts without losing filters or route context', async () => {
        render(<AffiliateProducts />)
        await screen.findByText('Draft Product')
        fireEvent.change(screen.getByRole('textbox'), { target: { value: 'draft' } })
        fireEvent.click(screen.getByRole('button', { name: 'Search' }))
        fireEvent.change(screen.getByLabelText('Policy Status'), { target: { value: 'enabled' } })
        await waitFor(() => expect(api.listProducts).toHaveBeenLastCalledWith(1, 20, expect.objectContaining({ search: 'draft', affiliateStatus: 'enabled' })))
        await act(() => i18n.changeLanguage('ar'))
        expect(screen.getByRole('textbox')).toHaveValue('draft')
        expect(screen.getByLabelText('حالة السياسة')).toHaveValue('enabled')
        expect(screen.getByText('منتج مسودة')).toBeVisible()
        expect(screen.getByText('منتج مسودة').closest('tr')).toHaveTextContent('١٢٫٣٤٥٦')
        expect(screen.getByText('منتج مسودة').closest('tr')).toHaveTextContent('إلكترونيات')
        expect(screen.getByRole('option', { name: 'إلكترونيات' })).toHaveValue('electronics-id')
        expect(document.documentElement.dir).toBe('rtl')
        expect(screen.getByRole('button', { name: 'إدارة: منتج مسودة' })).toBeVisible()
    })
})
