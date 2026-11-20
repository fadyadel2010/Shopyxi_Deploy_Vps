import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { act, fireEvent, render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter, Route, Routes, useLocation } from 'react-router-dom'
import Login from './Login'
import { useAuthStore } from '../store/authStore'

const TENANT_ID = '11111111-1111-4111-8111-111111111111'
const USER_ID = '22222222-2222-4222-8222-222222222222'

const makeSessionUser = (overrides: Record<string, unknown> = {}) => ({
    id: USER_ID,
    tenantId: TENANT_ID,
    role: 'Owner',
    effectivePermissions: ['dashboard.view', 'sales.operate'],
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
    ...overrides,
})

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
vi.mock('../components/LanguageSwitcher', () => ({
    default: () => null,
}))
vi.mock('react-i18next', () => ({
    useTranslation: () => ({ t: (key: string) => key }),
}))

const LocationProbe = () => {
    const location = useLocation()
    return <div>{`Sales destination ${location.pathname}${location.search}${location.hash}`}</div>
}

const renderLogin = (entry: string | { pathname: string; state?: unknown } = '/login') => render(
    <MemoryRouter initialEntries={[entry]}>
        <Routes>
            <Route path="/login" element={<Login />} />
            <Route path="/sales" element={<LocationProbe />} />
            <Route path="/" element={<div>Dashboard destination</div>} />
        </Routes>
    </MemoryRouter>,
)

const fillCredentials = async (user: ReturnType<typeof userEvent.setup>, username = 'admin', password = 'secret') => {
    await user.type(screen.getByLabelText('login.usernameOrEmail'), username)
    await user.type(screen.getByLabelText('login.password'), password)
}

const resetStore = () => {
    useAuthStore.setState({
        user: null,
        isAuthenticated: false,
        hasHydrated: true,
        isRouteAuthorityValidating: false,
    })
}

