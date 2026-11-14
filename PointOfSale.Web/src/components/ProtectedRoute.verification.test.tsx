import { describe, expect, it, vi } from 'vitest'
import { render, screen } from '@testing-library/react'
import { MemoryRouter, Route, Routes } from 'react-router-dom'
import ProtectedRoute from './ProtectedRoute'
import { useAuthStore } from '../store/authStore'

vi.mock('../store/authStore', () => ({ useAuthStore: vi.fn() }))
vi.mock('./EmailVerificationRequired', () => ({ default: () => <h1>Verification required</h1> }))

describe('Owner verification route boundary', () => {
    it.each([false, undefined])('does not grant normal Owner UI with verification=%s', emailVerified => {
        vi.mocked(useAuthStore).mockReturnValue({ hasHydrated: true, isAuthenticated: true, user: { role: 'Owner', emailVerified } })
        render(<MemoryRouter><Routes><Route element={<ProtectedRoute />}><Route path="/" element={<h1>Private dashboard</h1>} /></Route></Routes></MemoryRouter>)
        expect(screen.getByText('Verification required')).toBeInTheDocument()
        expect(screen.queryByText('Private dashboard')).not.toBeInTheDocument()
    })
    it('grants normal UI to a verified Owner', () => {
        vi.mocked(useAuthStore).mockReturnValue({ hasHydrated: true, isAuthenticated: true, user: { role: 'Owner', emailVerified: true } })
        render(<MemoryRouter><Routes><Route element={<ProtectedRoute />}><Route path="/" element={<h1>Private dashboard</h1>} /></Route></Routes></MemoryRouter>)
        expect(screen.getByText('Private dashboard')).toBeInTheDocument()
    })
})
