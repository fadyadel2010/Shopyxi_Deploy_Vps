/**
 * RB-05R — representative rendered-surface checks.
 *
 * The catalog gate proves the 284 previously missing keys exist. This file
 * proves that representative affected screens actually paint them, in English
 * and in Arabic, instead of leaking the key or falling back to English.
 */
import { cleanup, fireEvent, render, screen } from '@testing-library/react'
import { MemoryRouter } from 'react-router-dom'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import i18n from '../i18n/config'
import Settings from '../pages/Settings'
import StorefrontLiveMonitoring from '../components/StorefrontLiveMonitoring'
import { getRejectionReasonValidationMessage } from '../pages/manualPaymentValidation'
import { SettingsProvider } from '../contexts/SettingsContext'
import storefrontAnalyticsApi from '../services/storefrontAnalyticsApi'

const mocks = vi.hoisted(() => ({
    get: vi.fn(),
    put: vi.fn(),
    live: vi.fn(),
    platform: { isRTL: false, dir: 'ltr', locale: 'en-US' },
}))

vi.mock('../services/api', () => ({
    default: { get: mocks.get, put: mocks.put },
    isRequestTimeout: () => false,
}))
vi.mock('../components/Toast', () => ({
    useToast: () => ({ showSuccess: vi.fn(), showError: vi.fn(), showWarning: vi.fn() }),
}))
vi.mock('../store/authStore', () => ({
    useAuthStore: (selector: (state: { user: { role: string }; isAuthenticated: boolean; hasHydrated: boolean }) => unknown) =>
        selector({ user: { role: 'Owner' }, isAuthenticated: true, hasHydrated: true }),
}))
vi.mock('../common/confirm/ConfirmDialogProvider', () => ({ useConfirmDialog: () => ({ confirm: vi.fn() }) }))
vi.mock('../auth/posPermissions', () => ({ hasPosPermission: () => true }))
vi.mock('../auth/storefrontAdminAuthority', () => ({ canManageStorefrontSettings: () => true }))
vi.mock('../pages/StorefrontSettings', () => ({ default: () => null }))
vi.mock('../modules/storefront/informationArchitecture', () => ({ StorefrontBreadcrumbs: () => null }))
vi.mock('../hooks/useRTL', () => ({ useRTL: () => mocks.platform }))
vi.mock('../services/storefrontAnalyticsApi', () => ({ default: { getStorefrontLiveMonitoring: mocks.live } }))

const affectedStorefrontAdminKeys = [
    'ManualPayments.messages.rejectionReasonTooShort',
    'ManualPayments.messages.rejectionReasonTooLong',
]

const affectedSettingsKeys = [
    'settings.receiptPreview',
    'settings.defaultCompanyName',
    'settings.defaultReceiptHeader',
    'settings.defaultReceiptFooter',
    'settings.sampleItemOne',
    'settings.sampleItemTwo',
    'settings.currencyCode',
    'settings.defaultTaxRateHelp',
]

const affectedOtherKeys = [
    'storefrontLive.title',
    'storefrontLive.subtitle',
    'storefrontLive.liveVisitors',
    'storefrontLive.activeCarts',
    'storefrontLive.checkoutInProgress',
    'storefrontLive.recentOrders',
    'storefrontLive.recentActivity',
    'storefrontLive.recentActivitySubtitle',
    'storefrontLive.polling',
    'storefrontLive.noRecentActivity',
    'storefrontLive.activity.addToCart',
]

function expectNoRawKeys(container: HTMLElement, keys: string[]) {
    const text = container.textContent ?? ''
    for (const key of keys) {
        expect(text.includes(key), `${key} leaked as raw text`).toBe(false)
    }
}

async function useLanguage(language: 'en' | 'ar') {
    mocks.platform.isRTL = language === 'ar'
    mocks.platform.dir = language === 'ar' ? 'rtl' : 'ltr'
    mocks.platform.locale = language === 'ar' ? 'ar-EG' : 'en-US'
    await i18n.changeLanguage(language)
}

function renderSettings() {
    return render(
        <MemoryRouter>
            <SettingsProvider>
                <Settings />
            </SettingsProvider>
        </MemoryRouter>,
    )
}

beforeEach(() => {
    vi.clearAllMocks()
    mocks.get.mockImplementation(async (path: string) => ({
        data: path === '/settings'
            ? {
                companyName: '',
                companyAddress: '',
                companyPhone: '',
                companyEmail: '',
                companyLogo: '',
                receiptHeader: '',
                receiptFooter: '',
                showLogoOnReceipt: false,
                currencyCode: 'EGP',
                currencySymbol: 'E£',
                taxRate: 0,
            }
            : { countryCode: 'EG', isConfigured: true, tenantName: 'Demo' },
    }))
    mocks.put.mockResolvedValue({ data: {} })
    mocks.live.mockResolvedValue({
        liveVisitors: 12,
        activeCarts: 6,
        checkoutInProgress: 4,
        recentOrders: 10,
        recentActivity: [{ type: 'AddToCart', product: 'Demo product', occurredAtUtc: new Date().toISOString() }],
    })
})

afterEach(async () => {
    cleanup()
    await useLanguage('en')
})

