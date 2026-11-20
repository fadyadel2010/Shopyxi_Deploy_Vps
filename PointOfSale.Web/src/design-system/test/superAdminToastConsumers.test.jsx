import './radixJsdomPolyfills'
import { act, cleanup, fireEvent, render, screen } from '@testing-library/react'
import { MemoryRouter } from 'react-router-dom'
import { afterEach, describe, expect, it, vi } from 'vitest'

const observed = vi.hoisted(() => ({ toast: null }))
vi.mock('../../components/Toast', async (importOriginal) => {
    const actual = await importOriginal()
    return {
        ...actual,
        useToast: () => {
            const api = actual.useToast()
            observed.toast = api
            return api
        },
    }
})

// Mount the actual six page components, but never execute their query/mutation
// functions. These are isolated hook/provider wiring tests, NOT authenticated
// Super Admin runtime or page-action certification.
vi.mock('@tanstack/react-query', async (importOriginal) => {
    const actual = await importOriginal()
    const query = { data: undefined, isLoading: true, isFetching: false, isError: false, error: null, refetch: vi.fn() }
    const client = { invalidateQueries: vi.fn(), getQueryData: vi.fn(), setQueryData: vi.fn(), cancelQueries: vi.fn() }
    return {
        ...actual,
        useQuery: () => query,
        useQueryClient: () => client,
        useMutation: () => ({ mutate: vi.fn(), mutateAsync: vi.fn(), isPending: false, error: null }),
    }
})

import { ToastProvider } from '../../components/Toast'
import CommissionManagementPage from '../../super-admin/commission/CommissionManagementPage'
import HomepageBuilderPage from '../../super-admin/homepage/HomepageBuilderPage'
import PaymentProvidersPage from '../../super-admin/payment-providers/PaymentProvidersPage'
import PromoCodesPage from '../../super-admin/promo/PromoCodesPage'
import PayoutsPage from '../../super-admin/payouts/PayoutsPage'
import PaymentsPage from '../../super-admin/payments/PaymentsPage'

afterEach(() => {
    cleanup()
    observed.toast = null
})

describe('actual Super Admin page → real Toast hook/provider isolation (mocked queries)', () => {
    it.each([
        ['CommissionManagementPage', CommissionManagementPage],
        ['HomepageBuilderPage', HomepageBuilderPage],
        ['PaymentProvidersPage', PaymentProvidersPage],
        ['PromoCodesPage', PromoCodesPage],
        ['PayoutsPage', PayoutsPage],
        ['PaymentsPage', PaymentsPage],
    ])('%s retains non-POS notifications beside a POS token root', (name, Page) => {
        render(
            <MemoryRouter>
                <ToastProvider>
                    <div data-product="pos">Separate POS surface</div>
                    <Page />
                </ToastProvider>
            </MemoryRouter>,
        )
        expect(observed.toast).not.toBeNull()
        act(() => observed.toast.showError(`${name} failure`, 0))
        const alert = screen.getByRole('alert')
        expect(alert).toHaveTextContent(`${name} failure`)
        expect(alert).toHaveAttribute('aria-live', 'assertive')
        expect(alert.closest('[data-product="pos"]')).toBeNull()
        expect(alert.querySelector('.bg-red-500')).not.toBeNull()
        expect(alert.parentElement).not.toHaveAttribute('data-product')
        fireEvent.click(screen.getByRole('button', { name: 'Dismiss notification' }))
        expect(screen.queryByText(`${name} failure`)).toBeNull()
        expect(document.documentElement).not.toHaveAttribute('data-product')
        expect(document.body).not.toHaveAttribute('data-product')
    })
})
