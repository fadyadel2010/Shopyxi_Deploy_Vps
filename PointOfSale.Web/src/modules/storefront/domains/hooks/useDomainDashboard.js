/**
 * D01: Domain Dashboard Hook
 *
 * Loads the domain management dashboard summary via useDomainReadQuery.
 *
 * GET /api/admin/storefront/domains/dashboard
 */
import { useCallback } from 'react'
import { domainManagementService } from '../services/domainManagementService'
import { useDomainReadQuery } from './useDomainReadQuery'

export function useDomainDashboard() {
    const fetchFn = useCallback(async () => {
        const data = await domainManagementService.getDashboardSummary()
        const validDomain = row => row && typeof row.hostName === 'string' && row.hostName.trim()
        if (!data || !['totalDomains', 'sslExpiringSoon', 'pendingVerification', 'draftCount']
            .every(key => Number.isFinite(data[key]) && data[key] >= 0)
            || !Array.isArray(data.recentDomains) || !data.recentDomains.every(validDomain)
            || !(data.primaryDomain === null || validDomain(data.primaryDomain))) {
            throw new TypeError('domain-dashboard-malformed')
        }
        return data
    }, [])

    const isEmpty = (data) => data.totalDomains === 0

    return useDomainReadQuery(fetchFn, isEmpty, [])
}

export default useDomainDashboard