describe('Settings and Reports surfaces', () => {
    it('paints the English receipt and tax settings', async () => {
        await useLanguage('en')
        const { container } = renderSettings()

        fireEvent.click(await screen.findByRole('button', { name: 'Receipt Settings' }))
        expect(await screen.findByText('Receipt Preview')).toBeInTheDocument()
        expect(screen.getByText('Sample item 1')).toBeInTheDocument()
        expect(screen.getByText('Sample item 2')).toBeInTheDocument()
        expect(screen.getByText('Your Company Name')).toBeInTheDocument()
        expect(screen.getAllByText('Welcome to our store').length).toBeGreaterThan(0)
        expect(screen.getAllByText('Thank you for your purchase').length).toBeGreaterThan(0)

        fireEvent.click(screen.getByRole('button', { name: 'Tax & Currency' }))
        expect(await screen.findByText('Currency')).toBeInTheDocument()
        expect(screen.getByText(/Used only when a product has no tax rate/)).toBeInTheDocument()

        expectNoRawKeys(container, affectedSettingsKeys)
    })

    it('paints the Arabic receipt and tax settings instead of degrading to English', async () => {
        await useLanguage('ar')
        const { container } = renderSettings()

        fireEvent.click(await screen.findByRole('button', { name: 'إعدادات الفاتورة' }))
        expect(await screen.findByText('معاينة الفاتورة')).toBeInTheDocument()
        expect(screen.getByText('صنف تجريبي 1')).toBeInTheDocument()
        expect(screen.getByText('صنف تجريبي 2')).toBeInTheDocument()
        expect(screen.getByText('اسم شركتك')).toBeInTheDocument()
        expect(screen.getAllByText('مرحبًا بكم في متجرنا').length).toBeGreaterThan(0)
        expect(screen.getAllByText('شكرًا لشرائك من متجرنا').length).toBeGreaterThan(0)

        fireEvent.click(screen.getByRole('button', { name: 'الضريبة والعملة' }))
        expect(await screen.findByText('العملة')).toBeInTheDocument()
        expect(screen.getByText(/تُستخدم فقط عندما لا يكون للمنتج نسبة ضريبة/)).toBeInTheDocument()

        expectNoRawKeys(container, affectedSettingsKeys)
        expect(container.textContent).not.toContain('Your Company Name')
        expect(container.textContent).not.toContain('Sample item 1')
    })
})

describe('other affected surfaces', () => {
    it('paints the English live monitoring card', async () => {
        await useLanguage('en')
        const { container } = render(<StorefrontLiveMonitoring />)

        expect(await screen.findByText('Live Store Monitoring')).toBeInTheDocument()
        expect(screen.getByText('Live Visitors')).toBeInTheDocument()
        expect(screen.getByText('Orders in Last 5 Minutes')).toBeInTheDocument()
        expect(screen.getByText('Added to cart')).toBeInTheDocument()
        expectNoRawKeys(container, affectedOtherKeys)
    })

    it('paints the Arabic live monitoring card', async () => {
        await useLanguage('ar')
        const { container } = render(<StorefrontLiveMonitoring />)

        expect(await screen.findByText('المراقبة المباشرة للمتجر')).toBeInTheDocument()
        expect(screen.getByText('الزوار الآن')).toBeInTheDocument()
        expect(screen.getByText('السلال النشطة')).toBeInTheDocument()
        expect(screen.getByText('طلبات آخر 5 دقائق')).toBeInTheDocument()
        expect(screen.getByText('يتم التحديث كل 10 ثوانٍ')).toBeInTheDocument()
        expect(screen.getByText('أُضيف إلى السلة')).toBeInTheDocument()
        expectNoRawKeys(container, affectedOtherKeys)
        expect(screen.queryByText('Live Store Monitoring')).not.toBeInTheDocument()
        expect(storefrontAnalyticsApi.getStorefrontLiveMonitoring).toHaveBeenCalled()
    })
})

describe('Storefront Admin manual payment validation', () => {
    const t = (key: string, fallback?: string) =>
        i18n.t(key, fallback ? { defaultValue: fallback } : undefined) as string

    it('returns the English rejection messages', async () => {
        await useLanguage('en')
        expect(getRejectionReasonValidationMessage('ab', t)).toBe('Rejection reason must be at least 3 characters.')
        expect(getRejectionReasonValidationMessage('x'.repeat(501), t)).toBe('Rejection reason must be 500 characters or fewer.')
        expect(getRejectionReasonValidationMessage('  ', t)).toBe('Rejection reason must be at least 3 characters.')
        expect(getRejectionReasonValidationMessage('valid reason', t)).toBe('')
    })

    it('returns the Arabic rejection messages', async () => {
        await useLanguage('ar')
        expect(getRejectionReasonValidationMessage('ab', t)).toBe('يجب أن يتكون سبب الرفض من 3 أحرف على الأقل.')
        expect(getRejectionReasonValidationMessage('x'.repeat(501), t)).toBe('يجب ألا يتجاوز سبب الرفض 500 حرف.')
        for (const key of affectedStorefrontAdminKeys) {
            expect(t(key), key).not.toBe(key)
        }
    })
})
