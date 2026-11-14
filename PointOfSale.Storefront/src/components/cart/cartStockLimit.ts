/** A null/absent server cap is not zero stock. The server remains the authority. */
export function isAtCartStockLimit(quantity: number, availableStock: number | null | undefined): boolean {
    return typeof availableStock === 'number'
        && Number.isFinite(availableStock)
        && quantity >= Math.max(0, availableStock);
}
