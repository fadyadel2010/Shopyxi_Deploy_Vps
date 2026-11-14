// SQL-backed portal contracts. Legacy paidAmount is a commission-base snapshot, never an order total.
export interface AffiliateCanonicalItem {
    orderItemId: string;
    productId: string;
    variantId: string | null;
    productName: string;
    variantName: string | null;
    quantity: number;
    linePaymentAllocated: number;
    lineRefunded: number;
    commissionTermsCaptured: boolean;
    commissionEligibleAtCheckout: boolean;
}

export interface AffiliateCanonicalOrder {
    orderId: string;
    storeTenantId: string;
    storeName: string | null;
    orderNumber: string;
    createdAtUtc: string;
    orderTotal: number;
    orderCurrency: string;
    shippingCharge: number;
    paymentFee: number;
    orderStatus: string;
    paymentStatus: string;
    fulfillmentStatus: string;
    paymentMethod: string;
    isSyntheticQa: boolean;
    hasUnclassifiedQaPayment: boolean;
    paymentEligibilityClassification: 'SyntheticQaExcluded' | 'UnclassifiedQaPayment' | 'HistoryOnlyUseBalanceApi';
    attributionId: string;
    affiliateAccountId: string;
    referralCode: string;
    attributionSource: string;
    attributedAtUtc: string;
    promotionLinkId: string | null;
    promotedProductId: string | null;
    items: AffiliateCanonicalItem[];
}

export interface AffiliateCommissionActivityEntry {
    entryId: string;
    recordedAtUtc: string;
    entryType: 'Earned' | 'Approved' | 'Paid' | 'Reversed';
    amount: number;
    currency: string;
    commissionBase: number;
    storeTenantId: string;
    orderId: string;
    orderItemId: string;
    attributionId: string;
    originalEntryId: string | null;
    orderNumber: string | null;
    productName: string | null;
    sourceOrderAvailable: boolean;
    isSyntheticQa: boolean;
    classification: 'UnclassifiedLineage' | 'SyntheticQaExcluded' | 'HistoryOnlyUseBalanceApi';
    reasonCode: 'ReversalReasonNotRecorded' | null;
}

export interface AffiliateCommissionActivityPage {
    entries: AffiliateCommissionActivityEntry[];
    page: number;
    pageSize: number;
    totalCount: number;
    totalPages: number;
}
