import { act, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter, Route, Routes } from 'react-router-dom'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import Register from './Register'
import i18n from '../i18n/config'
import api from '../services/api'
import { useAuthStore } from '../store/authStore'

vi.mock('../services/api', () => ({ default: { get: vi.fn(), post: vi.fn() } }))
const get = vi.mocked(api.get)
const post = vi.mocked(api.post)
const available = { data: { available: true, storefrontUrl: 'https://example.test/stores/demo-store', suggestions: [] } }
function deferred() {
    let resolve!: (value: unknown) => void
    let reject!: (value: unknown) => void
    const promise = new Promise((res, rej) => { resolve = res; reject = rej })
    return { promise, resolve, reject }
}
const open = () => render(
    <MemoryRouter initialEntries={['/register']}>
        <Routes>
            <Route path="/register" element={<Register />} />
            <Route path="/login" element={<h1>Login destination</h1>} />
            <Route path="/" element={<h1>Dashboard destination</h1>} />
        </Routes>
    </MemoryRouter>,
)
async function fillValid() {
    await screen.findByRole('form', { name: 'Create your store' })
    fireEvent.change(screen.getByLabelText('Business name'), { target: { value: '  Demo Store  ' } })
    fireEvent.change(screen.getByLabelText('Email'), { target: { value: 'Owner@Example.test' } })
    fireEvent.change(screen.getByLabelText('Password'), { target: { value: ' raw-password ' } })
    const address = screen.getByLabelText('Store address')
    fireEvent.change(address, { target: { value: 'Demo-Store' } })
    fireEvent.blur(address)
    await screen.findByText('This address is available')
    await waitFor(() => expect(screen.getByRole('button', { name: 'Create your store' })).toBeEnabled())
}