describe('Login authentication workflow', () => {
    beforeEach(() => {
        vi.spyOn(console, 'warn').mockImplementation(() => {})
        window.localStorage.clear()
        resetStore()
        apiPostMock.mockReset().mockResolvedValue({ data: {} })
        apiGetMock.mockReset().mockResolvedValue({ data: { user: makeSessionUser() } })
        reloadSettingsMock.mockReset().mockResolvedValue(undefined)
    })

    afterEach(() => {
        vi.restoreAllMocks()
        window.localStorage.clear()
        resetStore()
    })

    it('submits the trimmed username, re-reads the session, stores authority, reloads settings, then redirects', async () => {
        const user = userEvent.setup()
        renderLogin()

        await user.type(screen.getByLabelText('login.usernameOrEmail'), '  admin@example.com  ')
        await user.type(screen.getByLabelText('login.password'), ' secret ')
        await user.click(screen.getByRole('button', { name: 'login.signIn' }))

        expect(await screen.findByText('Dashboard destination')).toBeInTheDocument()
        expect(apiPostMock).toHaveBeenCalledTimes(1)
        expect(apiPostMock).toHaveBeenCalledWith('/auth/login', {
            username: 'admin@example.com',
            password: ' secret ',
        })
        expect(apiGetMock).toHaveBeenCalledTimes(1)
        expect(apiGetMock).toHaveBeenCalledWith('/auth/session')
        expect(reloadSettingsMock).toHaveBeenCalledTimes(1)
        expect(apiPostMock.mock.invocationCallOrder[0]).toBeLessThan(apiGetMock.mock.invocationCallOrder[0])
        expect(apiGetMock.mock.invocationCallOrder[0]).toBeLessThan(reloadSettingsMock.mock.invocationCallOrder[0])
        expect(useAuthStore.getState().isAuthenticated).toBe(true)
        expect(useAuthStore.getState().user?.id).toBe(USER_ID)
        expect(window.localStorage.getItem('accessToken')).toBeNull()
        expect(window.localStorage.getItem('refreshToken')).toBeNull()
        expect(window.localStorage.getItem('auth-storage')).toBeNull()
        expect(window.localStorage.getItem('tenantId')).toBeNull()
    })

    it('blocks whitespace-only credentials locally without calling the API', async () => {
        const user = userEvent.setup()
        renderLogin()

        await fillCredentials(user, '   ', 'secret')
        await user.click(screen.getByRole('button', { name: 'login.signIn' }))

        const alert = await screen.findByRole('alert')
        expect(alert).toHaveTextContent('login.enterUsername')
        await waitFor(() => expect(alert).toHaveFocus())
        expect(screen.getByLabelText('login.usernameOrEmail')).toHaveAttribute('aria-invalid', 'true')
        expect(screen.getByLabelText('login.password')).not.toHaveAttribute('aria-invalid')
        expect(screen.getByLabelText('login.password')).not.toHaveAttribute('aria-describedby')
        await user.tab()
        expect(screen.getByLabelText('login.usernameOrEmail')).toHaveFocus()
        await user.type(screen.getByLabelText('login.usernameOrEmail'), 'admin')
        expect(screen.queryByRole('alert')).not.toBeInTheDocument()
        expect(apiPostMock).not.toHaveBeenCalled()
        expect(apiGetMock).not.toHaveBeenCalled()
    })

    it('preserves a known backend failure as localized feedback, keeping values without retrying', async () => {
        apiPostMock.mockRejectedValue({
            response: { status: 401, data: { message: 'Invalid username or password' } },
            message: 'Request failed',
        })
        const user = userEvent.setup()
        renderLogin()

        await fillCredentials(user)
        await user.click(screen.getByRole('button', { name: 'login.signIn' }))

        const alert = await screen.findByRole('alert')
        expect(alert).toHaveTextContent('login.invalidCredentials')
        expect(alert).toHaveAttribute('dir', 'auto')
        await waitFor(() => expect(alert).toHaveFocus())
        expect(apiPostMock).toHaveBeenCalledTimes(1)
        expect(apiGetMock).not.toHaveBeenCalled()
        expect(screen.getByLabelText('login.usernameOrEmail')).toHaveValue('admin')
        expect(screen.getByLabelText('login.password')).toHaveValue('secret')
        expect(screen.getByLabelText('login.usernameOrEmail')).toHaveAttribute('aria-describedby', 'pos-login-error')
        expect(screen.getByRole('button', { name: 'login.signIn' })).not.toBeDisabled()
    })

    it('uses safe localized connection and fallback text, not response or exception details', async () => {
        const user = userEvent.setup()
        apiPostMock.mockRejectedValue({ code: 'ERR_NETWORK', message: 'internal transport detail' })
        renderLogin()

        await fillCredentials(user)
        await user.click(screen.getByRole('button', { name: 'login.signIn' }))
        expect(await screen.findByRole('alert')).toHaveTextContent('login.connectionError')

        apiPostMock.mockReset().mockRejectedValue({
            response: { status: 500, data: { message: { private: 'synthetic internal detail' } } },
            message: 'synthetic request detail',
        })
        await user.click(screen.getByRole('button', { name: 'login.signIn' }))
        await waitFor(() => expect(screen.getByRole('alert')).toHaveTextContent('login.genericError'))
        expect(screen.getByRole('alert')).toHaveFocus()
    })

    it.each([
        [401, { message: 'Account is disabled' }, 'login.accountDisabled'],
        [401, { code: 'SESSION_ROTATED' }, 'login.sessionChanged'],
        [429, {}, 'login.tooManyAttempts'],
    ])('handles public sign-in response %s without retrying or exposing the body', async (status, data, key) => {
        apiPostMock.mockRejectedValue({ response: { status, data } })
        const user = userEvent.setup()
        renderLogin()
        await fillCredentials(user)
        await user.click(screen.getByRole('button', { name: 'login.signIn' }))
        expect(await screen.findByRole('alert')).toHaveTextContent(key)
        expect(apiPostMock).toHaveBeenCalledTimes(1)
        expect(apiGetMock).not.toHaveBeenCalled()
    })

    it('does not let the store-triggered redirect overtake the non-fatal settings step', async () => {
        let finishSettings: () => void = () => {}
        reloadSettingsMock.mockImplementation(() => new Promise<void>((resolve) => {
            finishSettings = resolve
        }))
        const user = userEvent.setup()
        renderLogin()
        await fillCredentials(user)
        await user.click(screen.getByRole('button', { name: 'login.signIn' }))
        await waitFor(() => expect(reloadSettingsMock).toHaveBeenCalledTimes(1))
        expect(useAuthStore.getState().isAuthenticated).toBe(true)
        expect(screen.queryByText('Dashboard destination')).not.toBeInTheDocument()
        expect(screen.getByRole('status')).toHaveTextContent('login.signingIn')
        expect(screen.getByRole('form', { name: 'login.signIn' })).not.toContainElement(screen.getByRole('status'))
        finishSettings()
        expect(await screen.findByText('Dashboard destination')).toBeInTheDocument()
    })

    it('continues to the redirect when the non-fatal settings reload fails', async () => {
        reloadSettingsMock.mockRejectedValue(new Error('settings unavailable'))
        const user = userEvent.setup()
        renderLogin()

        await fillCredentials(user)
        await user.click(screen.getByRole('button', { name: 'login.signIn' }))

        expect(await screen.findByText('Dashboard destination')).toBeInTheDocument()
        expect(useAuthStore.getState().isAuthenticated).toBe(true)
    })

    it('fails closed when the session user lacks POS vendor authority', async () => {
        apiGetMock.mockResolvedValue({
            data: { user: { id: USER_ID, tenantId: TENANT_ID, role: 'Owner' } },
        })
        const user = userEvent.setup()
        renderLogin()

        await fillCredentials(user)
        await user.click(screen.getByRole('button', { name: 'login.signIn' }))

        const alert = await screen.findByRole('alert')
        expect(alert).toHaveTextContent('login.genericError')
        expect(useAuthStore.getState().isAuthenticated).toBe(false)
        expect(reloadSettingsMock).not.toHaveBeenCalled()
        expect(screen.queryByText('Dashboard destination')).not.toBeInTheDocument()
        expect(apiPostMock).toHaveBeenCalledTimes(1)
        expect(apiGetMock).toHaveBeenCalledTimes(1)
    })

    it('shows the exact loading state and prevents a double submit', async () => {
        let resolveLogin: (value: unknown) => void = () => {}
        apiPostMock.mockImplementation(() => new Promise((resolve) => {
            resolveLogin = resolve
        }))
        const user = userEvent.setup()
        renderLogin()

        await fillCredentials(user)
        const form = screen.getByRole('form', { name: 'login.signIn' })
        act(() => {
            fireEvent.submit(form)
            fireEvent.submit(form)
        })
        expect(apiPostMock).toHaveBeenCalledTimes(1)

        const busyButton = await screen.findByRole('button', { name: 'login.signingIn' })
        expect(busyButton).toBeDisabled()
        expect(busyButton).toHaveAttribute('aria-busy', 'true')
        expect(busyButton.closest('form')).toHaveAttribute('aria-busy', 'true')
        expect(screen.getByLabelText('login.usernameOrEmail')).toBeDisabled()
        expect(screen.getByLabelText('login.password')).toBeDisabled()

        await user.click(busyButton)
        expect(apiPostMock).toHaveBeenCalledTimes(1)

        resolveLogin({ data: {} })
        expect(await screen.findByText('Dashboard destination')).toBeInTheDocument()
    })

    it('toggles password visibility without changing the value or submission payload', async () => {
        const user = userEvent.setup()
        renderLogin()

        const password = screen.getByLabelText('login.password')
        await user.type(screen.getByLabelText('login.usernameOrEmail'), 'admin')
        await user.type(password, 'p@ss-123')

        expect(password).toHaveAttribute('type', 'password')
        const toggle = screen.getByRole('button', { name: 'login.showPassword' })
        expect(toggle).toHaveAttribute('aria-pressed', 'false')
        expect(toggle).toHaveAttribute('aria-controls', 'pos-login-password')

        await user.click(toggle)

        expect(toggle).toHaveAccessibleName('login.showPassword')
        expect(toggle).toHaveAttribute('aria-pressed', 'true')
        expect(screen.getByLabelText('login.password')).toHaveAttribute('type', 'text')
        expect(screen.getByLabelText('login.password')).toHaveValue('p@ss-123')

        await user.click(screen.getByRole('button', { name: 'login.signIn' }))
        expect(await screen.findByText('Dashboard destination')).toBeInTheDocument()
        expect(apiPostMock).toHaveBeenCalledWith('/auth/login', {
            username: 'admin',
            password: 'p@ss-123',
        })
        expect(screen.queryByRole('checkbox')).not.toBeInTheDocument()
    })

    it('preserves the frozen credential field semantics', () => {
        renderLogin()

        const username = screen.getByLabelText('login.usernameOrEmail')
        expect(username).toHaveAttribute('id', 'pos-login-username')
        expect(username).toBeRequired()
        expect(username).toHaveAttribute('autocomplete', 'username')
        expect(username).toHaveAttribute('dir', 'auto')
        expect(username).toHaveAttribute('autocapitalize', 'none')
        expect(username).toHaveAttribute('spellcheck', 'false')
        expect(username).toHaveFocus()

        const password = screen.getByLabelText('login.password')
        expect(password).toHaveAttribute('id', 'pos-login-password')
        expect(password).toBeRequired()
        expect(password).toHaveAttribute('autocomplete', 'current-password')
        expect(password).toHaveAttribute('dir', 'ltr')
        expect(password).toHaveAttribute('type', 'password')
    })

    it('redirects to the sanitized previous location after authentication', async () => {
        const user = userEvent.setup()
        renderLogin({
            pathname: '/login',
            state: { from: { pathname: '/sales', search: '?tab=1', hash: '#top' } },
        })

        await fillCredentials(user)
        await user.click(screen.getByRole('button', { name: 'login.signIn' }))

        expect(await screen.findByText('Sales destination /sales?tab=1#top')).toBeInTheDocument()
    })

    it('rejects an off-site previous location and falls back to the product root', async () => {
        const user = userEvent.setup()
        renderLogin({
            pathname: '/login',
            state: { from: { pathname: '//evil.example.com/steal', search: '?x=1' } },
        })

        await fillCredentials(user)
        await user.click(screen.getByRole('button', { name: 'login.signIn' }))

        expect(await screen.findByText('Dashboard destination')).toBeInTheDocument()
        expect(screen.queryByText(/evil\.example\.com/)).not.toBeInTheDocument()
    })
})
