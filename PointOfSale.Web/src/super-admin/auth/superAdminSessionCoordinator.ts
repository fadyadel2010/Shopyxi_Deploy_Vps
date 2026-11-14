import { runWithCrossTabRefreshLock } from '../../auth/crossTabRefreshLock'
import {
    getSuperAdminAuthorityRevision,
    getStoredSuperAdminTokenVersion,
} from './superAdminAuthStore'
import {
    getSuperAdminSession,
    refreshSuperAdminSession,
    SuperAdminAuthError,
} from './superAdminAuthService'
import { canRefreshSuperAdminSession } from './superAdminSession'
import {
    requireSuperAdminSessionAuthority,
    type SuperAdminSessionResponse,
} from './superAdminAuth.types'

let refreshPromise: Promise<SuperAdminSessionResponse> | null = null

function requireUnchangedAuthority(revision: number): void {
    if (revision !== getSuperAdminAuthorityRevision()) {
        // Reject the old operation without revoking a newer valid session.
        throw new SuperAdminAuthError('Super Admin actor changed during refresh.', { status: 409, code: 'AUTHORITY_CHANGED' })
    }
}

export function isDefinitiveSuperAdminAuthFailure(error: unknown): boolean {
    const status = (error as { status?: number })?.status
    return status === 401 || status === 403
}

async function readValidatedSuperAdminSession(): Promise<SuperAdminSessionResponse> {
    const session = await getSuperAdminSession()

    try {
        return requireSuperAdminSessionAuthority(session)
    } catch {
        throw new SuperAdminAuthError('Super Admin session authority payload was invalid.', { status: 401 })
    }
}

export async function restoreSuperAdminSession(): Promise<SuperAdminSessionResponse> {
    const callerAuthorityRevision = getSuperAdminAuthorityRevision()
    try {
        const session = await readValidatedSuperAdminSession()
        requireUnchangedAuthority(callerAuthorityRevision)
        return session
    } catch (error) {
        requireUnchangedAuthority(callerAuthorityRevision)
        if ((error as { status?: number })?.status !== 401 || !canRefreshSuperAdminSession()) {
            throw error
        }
    }

    if (!refreshPromise) {
        refreshPromise = runWithCrossTabRefreshLock('super-admin-session-refresh', async () => {
            const authorityRevisionAtStart = getSuperAdminAuthorityRevision()
            try {
                const session = await readValidatedSuperAdminSession()
                requireUnchangedAuthority(authorityRevisionAtStart)
                return session
            } catch (error) {
                requireUnchangedAuthority(authorityRevisionAtStart)
                if ((error as { status?: number })?.status !== 401) {
                    throw error
                }
            }

            const tokenVersionResult = getStoredSuperAdminTokenVersion()
            if (tokenVersionResult !== undefined && typeof tokenVersionResult === 'object') {
                throw new SuperAdminAuthError('Super Admin token version authority is unavailable.', { status: 401 })
            }
            const tokenVersion = tokenVersionResult

            await refreshSuperAdminSession({ tokenVersion })
            const session = await readValidatedSuperAdminSession()

            requireUnchangedAuthority(authorityRevisionAtStart)

            return session
        }).finally(() => {
            refreshPromise = null
        })
    }

    const session = await refreshPromise
    requireUnchangedAuthority(callerAuthorityRevision)
    return session
}
