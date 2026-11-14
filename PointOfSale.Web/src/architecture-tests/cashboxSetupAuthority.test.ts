import { readFileSync } from 'node:fs'
import { resolve } from 'node:path'
import { describe, expect, it } from 'vitest'

const readSource = (path: string) => readFileSync(resolve(process.cwd(), 'src', path), 'utf8')

describe('cashbox setup authority', () => {
    it('exposes a finance.operate-gated cashbox management route without reviving the legacy /cashbox route', () => {
        const app = readSource('App.jsx')

        expect(app).toContain("import Cashbox from './pages/Cashbox'")
        expect(app).toContain('<Route path="treasury/cashboxes" element={<Cashbox />} />')
        expect(app).toContain('<Route path="cashbox" element={<Navigate to="/treasury/hub" replace />} />')
        expect(app).toContain('<Route element={<PosPermissionRouteGuard permission="finance.operate" />}>')
    })

    it('links cashbox setup from the Treasury Hub and manual payout empty state', () => {
        expect(readSource('pages/treasury/TreasuryHub.jsx')).toContain("navigate('/treasury/cashboxes')")
        expect(readSource('modules/affiliate/pages/AdminManualPayoutDialog.jsx'))
            .toContain('href="/treasury/cashboxes"')
    })

    it('routes cashbox funding through balanced Treasury transfers instead of unsupported direct cash mutation', () => {
        const cashbox = readSource('pages/Cashbox.jsx')

        expect(cashbox).toContain("navigate('/treasury/transfers')")
        expect(cashbox).toContain("t('cashbox.balancedTransferPolicy')")
        expect(cashbox).not.toContain("openTransactionModal('cash-in')")
        expect(cashbox).not.toContain("openTransactionModal('cash-out')")
        expect(cashbox).not.toContain("import CashboxTransactionModal")
    })
})
