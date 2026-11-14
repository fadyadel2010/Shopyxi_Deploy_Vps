import { describe, expect, it } from 'vitest'
import * as fs from 'fs'
import * as path from 'path'

const SRC = path.resolve(__dirname, '..')
const read = (relativePath: string) => fs.readFileSync(path.join(SRC, relativePath), 'utf-8')

describe('customer and supplier archival lifecycle', () => {
    it('exposes active/archived filters and canonical archive/reactivate commands', () => {
        const customers = read('pages/Customers.jsx')
        const suppliers = read('pages/Suppliers.jsx')

        for (const [source, endpoint, variable] of [
            [customers, 'customers', 'customer'],
            [suppliers, 'suppliers', 'supplier'],
        ] as const) {
            expect(source).toContain("const [statusFilter, setStatusFilter] = useState('active')")
            expect(source).toContain("statusFilter === 'all' ? undefined : statusFilter === 'active'")
            expect(source).toContain('api.post(`/' + endpoint + '/${' + variable + '.id}/${action}`')
            expect(source).toContain("? 'archive' : 'reactivate'")
            expect(source).toContain('isActive')
        }
    })

    it('keeps operational selectors active-only while management lists retain lifecycle filtering', () => {
        const lookup = read('services/entityLookup.js')
        const expenses = read('pages/Expenses.jsx')
        const purchaseReturns = read('pages/PurchaseReturns.jsx')
        const salesReturns = read('pages/SalesReturns.jsx')

        expect(lookup).toContain("searchEntity('/customers', { isActive: true, ...options })")
        expect(lookup).toContain("searchEntity('/suppliers', { isActive: true, ...options })")
        expect(expenses).toContain("params.set('isActive', 'true')")
        expect(lookup).toContain("searchCustomersIncludingArchived = (options) => searchEntity('/customers', options)")
        expect(lookup).toContain("searchSuppliersIncludingArchived = (options) => searchEntity('/suppliers', options)")
        expect(salesReturns).toContain('loadOptions={searchCustomersIncludingArchived}')
        expect(purchaseReturns).toContain('loadOptions={searchSuppliersIncludingArchived}')
    })

    it('ships English and Arabic lifecycle labels', () => {
        const en = JSON.parse(read('i18n/locales/en.json'))
        const ar = JSON.parse(read('i18n/locales/ar.json'))

        expect(en.customers.archive).toBe('Archive')
        expect(en.suppliers.reactivate).toBe('Reactivate')
        expect(ar.customers.archivedStatus).toBe('مؤرشف')
        expect(ar.suppliers.reactivate).toBe('إعادة تنشيط')
    })
})
