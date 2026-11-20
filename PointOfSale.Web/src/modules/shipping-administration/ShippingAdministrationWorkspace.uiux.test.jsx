import { cleanup, render, screen } from '@testing-library/react'
import { afterEach, expect, it, vi } from 'vitest'
import ShippingAdministrationWorkspace from './ShippingAdministrationWorkspace'
const state = vi.hoisted(() => ({ loading: true, error: '' }))
vi.mock('./hooks/useShippingAdministrationApi', () => ({
    useShippingAdministrationApi: () => ({
        supportedCarriers: [], carrierAccounts: [], warehouses: [], audit: [], refresh: vi.fn(), ...state,
    }),
}))
vi.mock('react-i18next', async importOriginal => ({
    ...await importOriginal(),
    useTranslation: () => ({ t: key => key, i18n: { language: 'en', dir: () => 'ltr' } }),
}))
afterEach(cleanup)
it.each([true, false])('hides successful zero/readiness cards and forms during loading/error (loading=%s)', loading => {
    state.loading = loading
    state.error = loading ? '' : 'RAW technical exception'
    render(<ShippingAdministrationWorkspace />)
    expect(screen.queryByText('shippingAdministration.summary.activeAccounts')).not.toBeInTheDocument()
    expect(screen.queryByText('shippingAdministration.overview.recommendedAction')).not.toBeInTheDocument()
    expect(screen.queryByText('RAW technical exception')).not.toBeInTheDocument()
    if (!loading) expect(screen.getByRole('alert')).toHaveTextContent('common.unavailable')
})
it('keeps genuine successful empty overview data visible', () => {
    state.loading = false
    state.error = ''
    render(<ShippingAdministrationWorkspace />)
    expect(screen.getByText('shippingAdministration.summary.activeAccounts')).toBeVisible()
    expect(screen.getByText('shippingAdministration.overview.recommendedAction')).toBeVisible()
})
