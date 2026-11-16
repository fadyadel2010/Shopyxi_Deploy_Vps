import { superAdminApiClient } from '../core/superAdminApiClient'

export interface SellerApplication {
    id: string
    tenantId: string
    referenceNumber: string
    revision: number
    status: string
    storeDisplayName: string
    businessCategory: string
    businessDescription: string
    contactName: string
    contactPhone: string
    contactEmail: string
    productCategories: string[]
    storefrontUrl?: string | null
    submittedAtUtc: string
    reviewerIdentity?: string | null
    reviewedAtUtc?: string | null
    rejectionReason?: string | null
}

export interface SellerApplicationListItem {
    id: string
    tenantId: string
    tenantReference: string
    merchantName: string
    businessCategory: string
    referenceNumber: string
    revision: number
    status: string
    submittedAtUtc: string
    reviewedAtUtc?: string | null
}

export interface SellerApplicationSearchResult {
    applications: SellerApplicationListItem[]
    page: number
    pageSize: number
    totalCount: number
    totalPages: number
}

export interface SellerApplicationDetails {
    application: SellerApplication
    tenantName: string
    tenantSlug?: string | null
    tenantStatus: string
    isKycVerified: boolean
    isMarketplaceApproved: boolean
    history: SellerApplication[]
}

const basePath = '/commercehub/seller-applications'

export const sellerApplicationsService = {
    async search(status: string, search = ''): Promise<SellerApplicationSearchResult> {
        const params = new URLSearchParams({ status, page: '1', pageSize: '50' })
        if (search.trim()) params.set('search', search.trim())
        return (await superAdminApiClient.get<SellerApplicationSearchResult>(`${basePath}?${params}`)).data
    },
    async get(applicationId: string): Promise<SellerApplicationDetails> {
        return (await superAdminApiClient.get<SellerApplicationDetails>(`${basePath}/${encodeURIComponent(applicationId)}`)).data
    },
    async approve(applicationId: string): Promise<SellerApplicationDetails> {
        return (await superAdminApiClient.post<SellerApplicationDetails>(`${basePath}/${encodeURIComponent(applicationId)}/approve`, {})).data
    },
    async reject(applicationId: string, reason: string): Promise<SellerApplicationDetails> {
        return (await superAdminApiClient.post<SellerApplicationDetails, { reason: string }>(
            `${basePath}/${encodeURIComponent(applicationId)}/reject`,
            { reason },
        )).data
    },
}
