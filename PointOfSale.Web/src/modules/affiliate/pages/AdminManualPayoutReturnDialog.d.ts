import type { ReactElement } from 'react';
export default function AdminManualPayoutReturnDialog(props: {
    requestId: string;
    formatCurrency: (amount: number, currency: string) => string;
    onClose: () => void;
    onCommitted?: () => void;
}): ReactElement;
