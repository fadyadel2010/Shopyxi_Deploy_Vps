import { useQuery, useQueryClient } from '@tanstack/react-query';
import { marketplaceApi } from '../api/marketplaceApi';
import { marketplaceKeys } from '../queryKeys';
import type { ServiceSearchRequest, ServiceSearchResponse } from '../types';
import { marketplaceAuthorityQueryOptions, useMarketplaceAuthorityScope } from '../authority';

/**
 * Hook for marketplace search.
 * Returns exactly what API returns - NO client-side filtering or sorting.
 * 
 * HARDENING:
 * - Uses standardized query keys
 * - No ad-hoc keys
 */
export function useMarketplaceSearch(request: ServiceSearchRequest) {
  const authorityScope = useMarketplaceAuthorityScope();
  const client = useQueryClient();
  const generationKey = marketplaceKeys.searchGeneration(request, authorityScope);
  // Subscribe without fetching or sharing mutable state across actors/filters.
  const generation = useQuery<string | null>({
    queryKey: generationKey, queryFn: async () => null, enabled: false,
    initialData: null, staleTime: Infinity, gcTime: 15 * 60 * 1000,
  });
  const continuation = request.page > 1 ? request.generationContinuation ?? generation.data : null;
  const effectiveRequest = continuation ? { ...request, generationContinuation: continuation } : request;

  return useQuery<ServiceSearchResponse>({
    queryKey: marketplaceKeys.search(effectiveRequest, authorityScope),
    queryFn: async ({ signal }) => {
      const response = await marketplaceApi.searchServices(effectiveRequest);
      if (request.page === 1 && !signal.aborted)
        client.setQueryData(generationKey, response.generationContinuation ?? null);
      return response;
    },
    ...marketplaceAuthorityQueryOptions,
  });
}
