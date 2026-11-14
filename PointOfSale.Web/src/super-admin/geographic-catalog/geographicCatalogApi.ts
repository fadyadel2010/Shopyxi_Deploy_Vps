import { superAdminApiClient } from '../core/superAdminApiClient'

const base = '/api/admin/geographic/catalog'

// The catalog controller returns raw payloads, but requires the established platform
// session. Never use the tenant/POS client or attach a tenant identity to these calls.
export const geographicCatalogApi = {
    async get<T>(path: string): Promise<{ data: T }> {
        return { data: await superAdminApiClient.getRaw<T>(`${base}${path}`) }
    },
    async post<T>(path: string, body?: unknown): Promise<{ data: T }> {
        return { data: await superAdminApiClient.postRaw<T>(`${base}${path}`, body) }
    },
}
