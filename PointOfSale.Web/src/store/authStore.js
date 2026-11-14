import { create } from 'zustand'
import { derivePosVendorSessionUserAuthority } from '../auth/posVendorSessionAuthority'

export const STORAGE_KEYS = {
    TENANT_ID: 'tenantId',
    AUTH_STORAGE: 'auth-storage',
}

export const LEGACY_AUTH_TOKEN_KEYS = ['accessToken', 'refreshToken']
let posAuthorityRevision = 0
export const getPosAuthorityRevision = () => posAuthorityRevision

const hasStorage = () => typeof window !== 'undefined' && typeof window.localStorage !== 'undefined'

const removeStoredValue = (key) => {
    if (!hasStorage()) {
        return
    }

    window.localStorage.removeItem(key)
}

export const clearLegacyPosTokenStorage = () => {
    LEGACY_AUTH_TOKEN_KEYS.forEach((key) => removeStoredValue(key))
}

const writeUserSession = () => {
    clearLegacyPosTokenStorage()
    removeStoredValue(STORAGE_KEYS.TENANT_ID)
}

const clearStoredSession = () => {
    clearLegacyPosTokenStorage()
    removeStoredValue(STORAGE_KEYS.TENANT_ID)
    removeStoredValue(STORAGE_KEYS.AUTH_STORAGE)
}

const hasSameUserAuthority = (currentUser, nextUser) => (
    currentUser === nextUser || JSON.stringify(currentUser) === JSON.stringify(nextUser)
)

const applyUserSession = (set, get, user) => {
    const authorizedUser = derivePosVendorSessionUserAuthority(user)

    if (!authorizedUser) {
        clearStoredSession()
        set({ user: null, isAuthenticated: false, hasHydrated: true, isRouteAuthorityValidating: false })
        return
    }

    writeUserSession(authorizedUser)
    const currentState = get()
    if (currentState.isAuthenticated && hasSameUserAuthority(currentState.user, authorizedUser)) {
        if (!currentState.hasHydrated) {
            set({ hasHydrated: true })
        }
        return
    }

    set({ user: authorizedUser, isAuthenticated: true, hasHydrated: true })
}

export const useAuthStore = create((set, get) => ({
    user: null,
    isAuthenticated: false,
    hasHydrated: false,
    isRouteAuthorityValidating: false,

    beginRouteAuthorityValidation: () => set({ isRouteAuthorityValidating: true }),
    finishRouteAuthorityValidation: () => set({ isRouteAuthorityValidating: false }),

    login: (user) => {
        posAuthorityRevision += 1
        applyUserSession(set, get, user)
        set({ isRouteAuthorityValidating: false })
    },

    logout: () => {
        posAuthorityRevision += 1
        clearStoredSession()
        set({ user: null, isAuthenticated: false, hasHydrated: true, isRouteAuthorityValidating: false })
    },

    hydrateSession: (user) => {
        posAuthorityRevision += 1
        applyUserSession(set, get, user)
    },

    updateUser: (user) => {
        posAuthorityRevision += 1
        applyUserSession(set, get, user)
    },

    setHasHydrated: (hasHydrated) => set({ hasHydrated }),
}))
