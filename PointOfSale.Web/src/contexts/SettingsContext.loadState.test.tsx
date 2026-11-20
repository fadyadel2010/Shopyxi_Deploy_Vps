/**
 * Group 5B — D-4 / D-5 regression protection.
 *
 * SettingsContext exposes explicit settings load provenance. A failed settings read
 * is never presented as a confirmed EGP tenant, and financial formatting never
 * invents an EGP value from an unconfirmed settings state.
 */
import { act, cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { SettingsProvider, useSettings } from './SettingsContext'
import { useFormatting } from '../hooks/useFormatting'

const state = vi.hoisted(() => ({ isAuthenticated: true, hasHydrated: true, user: { tenantId: 'tenant-a' } as { tenantId: string } | null }))
const get = vi.hoisted(() => vi.fn())
vi.mock('../services/api', () => ({ default: { get, put: vi.fn() } }))
vi.mock('../store/authStore', () => ({ useAuthStore: (selector: (value: typeof state) => unknown) => selector(state) }))

const egpSettings = { currencyCode: 'EGP', currencySymbol: 'E£', companyName: 'EGP shop' }
const usdSettings = { currencyCode: 'USD', currencySymbol: '$', companyName: 'USD shop' }

function Probe() {
    const { settings, status, error, provenance, currencyConfirmed, currencyProvenance, formatCurrency, reloadSettings } = useSettings()
    return (
        <>
            <p data-testid="status">{status}</p>
            <p data-testid="error">{error ?? 'none'}</p>
            <p data-testid="provenance">{provenance}</p>
            <p data-testid="currency-confirmed">{String(currencyConfirmed)}</p>
            <p data-testid="currency-provenance">{currencyProvenance}</p>
            <p data-testid="currency">{settings.currencyCode}</p>
            <p data-testid="amount">{formatCurrency(1234.5)}</p>
            <p data-testid="explicit-amount">{formatCurrency(10, 'USD')}</p>
            <button onClick={() => { void reloadSettings() }}>Reload settings</button>
        </>
    )
}

function FormattingProbe() {
    const { formatCurrency, formatSignedCurrency } = useFormatting()
    return (
        <>
            <p data-testid="hook-amount">{formatCurrency(1234.5)}</p>
            <p data-testid="hook-explicit">{formatCurrency(1234.5, 'USD')}</p>
            <p data-testid="hook-signed">{formatSignedCurrency(-20)}</p>
        </>
    )
}

const renderProbe = () => render(<SettingsProvider><Probe /></SettingsProvider>)
const renderFormattingProbe = () => render(<SettingsProvider><FormattingProbe /></SettingsProvider>)
const text = (id: string) => screen.getByTestId(id).textContent

beforeEach(() => {
    vi.resetAllMocks()
    state.isAuthenticated = true
    state.hasHydrated = true
    state.user = { tenantId: 'tenant-a' }
})

afterEach(cleanup)

describe('settings load provenance', () => {
    it('reports ready and a confirmed EGP currency from a successful response', async () => {
        get.mockResolvedValue({ data: egpSettings })
        renderProbe()

        await waitFor(() => expect(text('status')).toBe('ready'))
        expect(text('provenance')).toBe('confirmed')
        expect(text('error')).toBe('none')
        expect(text('currency-confirmed')).toBe('true')
        expect(text('currency')).toBe('EGP')
        expect(text('amount')).toContain('1,234.50')
        expect(text('amount')).toMatch(/EGP|E£/)
    })

    it('reports ready and the tenant currency for a supported non-EGP currency', async () => {
        get.mockResolvedValue({ data: usdSettings })
        renderProbe()

        await waitFor(() => expect(text('status')).toBe('ready'))
        expect(text('currency')).toBe('USD')
        expect(text('amount')).toContain('1,234.50')
        expect(text('amount')).toMatch(/\$|USD/)
        expect(text('amount')).not.toMatch(/EGP|E£/)
    })

    it('never presents the pending defaults as a confirmed tenant currency', async () => {
        get.mockReturnValue(new Promise(() => {}))
        renderProbe()

        expect(text('status')).toBe('loading')
        expect(text('provenance')).toBe('unconfirmed')
        expect(text('currency-confirmed')).toBe('false')
        expect(text('amount')).toBe('—')
    })

    it.each([
        ['401', { response: { status: 401 } }, 'unauthorized'],
        ['403', { response: { status: 403 } }, 'unauthorized'],
        ['500', { response: { status: 500 } }, 'failed'],
        ['network failure', new Error('Network Error'), 'failed'],
    ])('classifies a %s failure without confirming EGP', async (_label, rejection, expected) => {
        get.mockRejectedValue(rejection)
        renderProbe()

        await waitFor(() => expect(text('status')).toBe('error'))
        expect(text('error')).toBe(expected)
        expect(text('provenance')).toBe('unconfirmed')
        expect(text('currency-confirmed')).toBe('false')
        expect(text('amount')).toBe('—')
        expect(text('amount')).not.toMatch(/EGP|E£/)
        // An explicitly supplied currency is still the caller's own evidence.
        expect(text('explicit-amount')).toMatch(/\$|USD/)
    })

    it.each([
        ['null body', { data: null }],
        ['non-object body', { data: 'not-settings' }],
        ['array body', { data: [{ currencyCode: 'EGP' }] }],
    ])('classifies an unusable settings payload (%s) as malformed', async (_label, resolution) => {
        get.mockResolvedValue(resolution)
        renderProbe()

        await waitFor(() => expect(text('status')).toBe('error'))
        expect(text('error')).toBe('malformed')
        expect(text('currency-confirmed')).toBe('false')
        expect(text('amount')).toBe('—')
    })

    it('accepts a successful payload that names no currency but never confirms EGP from it', async () => {
        get.mockResolvedValue({ data: { storefrontUrl: 'https://shop.example.test' } })
        renderProbe()

        await waitFor(() => expect(text('status')).toBe('ready'))
        expect(text('error')).toBe('none')
        expect(text('currency-confirmed')).toBe('false')
        expect(text('amount')).toBe('—')
        expect(text('amount')).not.toMatch(/EGP|E£/)
    })

    it('retains last-known-good settings as stale when a later refresh fails', async () => {
        get.mockResolvedValueOnce({ data: usdSettings })
        renderProbe()
        await waitFor(() => expect(text('status')).toBe('ready'))

        get.mockRejectedValueOnce({ response: { status: 500 } })
        fireEvent.click(screen.getByRole('button', { name: 'Reload settings' }))

        await waitFor(() => expect(text('status')).toBe('error'))
        expect(text('error')).toBe('failed')
        expect(text('provenance')).toBe('stale')
        expect(text('currency')).toBe('USD')
        expect(text('amount')).toMatch(/\$|USD/)
        expect(text('amount')).not.toMatch(/EGP|E£/)
    })

    it('restores ready after a successful retry', async () => {
        get.mockRejectedValueOnce({ response: { status: 500 } })
        renderProbe()
        await waitFor(() => expect(text('status')).toBe('error'))

        get.mockResolvedValue({ data: egpSettings })
        fireEvent.click(screen.getByRole('button', { name: 'Reload settings' }))

        await waitFor(() => expect(text('status')).toBe('ready'))
        expect(text('error')).toBe('none')
        expect(text('provenance')).toBe('confirmed')
        expect(text('currency-confirmed')).toBe('true')
        expect(text('amount')).toMatch(/EGP|E£/)
    })

    it('never leaks one tenant currency into another tenant scope', async () => {
        get.mockResolvedValueOnce({ data: usdSettings })
            .mockResolvedValueOnce({ data: egpSettings })
        const view = renderProbe()
        await waitFor(() => expect(text('currency')).toBe('USD'))

        state.user = { tenantId: 'tenant-b' }
        view.rerender(<SettingsProvider><Probe /></SettingsProvider>)

        await waitFor(() => expect(text('currency')).toBe('EGP'))
        expect(text('status')).toBe('ready')
        expect(text('amount')).not.toMatch(/\$/)
    })

    it('ignores a late response from a previous tenant scope', async () => {
        let resolveFirst: (value: unknown) => void = () => {}
        get.mockImplementationOnce(() => new Promise(resolve => { resolveFirst = resolve }))
            .mockResolvedValueOnce({ data: egpSettings })
        const view = renderProbe()

        state.user = { tenantId: 'tenant-b' }
        view.rerender(<SettingsProvider><Probe /></SettingsProvider>)
        await waitFor(() => expect(text('currency')).toBe('EGP'))

        await act(async () => resolveFirst({ data: usdSettings }))
        expect(text('currency')).toBe('EGP')
        expect(text('amount')).not.toMatch(/\$/)
    })

    it('does not report an unsupported tenant currency as EGP', async () => {
        get.mockResolvedValue({ data: { currencyCode: 'JOD', currencySymbol: 'د.أ' } })
        renderProbe()

        await waitFor(() => expect(text('status')).toBe('ready'))
        expect(text('currency')).toBe('JOD')
        expect(text('amount')).toBe('—')
        expect(text('amount')).not.toMatch(/EGP|E£|\$/)
    })

    it('applies the same currency authority to the shared formatting hook', async () => {
        get.mockResolvedValueOnce({ data: usdSettings })
        const view = renderFormattingProbe()
        await waitFor(() => expect(text('hook-amount')).toMatch(/\$|USD/))
        view.unmount()

        get.mockRejectedValueOnce({ response: { status: 500 } })
        renderFormattingProbe()
        await waitFor(() => expect(text('hook-amount')).toBe('—'))
        expect(text('hook-signed')).toBe('—')
        expect(text('hook-explicit')).toMatch(/\$|USD/)
        expect(text('hook-amount')).not.toMatch(/EGP|E£/)
    })

    it('keeps a settings failure visible without exposing server content', async () => {
        get.mockRejectedValue({
            response: { status: 500, data: { message: 'Connection string to internal host failed' } },
            config: { headers: { Authorization: 'Bearer secret-token' } },
        })
        const { container } = renderProbe()

        await waitFor(() => expect(text('status')).toBe('error'))
        expect(container.textContent).not.toContain('Connection string')
        expect(container.textContent).not.toContain('secret-token')
        expect(text('error')).toBe('failed')
    })
    it('never renders default currency through the hook while settings are pending', () => {
        get.mockReturnValue(new Promise(() => {}))
        renderFormattingProbe()
        expect(text('hook-amount')).toBe('—')
        expect(text('hook-signed')).toBe('—')
    })
    it('does not confirm a currency from a display symbol alone', async () => {
        get.mockResolvedValue({ data: { currencySymbol: '$' } })
        renderProbe()
        await waitFor(() => expect(text('status')).toBe('ready'))
        expect(text('currency-confirmed')).toBe('false')
        expect(text('amount')).toBe('—')
    })
    it.each([401, 403])('removes last-known settings after access is revoked (%s)', async status => {
        get.mockResolvedValueOnce({ data: usdSettings })
        renderProbe()
        await waitFor(() => expect(text('amount')).toMatch(/\$|USD/))
        get.mockRejectedValueOnce({ response: { status } })
        fireEvent.click(screen.getByRole('button', { name: 'Reload settings' }))
        await waitFor(() => expect(text('status')).toBe('error'))
        expect(text('currency-confirmed')).toBe('false')
        expect(text('amount')).toBe('—')
    })
    it('does not promote last-known currency to fresh evidence after a partial settings read', async () => {
        get.mockResolvedValueOnce({ data: usdSettings })
        renderProbe()
        await waitFor(() => expect(text('currency-provenance')).toBe('confirmed'))
        get.mockResolvedValueOnce({ data: { companyName: 'Changed name' } })
        fireEvent.click(screen.getByRole('button', { name: 'Reload settings' }))
        await waitFor(() => expect(text('currency-provenance')).toBe('stale'))
        expect(text('amount')).toMatch(/\$|USD/)
    })
})
