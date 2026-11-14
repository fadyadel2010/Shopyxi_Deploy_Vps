import { beforeEach, describe, expect, it, vi } from 'vitest'
import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import { MemoryRouter } from 'react-router-dom'
import EmailVerificationRequired from './EmailVerificationRequired'
import api from '../services/api'

const auth = vi.hoisted(() => ({ hydrateSession: vi.fn(), logout: vi.fn() }))
vi.mock('../store/authStore', () => ({ useAuthStore: { getState: () => auth } }))
vi.mock('../services/posRuntimeCache', () => ({ invalidatePosRuntimeCache: vi.fn() }))
vi.mock('../auth/posVendorSessionAuthority', () => ({ broadcastPosVendorLogout: vi.fn() }))
vi.mock('../services/api', () => ({ default: { get: vi.fn(), post: vi.fn() } }))
vi.mock('react-i18next', () => ({ useTranslation: () => ({ i18n: { resolvedLanguage: 'en' } }) }))

describe('Owner verification screen', () => {
    beforeEach(() => vi.clearAllMocks())
    it('rehydrates only from the authoritative session after verification', async () => {
        const user = { role: 'Owner', emailVerified: true }
        vi.mocked(api.get).mockResolvedValue({ data: { user } })
        render(<MemoryRouter><EmailVerificationRequired /></MemoryRouter>)
        fireEvent.click(screen.getByRole('button', { name: 'I have verified my email' }))
        await waitFor(() => expect(auth.hydrateSession).toHaveBeenCalledWith(user))
        expect(api.get).toHaveBeenCalledWith('/auth/session')
    })
    it('does not grant UI authority when session refresh fails', async () => {
        vi.mocked(api.get).mockRejectedValue(new Error('Unavailable'))
        render(<MemoryRouter><EmailVerificationRequired /></MemoryRouter>)
        fireEvent.click(screen.getByRole('button', { name: 'I have verified my email' }))
        await screen.findByText('Unable to check your session. Please try again.')
        expect(auth.hydrateSession).not.toHaveBeenCalled()
    })
    it('shows resend throttling without bypassing verification', async () => {
        vi.mocked(api.post).mockRejectedValue({ response: { status: 429 } })
        render(<MemoryRouter><EmailVerificationRequired /></MemoryRouter>)
        fireEvent.click(screen.getByRole('button', { name: 'Resend verification email' }))
        await screen.findByText('Please wait before requesting another email.')
        expect(auth.hydrateSession).not.toHaveBeenCalled()
    })
    it('clears local authority only after successful server logout', async () => {
        vi.mocked(api.post).mockResolvedValue({ data: {} })
        render(<MemoryRouter><EmailVerificationRequired /></MemoryRouter>)
        fireEvent.click(screen.getByRole('button', { name: 'Sign out' }))
        await waitFor(() => expect(auth.logout).toHaveBeenCalledTimes(1))
        expect(api.post).toHaveBeenCalledWith('/auth/logout')
    })
})
