import { useMutation, useQueryClient } from '@tanstack/react-query'
import { marketplaceApi } from '../api/marketplaceApi'
import { marketplaceKeys } from '../queryKeys'
import { getMarketplaceAuthorityScope, publishMarketplaceAuthorityInvalidation, useMarketplaceAuthorityScope } from '../authority'
import type { CreateServiceRequest } from '../types'

export function useMarketplaceServiceEditor(onCreated: () => void, onUpdated: () => void) {
    const queryClient = useQueryClient()
    const authorityScope = useMarketplaceAuthorityScope()
    const requireCurrentAuthority = () => {
        if (authorityScope === 'ineligible' || getMarketplaceAuthorityScope() !== authorityScope) {
            throw new Error('Current marketplace editor authority is unavailable')
        }
    }
    const invalidate = () => {
        publishMarketplaceAuthorityInvalidation(authorityScope)
        queryClient.invalidateQueries({ queryKey: marketplaceKeys.scope(authorityScope) })
    }
    const createServiceMutation = useMutation({
        mutationFn: async (data: CreateServiceRequest) => {
            requireCurrentAuthority()
            const response = await marketplaceApi.createService(data)
            requireCurrentAuthority()
            return response
        },
        retry: false, networkMode: 'online',
        onSuccess: () => { queryClient.invalidateQueries({ queryKey: marketplaceKeys.scope(authorityScope) }); onCreated() },
        onSettled: invalidate,
    })
    const updateServiceMutation = useMutation({
        mutationFn: async ({ id, data }: { id: string; data: CreateServiceRequest }) => {
            requireCurrentAuthority()
            const response = await marketplaceApi.updateService(id, data)
            requireCurrentAuthority()
            return response
        },
        retry: false, networkMode: 'online',
        onSuccess: () => { queryClient.invalidateQueries({ queryKey: marketplaceKeys.scope(authorityScope) }); onUpdated() },
        onSettled: invalidate,
    })
    return { createServiceMutation, updateServiceMutation }
}
