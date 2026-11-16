import { beforeEach, describe, expect, it, vi } from 'vitest'
import { render, screen } from '@testing-library/react'
import { MemoryRouter } from 'react-router-dom'
import CommerceHubSellerApplication from './CommerceHubSellerApplication'
import api from '../services/api'

const mocks = vi.hoisted(() => ({
    showSuccess: vi.fn(),
    showError: vi.fn(),
    t: (key: string) => ({
        'commerceHub.sellerApplication.title': 'Become a CommerceHub Seller',
        'commerceHub.sellerApplication.currentStatus': 'Application status',
        'commerceHub.sellerApplication.status.PendingReview': 'Pending review',
        'commerceHub.sellerApplication.status.Rejected': 'Rejected',
        'commerceHub.sellerApplication.statusHelp.PendingReview': 'Waiting for review',
        'commerceHub.sellerApplication.statusHelp.Rejected': 'Correct and resubmit',
        'commerceHub.sellerApplication.rejectionReason': 'Why the application was rejected',
        'commerceHub.sellerApplication.correctAndResubmit': 'Correct and resubmit your application',
        'commerceHub.sellerApplication.resubmit': 'Resubmit for review',
    } as Record<string, string>)[key] || key,
}))

vi.mock('../services/api', () => ({
    default: {
        get: vi.fn(),
        post: vi.fn(),
    },
}))

vi.mock('../components/Toast', () => ({
    useToast: () => ({ showSuccess: mocks.showSuccess, showError: mocks.showError }),
}))

vi.mock('../hooks/useFormatting', () => ({
    useFormatting: () => ({
        isRTL: false,
        formatDateTime: (value: string) => value,
    }),
}))

vi.mock('react-i18next', () => ({
    useTranslation: () => ({
        t: mocks.t,
    }),
}))

const application = {
    id: 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa',
    tenantId: 'bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb',
    referenceNumber: 'CHS-20261116-ABCDEF1234',
    revision: 1,
    status: 'PendingReview',
    storeDisplayName: 'Ahram QA Store',
    businessCategory: 'Home',
    businessDescription: 'A detailed description for the seller application.',
    contactName: 'QA Owner',
    contactPhone: '+201000000000',
    contactEmail: 'qa@example.test',
    productCategories: ['Home'],
    storefrontUrl: 'https://qa.example.test',
    submittedAtUtc: '2026-11-16T10:00:00Z',
}

describe('CommerceHubSellerApplication', () => {
    beforeEach(() => vi.clearAllMocks())

    it('shows a durable pending decision and prevents a duplicate form', async () => {
        vi.mocked(api.get).mockResolvedValue({
            data: {
                status: 'PendingReview',
                publishingEnabled: false,
                canSubmit: false,
                canResubmit: false,
                prefill: {},
                currentApplication: application,
                history: [application],
            },
        })

        render(<MemoryRouter><CommerceHubSellerApplication /></MemoryRouter>)

        expect(await screen.findByText('Pending review')).toBeInTheDocument()
        expect(screen.getByText('CHS-20261116-ABCDEF1234')).toBeInTheDocument()
        expect(screen.queryByRole('button', { name: 'commerceHub.sellerApplication.submit' })).not.toBeInTheDocument()
    })

    it('shows the exact rejection reason and the correction form', async () => {
        vi.mocked(api.get).mockResolvedValue({
            data: {
                status: 'Rejected',
                publishingEnabled: false,
                canSubmit: false,
                canResubmit: true,
                prefill: {},
                currentApplication: {
                    ...application,
                    status: 'Rejected',
                    rejectionReason: 'Please clarify the requested product categories.',
                    reviewedAtUtc: '2026-11-16T11:00:00Z',
                },
                history: [{ ...application, status: 'Rejected' }],
            },
        })

        render(<MemoryRouter><CommerceHubSellerApplication /></MemoryRouter>)

        expect(await screen.findByText('Please clarify the requested product categories.')).toBeInTheDocument()
        expect(screen.getByRole('button', { name: 'Resubmit for review' })).toBeInTheDocument()
        expect(screen.getByDisplayValue('Ahram QA Store')).toBeInTheDocument()
    })
})
