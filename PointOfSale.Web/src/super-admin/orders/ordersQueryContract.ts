import {
    SUPER_ADMIN_ORDER_STATUS_OPTIONS, SUPER_ADMIN_PAYMENT_FILTER_OPTIONS,
} from './ordersService'

export function getOrdersQueryError(params: URLSearchParams): boolean {
    for (const [key, allowed] of [
        ['status', SUPER_ADMIN_ORDER_STATUS_OPTIONS],
        ['paymentStatus', SUPER_ADMIN_PAYMENT_FILTER_OPTIONS],
        ['sortBy', ['CreatedAt', 'OrderNumber', 'Customer', 'Tenant', 'TotalAmount', 'Status', 'PaymentStatus', 'VendorCount']],
        ['sortDirection', ['Asc', 'Desc']],
    ] as const) {
        const value = params.get(key)
        if (value && !(allowed as readonly string[]).includes(value)) return true
    }
    const page = params.get('page')
    const size = params.get('pageSize')
    if (page && (!/^[1-9]\d*$/.test(page) || Number(page) > 21474836)) return true
    if (size && !['20', '50', '100'].includes(size)) return true
    if ((params.get('search')?.trim().length ?? 0) > 100) return true
    const tenant = params.get('tenantId')
    if (tenant && (!/^[0-9a-f]{8}(-[0-9a-f]{4}){3}-[0-9a-f]{12}$/i.test(tenant) ||
        tenant === '00000000-0000-0000-0000-000000000000')) return true
    const from = params.get('fromDate')
    const to = params.get('toDate')
    for (const value of [from, to]) {
        if (value && (!/^\d{4}-\d{2}-\d{2}$/.test(value) ||
            !Number.isFinite(Date.parse(value)) ||
            new Date(value).toISOString().slice(0, 10) !== value)) return true
    }
    return !!(to === '9999-12-31' || (from && to && from > to))
}

export function isOrdersSnapshotFresh(asOf: string | undefined, now: number): boolean {
    const time = Date.parse(asOf ?? '')
    return Number.isFinite(time) && now - time < 90_000 && time - now < 30_000
}
