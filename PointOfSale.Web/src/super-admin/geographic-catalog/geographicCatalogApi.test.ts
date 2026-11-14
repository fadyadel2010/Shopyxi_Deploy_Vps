import { beforeEach, expect, it, vi } from 'vitest'
import { geographicCatalogApi } from './geographicCatalogApi'

const platform = vi.hoisted(() => ({ getRaw: vi.fn(), postRaw: vi.fn() }))
vi.mock('../core/superAdminApiClient', () => ({ superAdminApiClient: platform }))
beforeEach(() => vi.resetAllMocks())

it('uses only the platform raw client and exact catalog endpoints', async () => {
    platform.getRaw.mockResolvedValue([{ id: 'synthetic' }])
    platform.postRaw.mockResolvedValue({ succeeded: false, failureCode: 'CatalogRevisionMismatch' })
    expect(await geographicCatalogApi.get('/releases')).toEqual({ data: [{ id: 'synthetic' }] })
    const body = { expectedContentChecksumSha256: 'synthetic', reason: 'Explicit review' }
    expect(await geographicCatalogApi.post('/releases/synthetic/publish', body)).toEqual({ data: { succeeded: false, failureCode: 'CatalogRevisionMismatch' } })
    expect(platform.getRaw).toHaveBeenCalledWith('/api/admin/geographic/catalog/releases')
    expect(platform.postRaw).toHaveBeenCalledWith('/api/admin/geographic/catalog/releases/synthetic/publish', body)
})

it('propagates platform authorization loss rather than retrying with tenant authority', async () => {
    const denied = Object.assign(new Error('Forbidden'), { status: 403 })
    platform.postRaw.mockRejectedValue(denied)
    await expect(geographicCatalogApi.post('/import/drafts', {})).rejects.toBe(denied)
    expect(platform.postRaw).toHaveBeenCalledTimes(1)
})
