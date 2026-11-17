import { describe, expect, it } from 'vitest'
import { getOrdersQueryError, isOrdersSnapshotFresh } from './ordersQueryContract'
import { SUPER_ADMIN_ORDER_STATUS_OPTIONS, isFailedPaymentStatus, isPendingOrderStatus } from './ordersService'

describe('Orders query contract', () => {
    it.each([
        'status=Packed','paymentStatus=999','fromDate=2026-02-30','fromDate=bad',
        'fromDate=2026-11-20&toDate=2026-11-01','page=0','page=2foo','pageSize=101',
        'sortDirection=sideways','tenantId=garbage','toDate=9999-12-31',
    ])('rejects %s without remapping it to an unrelated valid state', query => {
        expect(getOrdersQueryError(new URLSearchParams(query))).toBe(true)
    })
    it.each(['','search=a','fromDate=2026-11-01','toDate=2026-11-16',
        'status=OnHold&paymentStatus=Failed&pageSize=100&sortDirection=Asc'])('accepts %s', query => {
        expect(getOrdersQueryError(new URLSearchParams(query))).toBe(false)
    })
    it('offers only persisted OrderStatus enum members', () => {
        expect(SUPER_ADMIN_ORDER_STATUS_OPTIONS).toEqual(['Pending','Confirmed','Processing','Shipped','Delivered','Completed','Cancelled','Refunded','OnHold'])
        expect(isPendingOrderStatus('Confirmed')).toBe(false)
        expect(isFailedPaymentStatus('Expired')).toBe(true)
        expect(isFailedPaymentStatus('AmountMismatch')).toBe(true)
        expect(isFailedPaymentStatus('Collected')).toBe(false)
    })
    it('detects stale, invalid and future-dated timestamps', () => {
        const now=Date.now()
        expect(isOrdersSnapshotFresh(new Date(now).toISOString(),now)).toBe(true)
        expect(isOrdersSnapshotFresh(new Date(now-90_001).toISOString(),now)).toBe(false)
        expect(isOrdersSnapshotFresh(new Date(now+90_001).toISOString(),now)).toBe(false)
        expect(isOrdersSnapshotFresh(undefined,now)).toBe(false)
    })
})
