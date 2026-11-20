import { afterEach, beforeAll, beforeEach, describe, expect, it, vi } from 'vitest'
import { render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter, Route, Routes } from 'react-router-dom'
import i18n from '../i18n/config'
import Login from './Login'
import { useAuthStore } from '../store/authStore'

const TENANT_ID = '11111111-1111-4111-8111-111111111111'
const USER_ID = '22222222-2222-4222-8222-222222222222'

const sessionUser = {
    id: USER_ID,
    tenantId: TENANT_ID,
    role: 'Owner',
    effectivePermissions: ['dashboard.view'],
    navigationCapabilities: ['commercehub.orders.view'],
    vendorLifecycleAuthority: {
        vendorId: TENANT_ID,
        status: 'Active',
        debtStatus: 'Clear',
        tenantActive: true,
        marketplaceApproved: true,
        kycVerified: true,
        isDebtBlocked: false,
        blocksCatalog: false,
        blocksOrders: false,
        blocksStorefront: false,
        blocksAnalytics: false,
        blocksPayments: false,
        blocksPayouts: false,
        blocksLogin: false,
    },
}

const { apiGetMock, apiPostMock, reloadSettingsMock } = vi.hoisted(() => ({
    apiGetMock: vi.fn(),
    apiPostMock: vi.fn(),
    reloadSettingsMock: vi.fn(),
}))

vi.mock('../contexts/SettingsContext', () => ({
    useSettings: () => ({ reloadSettings: reloadSettingsMock }),
}))
vi.mock('../services/api', () => ({
    default: { get: apiGetMock, post: apiPostMock },
}))

const renderLogin = () => render(
    <MemoryRouter initialEntries={['/login?switchAccount=1']}>
        <Routes>
            <Route path="/login" element={<Login />} />
            <Route path="/" element={<div>Dashboard destination</div>} />
        </Routes>
    </MemoryRouter>,
)

const switchToArabic = async (user: ReturnType<typeof userEvent.setup>) => {
    await user.click(screen.getByRole('button', { name: 'AR — Switch to Arabic' }))
    await waitFor(() => expect(document.documentElement).toHaveAttribute('dir', 'rtl'))
    expect(document.documentElement).toHaveAttribute('lang', 'ar')
}

