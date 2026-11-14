import type { ReactElement } from 'react';

export interface SaleDetailsRecord {
    id: string;
    invoiceNumber: string;
    invoiceDate: string;
    status?: unknown;
    paymentMethod?: string;
    subtotal?: number;
    discountAmount?: number;
    taxAmount?: number;
    totalAmount: number;
    customer?: { name: string; phone?: string } | null;
    items?: {
        id: string;
        productName?: string;
        productVariant?: { sku: string } | null;
        quantity: number;
        unitPrice: number;
        totalAmount: number;
    }[];
}

export default function SaleDetailsModal(props: {
    sale: SaleDetailsRecord;
    onClose(): void;
    onReturnProcessed(response: unknown): void;
}): ReactElement;
