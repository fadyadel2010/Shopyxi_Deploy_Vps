import { beforeEach, describe, expect, it, vi } from 'vitest'
import { render, screen, waitFor } from '@testing-library/react'
import { MemoryRouter } from 'react-router-dom'
import Register from './Register'
import api from '../services/api'

vi.mock('../services/api', () => ({ default: { get: vi.fn(), post: vi.fn() } }))
vi.mock('react-i18next', () => ({ useTranslation: () => ({ t: (key: string) => key, i18n: { resolvedLanguage: 'en' } }) }))

describe('public merchant admission UX', () => {
    beforeEach(() => vi.clearAllMocks())
    it.each([false, null, 'true'])('does not mount a registration form for a non-authoritative open policy: %s', async enabled => {
        vi.mocked(api.get).mockResolvedValue({ data: { enabled } })
        const { container } = render(<MemoryRouter><Register /></MemoryRouter>)
        await screen.findByText('New store creation is temporarily unavailable')
        expect(container.querySelector('form')).toBeNull()
        expect(container.querySelector('button[type=submit]')).toBeNull()
        expect(screen.getByRole('link')).toHaveAttribute('href', '/login')
        expect(api.post).not.toHaveBeenCalled()
    })
    it('fails safely when admission policy cannot be loaded', async () => {
        vi.mocked(api.get).mockRejectedValue(new Error('Unavailable'))
        const { container } = render(<MemoryRouter><Register /></MemoryRouter>)
        await screen.findByText('New store creation is temporarily unavailable')
        expect(container.querySelector('form')).toBeNull()
        expect(api.post).not.toHaveBeenCalled()
    })
    it('does not mount a form while the policy is loading', async () => {
        vi.mocked(api.get).mockReturnValue(new Promise(() => {}))
        const { container } = render(<MemoryRouter><Register /></MemoryRouter>)
        await waitFor(() => expect(api.get).toHaveBeenCalledTimes(1))
        expect(container.querySelector('form')).toBeNull()
        expect(api.post).not.toHaveBeenCalled()
    })
    it('mounts the normal form only for an explicit open server policy', async () => {
        vi.mocked(api.get).mockResolvedValue({ data: { enabled: true } })
        const { container } = render(<MemoryRouter><Register /></MemoryRouter>)
        await waitFor(() => expect(container.querySelector('form')).not.toBeNull())
        expect(api.post).not.toHaveBeenCalled()
    })
})
