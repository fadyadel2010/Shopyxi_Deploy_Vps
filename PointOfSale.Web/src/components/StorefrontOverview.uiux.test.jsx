import { act } from 'react'
import { cleanup, render, screen } from '@testing-library/react'
import { afterEach, expect, it, vi } from 'vitest'
import { MemoryRouter } from 'react-router-dom'
import StorefrontDashboard from '../pages/StorefrontDashboard'
import i18n from '../i18n/config'
import { useAuthStore } from '../store/authStore'

const mounts = vi.hoisted(() => ({ status: vi.fn(), wishlist: vi.fn() }))
vi.mock('./StorefrontStatusCard', () => ({ default: function Status() {
    const [initial] = React.useState(() => { mounts.status(); return 'status fixture' })
    return <div>{initial}</div>
} }))
vi.mock('./WishlistAnalyticsCard', () => ({ default: function Wishlist() {
    const [initial] = React.useState(() => { mounts.wishlist(); return 'wishlist fixture' })
    return <div>{initial}</div>
} }))
import React from 'react'
afterEach(() => { cleanup(); vi.clearAllMocks() })
it('uses normalized regional Arabic and remounts overview sources when actor scope changes', async () => {
    await act(async () => { await i18n.changeLanguage('ar-EG') })
    useAuthStore.setState({ user: { id: 'a', tenantId: 't', role: 'Owner' } })
    render(<MemoryRouter><StorefrontDashboard /></MemoryRouter>)
    expect(screen.getByRole('heading', { level: 1 })).toHaveTextContent('نظرة عامة')
    expect(mounts.status).toHaveBeenCalledTimes(1)
    act(() => useAuthStore.setState({ user: { id: 'b', tenantId: 't2', role: 'Owner' } }))
    expect(mounts.status).toHaveBeenCalledTimes(2)
})
