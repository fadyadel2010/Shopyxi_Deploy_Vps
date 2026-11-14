const invoiceStatuses = ['Draft', 'Completed', 'Cancelled', 'PartiallyReturned', 'FullyReturned'] as const

export const getInvoiceStatus = (status: unknown) => typeof status === 'number'
    ? invoiceStatuses[status]
    : invoiceStatuses.find(value => value === status)

export const canRewriteInvoice = (type: string, invoice: unknown): boolean => (
    type !== 'sales' || typeof invoice === 'object' && invoice !== null && 'status' in invoice &&
        (invoice.status === 'Draft' || invoice.status === 0)
)
