import { beforeEach, describe, expect, it, vi } from 'vitest'
import { render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import SellerApplicationsPage from './SellerApplicationsPage'
import { sellerApplicationsService } from './sellerApplicationsService'

const mocks = vi.hoisted(() => ({
    tr: (english: string) => english,
}))

vi.mock('../i18n/useSuperAdminLocale', () => ({
    useSuperAdminLocale: () => ({
        tr: mocks.tr,
        formatDateTime: (value: string) => value,
        isArabic: false,
    }),
}))

vi.mock('./sellerApplicationsService', () => ({
    sellerApplicationsService: {
        search: vi.fn(),
        get: vi.fn(),
        approve: vi.fn(),
        reject: vi.fn(),
    },
}))

const application = {
    id: 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa',
    tenantId: 'bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb',
    referenceNumber: 'CHS-20261116-ABCDEF1234',
    revision: 1,
    status: 'PendingReview',
    storeDisplayName: 'Ahram QA Store',
    businessCategory: 'Home',
    businessDescription: 'A detailed seller application description.',
    contactName: 'QA Owner',
    contactPhone: '+201000000000',
    contactEmail: 'qa-owner@example.test',
    productCategories: ['Home', 'Gifts'],
    submittedAtUtc: '2026-11-16T10:00:00Z',
}

const details = {
    application,
    tenantName: 'Ahram QA Store',
    tenantSlug: 'ahram-qa',
    tenantStatus: 'Active',
    isKycVerified: true,
    isMarketplaceApproved: false,
    history: [application],
}

describe('SellerApplicationsPage', () => {
    beforeEach(() => {
        vi.clearAllMocks()
        vi.mocked(sellerApplicationsService.search).mockResolvedValue({
            applications: [{
                id: application.id,
                tenantId: application.tenantId,
                tenantReference: 'ahram-qa',
                merchantName: application.storeDisplayName,
                businessCategory: application.businessCategory,
                referenceNumber: application.referenceNumber,
                revision: application.revision,
                status: 'Submitted',
                submittedAtUtc: application.submittedAtUtc,
            }],
            page: 1,
            pageSize: 50,
            totalCount: 1,
            totalPages: 1,
        })
        vi.mocked(sellerApplicationsService.get).mockResolvedValue(details)
    })

    it('requires a meaningful rejection reason before submitting the decision', async () => {
        const user = userEvent.setup()
        vi.mocked(sellerApplicationsService.reject).mockResolvedValue({
            ...details,
            application: {
                ...application,
                status: 'Rejected',
                rejectionReason: 'Please clarify the requested product categories.',
            },
        })

        render(<SellerApplicationsPage />)
        await user.click(await screen.findByRole('button', { name: /Ahram QA Store/ }))
        await screen.findByText('A detailed seller application description.')

        await user.click(screen.getByRole('button', { name: 'Reject' }))
        expect(await screen.findByRole('alert')).toHaveTextContent('at least 10 characters')
        expect(sellerApplicationsService.reject).not.toHaveBeenCalled()

        await user.type(screen.getByLabelText('Rejection reason'), 'Please clarify the requested product categories.')
        await user.click(screen.getByRole('button', { name: 'Reject' }))

        await waitFor(() => expect(sellerApplicationsService.reject).toHaveBeenCalledWith(
            application.id,
            'Please clarify the requested product categories.',
        ))
        expect(await screen.findByRole('status')).toHaveTextContent('merchant can see the reason and resubmit')
    })

    it('does not enable approval before existing merchant verification is complete', async () => {
        const user = userEvent.setup()
        vi.mocked(sellerApplicationsService.get).mockResolvedValue({ ...details, isKycVerified: false })

        render(<SellerApplicationsPage />)
        await user.click(await screen.findByRole('button', { name: /Ahram QA Store/ }))

        expect(await screen.findByRole('button', { name: 'Approve seller' })).toBeDisabled()
        expect(sellerApplicationsService.approve).not.toHaveBeenCalled()
    })
})