describe('Register presentation, localization and preserved merchant contract', () => {
    beforeEach(async () => {
        vi.clearAllMocks()
        useAuthStore.setState({ user: null, isAuthenticated: false, hasHydrated: true })
        await i18n.changeLanguage('en')
        get.mockImplementation(async url => {
            if (url === '/platform/registration-policy') return { data: { enabled: true } }
            if (String(url).startsWith('/platform/subdomain/check')) return available
            if (String(url).startsWith('/platform/email/check')) return { data: { isRegistered: false } }
            return { data: { user: null } }
        })
        post.mockRejectedValue({ response: { status: 500, data: {} } })
    })
    afterEach(async () => {
        useAuthStore.setState({ user: null, isAuthenticated: false, hasHydrated: true })
        await i18n.changeLanguage('en')
    })

    it('switches the pending and closed admission state without exposing a form', async () => {
        const policy = deferred()
        get.mockReturnValue(policy.promise as ReturnType<typeof api.get>)
        const user = userEvent.setup()
        const { container } = open()
        expect(screen.getByRole('status')).toHaveTextContent('Checking store registration')
        await user.click(screen.getByRole('button', { name: 'AR — Switch to Arabic' }))
        expect(document.documentElement).toHaveAttribute('dir', 'rtl')
        expect(screen.getByRole('status')).toHaveTextContent('جارٍ التحقق من إتاحة إنشاء المتاجر')
        await act(async () => policy.resolve({ data: { enabled: false } }))
        expect(screen.getByRole('heading', { level: 1 })).toHaveTextContent('إنشاء المتاجر غير متاح مؤقتًا')
        expect(container.querySelector('form')).toBeNull()
        expect(post).not.toHaveBeenCalled()
        await user.click(screen.getByRole('link', { name: 'تسجيل الدخول' }))
        expect(screen.getByText('Login destination')).toBeInTheDocument()
    })

    it('aborts the existing policy read on unmount and restores the page title', async () => {
        document.title = 'Previous page'
        const policy = deferred()
        get.mockReturnValue(policy.promise as ReturnType<typeof api.get>)
        const view = open()
        expect(document.title).toBe('Create your store | SHOPYXI')
        const signal = get.mock.calls[0][1]?.signal
        view.unmount()
        expect(signal?.aborted).toBe(true)
        expect(document.title).toBe('Previous page')
        await act(async () => policy.resolve({ data: { enabled: true } }))
        expect(post).not.toHaveBeenCalled()
    })

    it('keeps the four existing fields, password-manager semantics and stable visibility toggle', async () => {
        const user = userEvent.setup()
        open()
        await screen.findByRole('form')
        const password = screen.getByLabelText('Password')
        expect(screen.getByLabelText('Business name')).toHaveFocus()
        expect(screen.getByLabelText('Business name')).toHaveAttribute('autocomplete', 'organization')
        expect(screen.getByLabelText('Email')).toHaveAttribute('autocomplete', 'email')
        expect(password).toHaveAttribute('autocomplete', 'new-password')
        expect(password).toHaveAttribute('dir', 'ltr')
        expect(screen.getByLabelText('Store address')).toHaveAttribute('maxlength', '63')
        await user.type(password, ' raw-password ')
        const toggle = screen.getByRole('button', { name: 'Show password' })
        await user.click(toggle)
        expect(toggle).toHaveAttribute('aria-pressed', 'true')
        expect(toggle).toHaveAttribute('aria-controls', 'password')
        expect(password).toHaveAttribute('type', 'text')
        expect(password).toHaveValue(' raw-password ')
        await user.click(toggle)
        expect(password).toHaveAttribute('type', 'password')
        expect(screen.getByRole('button', { name: 'Create your store' })).toBeDisabled()
    })

    it('keeps inline errors, focuses a linked summary after invalid submit, and retranslates local errors', async () => {
        const user = userEvent.setup()
        open()
        const form = await screen.findByRole('form')
        fireEvent.submit(form)
        const summary = screen.getByRole('alert', { name: 'Please review your details' })
        await waitFor(() => expect(summary).toHaveFocus())
        expect(within(summary).getAllByRole('link')).toHaveLength(4)
        expect(document.getElementById('email-error')).toHaveTextContent('Please enter your email')
        expect(screen.getByLabelText('Email')).toHaveAttribute('aria-describedby', expect.stringContaining('email-error'))
        await user.click(within(summary).getByRole('link', { name: 'Email: Please enter your email' }))
        expect(screen.getByLabelText('Email')).toHaveFocus()
        await user.click(screen.getByRole('button', { name: 'AR — Switch to Arabic' }))
        expect(summary).toHaveTextContent('يرجى مراجعة بياناتك')
        expect(document.getElementById('email-error')).toHaveTextContent('يرجى إدخال البريد الإلكتروني')
        expect(post).not.toHaveBeenCalled()
    })

    it('shows server field errors even for fields that were not blurred and retains entered values', async () => {
        post.mockRejectedValue({
            response: { status: 400, data: { fieldErrors: { email: 'Use a different email address.' } } },
        })
        open()
        await fillValid()
        fireEvent.submit(screen.getByRole('form'))
        const summary = await screen.findByRole('alert', { name: 'Please review your details' })
        await waitFor(() => expect(summary).toHaveFocus())
        expect(document.getElementById('email-error')).toHaveTextContent('Use a different email address.')
        expect(screen.getByLabelText('Email')).toHaveAttribute('aria-invalid', 'true')
        expect(screen.getByLabelText('Password')).toHaveValue(' raw-password ')
    })

    it('preserves payload/idempotency, blocks same-turn duplicate submission and announces loading', async () => {
        const pending = deferred()
        post.mockReturnValue(pending.promise as ReturnType<typeof api.post>)
        open()
        await fillValid()
        act(() => {
            fireEvent.submit(screen.getByRole('form'))
            fireEvent.submit(screen.getByRole('form'))
        })
        expect(post).toHaveBeenCalledTimes(1)
        expect(post).toHaveBeenCalledWith('/platform/register', {
            businessName: 'Demo Store', email: 'owner@example.test',
            password: ' raw-password ', subdomain: 'demo-store',
        }, { headers: { 'X-Request-Id': expect.stringMatching(/^reg-/) } })
        expect(screen.getByRole('form')).toHaveAttribute('aria-busy', 'true')
        expect(screen.getByRole('button', { name: 'Show password' })).toBeDisabled()
        expect(screen.getAllByRole('status').some(node => node.textContent === 'Creating your store...')).toBe(true)
        await act(async () => pending.reject({ response: { status: 500, data: {} } }))
        await waitFor(() => expect(screen.getByRole('alert', { name: 'Please review your details' })).toHaveFocus())
        const requestId = post.mock.calls[0][2]?.headers?.['X-Request-Id']
        fireEvent.submit(screen.getByRole('form'))
        await waitFor(() => expect(post).toHaveBeenCalledTimes(2))
        expect(post.mock.calls[1][2]?.headers?.['X-Request-Id']).toBe(requestId)
    })

    it.each([
        { status: 403, data: {} },
        { status: 503, data: { error: 'REGISTRATION_DISABLED' } },
    ])('preserves server refusal and focuses the closed-admission heading: $status', async response => {
        post.mockRejectedValue({ response })
        open()
        await fillValid()
        fireEvent.submit(screen.getByRole('form'))
        const heading = await screen.findByRole('heading', { name: 'New store creation is temporarily unavailable' })
        expect(heading).toHaveFocus()
        expect(screen.queryByRole('form')).not.toBeInTheDocument()
        expect(post).toHaveBeenCalledTimes(1)
        expect(get.mock.calls.filter(([url]) => url === '/auth/session')).toHaveLength(0)
    })

    it('keeps address suggestions keyboard-operable and uses the server-provided URL', async () => {
        get.mockImplementation(async url => {
            if (url === '/platform/registration-policy') return { data: { enabled: true } }
            if (String(url).includes('slug=demo-store-2')) return available
            return { data: { available: false, suggestions: ['demo-store-2'] } }
        })
        const user = userEvent.setup()
        open()
        await screen.findByRole('form')
        fireEvent.change(screen.getByLabelText('Store address'), { target: { value: 'demo-store' } })
        fireEvent.blur(screen.getByLabelText('Store address'))
        const suggestion = await screen.findByRole('button', { name: 'demo-store-2' })
        suggestion.focus()
        await user.keyboard('{Enter}')
        expect(screen.getByLabelText('Store address')).toHaveFocus()
        expect(screen.getByLabelText('Store address')).toHaveValue('demo-store-2')
        await screen.findByText(available.data.storefrontUrl)
    })

    it('keeps the existing navigation to the dashboard if the post-registration session read fails', async () => {
        post.mockResolvedValue({ data: {} })
        open()
        await fillValid()
        fireEvent.submit(screen.getByRole('form'))
        expect(await screen.findByText('Dashboard destination')).toBeInTheDocument()
        expect(get.mock.calls.filter(([url]) => url === '/auth/session')).toHaveLength(1)
        expect(useAuthStore.getState().isAuthenticated).toBe(false)
    })
})
