import { beforeEach, describe, expect, it, vi } from 'vitest'

const authServiceMock = vi.hoisted(() => ({
    getSession: vi.fn(),
    refreshSession: vi.fn(),
}))

const authorityMock = vi.hoisted(() => ({
    revision: 7,
    tokenVersion: 3 as number | undefined,
    canRefresh: true,
}))

vi.mock('./superAdminAuthService', () => ({
    getSuperAdminSession: authServiceMock.getSession,
    refreshSuperAdminSession: authServiceMock.refreshSession,
    SuperAdminAuthError: class SuperAdminAuthError extends Error {
        status?: number

        constructor(message: string, options?: { status?: number }) {
            super(message)
            this.status = options?.status
        }
    },
}))

vi.mock('./superAdminAuthStore', () => ({
    getSuperAdminAuthorityRevision: () => authorityMock.revision,
    getStoredSuperAdminTokenVersion: () => authorityMock.tokenVersion,
}))

vi.mock('./superAdminSession', () => ({
    canRefreshSuperAdminSession: () => authorityMock.canRefresh,
}))

import {
    isDefinitiveSuperAdminAuthFailure,
    restoreSuperAdminSession,
} from './superAdminSessionCoordinator'

const session = {
    superAdminId: '11111111-1111-4111-8111-111111111111',
    email: 'admin@example.test',
    role: 'SuperAdmin' as const,
    tokenType: 'Bearer',
    expiresAtUtc: '2099-01-01T00:00:00.000Z',
    refreshTokenExpiresAtUtc: '2099-01-02T00:00:00.000Z',
    tokenVersion: 3,
    navigationCapabilities: ['super-admin.finance.view' as const],
}

const unauthorized = () => ({ status: 401 })

describe('Super Admin session coordinator', () => {
    beforeEach(() => {
        authServiceMock.getSession.mockReset()
        authServiceMock.refreshSession.mockReset()
        authorityMock.revision = 7
        authorityMock.tokenVersion = 3
        authorityMock.canRefresh = true
    })

    it('returns a valid current session without rotating refresh credentials', async () => {
        authServiceMock.getSession.mockResolvedValue(session)

        await expect(restoreSuperAdminSession()).resolves.toEqual(session)
        expect(authServiceMock.refreshSession).not.toHaveBeenCalled()
    })

    it('rejects initial session-read settlement after same-actor logout/login ABA without revoking the new session', async () => {
        authServiceMock.getSession.mockImplementationOnce(async () => {
            authorityMock.revision += 2
            return session
        })
        const error = await restoreSuperAdminSession().catch((failure: unknown) => failure)
        expect(error).toMatchObject({ status: 409 })
        expect(isDefinitiveSuperAdminAuthFailure(error)).toBe(false)
        expect(authServiceMock.refreshSession).not.toHaveBeenCalled()
    })

    it('fences a changed actor during the lock-protected reread before rotating credentials', async () => {
        authServiceMock.getSession.mockRejectedValueOnce(unauthorized()).mockImplementationOnce(async () => {
            authorityMock.revision += 1
            return session
        })
        await expect(restoreSuperAdminSession()).rejects.toMatchObject({ status: 409 })
        expect(authServiceMock.refreshSession).not.toHaveBeenCalled()
    })

    it('rejects a late refresh response as an operation conflict, not authentication loss', async () => {
        authServiceMock.getSession.mockRejectedValueOnce(unauthorized()).mockRejectedValueOnce(unauthorized()).mockResolvedValue(session)
        authServiceMock.refreshSession.mockImplementationOnce(async () => { authorityMock.revision += 1; return session })
        const error = await restoreSuperAdminSession().catch((failure: unknown) => failure)
        expect(error).toMatchObject({ status: 409 })
        expect(isDefinitiveSuperAdminAuthFailure(error)).toBe(false)
        expect(authServiceMock.refreshSession).toHaveBeenCalledOnce()
    })

    it('single-flights concurrent 401 recovery and rotates the refresh token once', async () => {
        authServiceMock.getSession
            .mockRejectedValueOnce(unauthorized())
            .mockRejectedValueOnce(unauthorized())
            .mockRejectedValueOnce(unauthorized())
            .mockResolvedValue(session)
        authServiceMock.refreshSession.mockResolvedValue(session)

        const [first, second] = await Promise.all([
            restoreSuperAdminSession(),
            restoreSuperAdminSession(),
        ])

        expect(first).toEqual(session)
        expect(second).toEqual(session)
        expect(authServiceMock.refreshSession).toHaveBeenCalledOnce()
        expect(authServiceMock.refreshSession).toHaveBeenCalledWith({ tokenVersion: 3 })
    })

    it('does not refresh or hide a temporary session-service failure as authentication loss', async () => {
        const networkError = new Error('network unavailable')
        authServiceMock.getSession.mockRejectedValue(networkError)

        await expect(restoreSuperAdminSession()).rejects.toBe(networkError)
        expect(authServiceMock.refreshSession).not.toHaveBeenCalled()
        expect(isDefinitiveSuperAdminAuthFailure(networkError)).toBe(false)
    })

    it('classifies rejected session authority as definitive', () => {
        expect(isDefinitiveSuperAdminAuthFailure({ status: 401 })).toBe(true)
        expect(isDefinitiveSuperAdminAuthFailure({ status: 403 })).toBe(true)
        expect(isDefinitiveSuperAdminAuthFailure({ status: 503 })).toBe(false)
    })
})
