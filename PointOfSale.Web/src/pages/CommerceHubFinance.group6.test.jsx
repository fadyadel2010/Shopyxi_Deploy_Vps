import { act, fireEvent, render, screen } from '@testing-library/react'
import { expect, it, vi } from 'vitest'
import CommerceHubFinance from './CommerceHubFinance'
import i18n from '../i18n/config'
const mocks = vi.hoisted(() => ({
    download: vi.fn(), data: { currency: 'EGP', transactions: [], totalCount: 0, pageSize: 25 },
}))
vi.mock('@tanstack/react-query', () => ({ useQuery: () => ({ data: mocks.data, isFetching: false, refetch: vi.fn() }) }))
vi.mock('../auth/commerceHubVendorFinancialParticipationAuthority', () => ({
    deriveVendorEarningsDashboardPresentation: value => value,
    deriveVendorFinanceStatementPresentation: value => value,
    deriveVendorFinancialDashboardPresentation: value => value,
    deriveVendorPaymentsDashboardPresentation: value => value,
}))
vi.mock('../utils/commerceHubAuthorityDiagnostics', () => ({ deriveCommerceHubAuthorityDiagnostic: () => null }))
vi.mock('../services/commerceHubFinanceApi', () => ({
    COMMERCEHUB_FINANCE_TRANSACTION_TYPES: [], downloadCommerceHubFinanceStatementCsv: mocks.download,
    getCommerceHubFinanceDashboard: vi.fn(), getCommerceHubFinanceStatement: vi.fn(),
}))
it('CSV failure stays local and duplicate requests cannot start while the export is pending', async () => {
    await i18n.changeLanguage('ar')
    let fail
    mocks.download.mockReturnValue(new Promise((_resolve, reject) => { fail = reject }))
    const alert = vi.spyOn(window, 'alert').mockImplementation(() => {})
    render(<CommerceHubFinance />)
    const download = screen.getByRole('button', { name: i18n.t('CommerceHubFinance.actions.downloadCsv') })
    act(() => { fireEvent.click(download); fireEvent.click(download) })
    expect(mocks.download).toHaveBeenCalledTimes(1)
    await act(async () => { fail(new Error('private diagnostic')) })
    expect(await screen.findByRole('alert')).toHaveTextContent(i18n.t('CommerceHubFinance.actions.downloadCsvError'))
    expect(screen.queryByText('private diagnostic')).toBeNull()
    expect(alert).not.toHaveBeenCalled()
    alert.mockRestore()
    await i18n.changeLanguage('en')
})