describe('Login localization and document direction', () => {
    beforeAll(async () => {
        await i18n.changeLanguage('en')
    })

    beforeEach(async () => {
        vi.spyOn(console, 'error').mockImplementation(() => {})
        vi.spyOn(console, 'warn').mockImplementation(() => {})
        window.localStorage.clear()
        useAuthStore.setState({ user: null, isAuthenticated: false, hasHydrated: true })
        apiPostMock.mockReset().mockResolvedValue({ data: {} })
        apiGetMock.mockReset().mockResolvedValue({ data: { user: sessionUser } })
        reloadSettingsMock.mockReset().mockResolvedValue(undefined)
        await i18n.changeLanguage('en')
    })

    afterEach(async () => {
        vi.restoreAllMocks()
        window.localStorage.clear()
        useAuthStore.setState({ user: null, isAuthenticated: false, hasHydrated: true })
        await i18n.changeLanguage('en')
    })

    it('renders the approved English copy under document LTR authority', () => {
        renderLogin()

        expect(document.documentElement).toHaveAttribute('lang', 'en')
        expect(document.documentElement).toHaveAttribute('dir', 'ltr')
        expect(screen.getByRole('complementary', { name: 'SHOPYXI' })).toBeInTheDocument()
        expect(screen.getByRole('heading', { level: 1, name: 'Welcome back' })).toBeInTheDocument()
        expect(screen.getByText('Sign in to your workspace')).toBeInTheDocument()
        expect(screen.getByLabelText('Username or email')).toBeInTheDocument()
        expect(screen.getByLabelText('Password')).toBeInTheDocument()
        expect(screen.getByRole('button', { name: 'Sign in' })).toBeInTheDocument()
        expect(screen.getByText('Your store. One workspace.')).toBeInTheDocument()
        expect(screen.getByText('Manage sales, inventory and reporting from one place.')).toBeInTheDocument()
        expect(screen.getByText('Unified sales')).toBeInTheDocument()
        expect(screen.getByText('Inventory control')).toBeInTheDocument()
        expect(screen.getByText('Operational reporting')).toBeInTheDocument()
        expect(screen.getByText('Secure connection')).toBeInTheDocument()
        expect(screen.getByRole('link', { name: 'Create your store' })).toHaveAttribute('href', '/register')
        expect(screen.getByText('SHOPYXI').tagName).toBe('BDI')
        expect(screen.getByText('POS').tagName).toBe('BDI')

        const languageButton = screen.getByRole('button', { name: 'AR — Switch to Arabic' })
        expect(within(languageButton).getByText('AR').tagName).toBe('BDI')
        expect(languageButton.compareDocumentPosition(screen.getByRole('heading', { level: 1 })) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy()
        expect(screen.getByRole('complementary').compareDocumentPosition(document.querySelector('form')!) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy()
    })

    it('switches to Arabic, mirrors the document, and localizes every new string', async () => {
        const user = userEvent.setup()
        renderLogin()

        await switchToArabic(user)

        expect(screen.getByRole('heading', { level: 1, name: 'مرحبًا بعودتك' })).toBeInTheDocument()
        expect(screen.getByRole('form', { name: 'تسجيل الدخول' })).toBeInTheDocument()
        expect(document.title).toBe('تسجيل الدخول | SHOPYXI')
        expect(screen.getByText('سجّل الدخول إلى مساحة عملك.')).toBeInTheDocument()
        expect(screen.getByLabelText('اسم المستخدم أو البريد الإلكتروني')).toBeInTheDocument()
        expect(screen.getByLabelText('كلمة المرور')).toBeInTheDocument()
        expect(screen.getByRole('button', { name: 'تسجيل الدخول' })).toBeInTheDocument()
        expect(screen.getByText('متجرك، في مساحة عمل واحدة.')).toBeInTheDocument()
        expect(screen.getByText('أدر المبيعات والمخزون والتقارير من مكان واحد.')).toBeInTheDocument()
        expect(screen.getByText('مبيعات موحدة')).toBeInTheDocument()
        expect(screen.getByText('إدارة المخزون')).toBeInTheDocument()
        expect(screen.getByText('تقارير تشغيلية')).toBeInTheDocument()
        expect(screen.getByText('اتصال آمن')).toBeInTheDocument()
        expect(screen.getByText('ليس لديك حساب؟')).toBeInTheDocument()
        expect(screen.getByRole('link', { name: 'أنشئ متجرك' })).toHaveAttribute('href', '/register')

        const languageButton = screen.getByRole('button', { name: 'EN — التبديل إلى الإنجليزية' })
        expect(within(languageButton).getByText('EN').tagName).toBe('BDI')
    })

    it('keeps credentials bidi-safe and isolates Latin brand strings', async () => {
        const user = userEvent.setup()
        renderLogin()
        await switchToArabic(user)

        expect(screen.getByLabelText('اسم المستخدم أو البريد الإلكتروني')).toHaveAttribute('dir', 'auto')
        expect(screen.getByLabelText('كلمة المرور')).toHaveAttribute('dir', 'ltr')
        expect(screen.getByText('SHOPYXI')).toHaveAttribute('dir', 'ltr')
        expect(screen.getByText('POS')).toHaveAttribute('dir', 'ltr')

        const secureNote = screen.getByText('اتصال آمن').closest('p')!
        expect(secureNote.firstElementChild?.tagName.toLowerCase()).toBe('svg')
        expect(secureNote).toHaveTextContent('اتصال آمن')
    })

    it('shows the exact localized loading state in Arabic', async () => {
        expect(i18n.getFixedT('en')('login.signingIn')).toBe('Signing in...')
        expect(i18n.getFixedT('ar')('login.signingIn')).toBe('جارٍ تسجيل الدخول…')

        let resolveLogin: (value: unknown) => void = () => {}
        apiPostMock.mockImplementation(() => new Promise((resolve) => {
            resolveLogin = resolve
        }))
        const user = userEvent.setup()
        renderLogin()
        await switchToArabic(user)

        await user.type(screen.getByLabelText('اسم المستخدم أو البريد الإلكتروني'), 'admin')
        await user.type(screen.getByLabelText('كلمة المرور'), 'secret')
        await user.click(screen.getByRole('button', { name: 'تسجيل الدخول' }))

        const busyButton = await screen.findByRole('button', { name: 'جارٍ تسجيل الدخول…' })
        expect(busyButton).toBeDisabled()
        expect(busyButton).toHaveAttribute('aria-busy', 'true')
        expect(screen.getByRole('status')).toHaveTextContent('جارٍ تسجيل الدخول…')

        resolveLogin({ data: {} })
        expect(await screen.findByText('Dashboard destination')).toBeInTheDocument()
    })

    it('localizes an in-flight failure in the current language and retranslates a displayed error', async () => {
        let rejectLogin: (reason: unknown) => void = () => {}
        apiPostMock.mockImplementation(() => new Promise((_, reject) => {
            rejectLogin = reject
        }))
        const user = userEvent.setup()
        renderLogin()

        await user.type(screen.getByLabelText('Username or email'), 'admin')
        await user.type(screen.getByLabelText('Password'), 'secret')
        await user.click(screen.getByRole('button', { name: 'Sign in' }))
        expect(screen.getByRole('status')).toHaveTextContent('Signing in...')
        await switchToArabic(user)
        expect(screen.getByRole('status')).toHaveTextContent('جارٍ تسجيل الدخول…')

        rejectLogin({ response: { status: 401, data: { message: 'Invalid username or password' } } })

        const alert = await screen.findByRole('alert')
        expect(alert).toHaveTextContent('اسم المستخدم أو كلمة المرور غير صحيحة.')
        expect(alert).toHaveAttribute('dir', 'auto')
        await waitFor(() => expect(alert).toHaveFocus())
        expect(document.documentElement).toHaveAttribute('dir', 'rtl')
        const switcher = screen.getByRole('button', { name: 'EN — التبديل إلى الإنجليزية' })
        await user.click(switcher)
        expect(alert).toHaveTextContent('Invalid username or password.')
        expect(switcher).toHaveFocus()
    })
})
