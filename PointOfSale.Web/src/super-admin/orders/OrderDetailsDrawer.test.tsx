import { cleanup, fireEvent, render, screen } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import OrderDetailsDrawer from './OrderDetailsDrawer'
import type { SuperAdminOrderDetails } from './ordersService'
const language = vi.hoisted(() => ({ arabic: false }))
vi.mock('../i18n/useSuperAdminLocale', () => ({
    useSuperAdminLocale: () => ({
        tr: (en: string, ar: string) => language.arabic ? ar : en, locale: language.arabic ? 'ar-EG' : 'en-US',
        formatCurrency: (n: number) => `EGP ${n.toFixed(2)}`, formatPercent: (n: number) => `${n * 100}%`,
        formatDateTime: (s: string) => s || '—',
    }),
}))
const order: SuperAdminOrderDetails = {
    orderId:'fixture-order', orderNumber:'CH-FIXTURE',customerName:'Synthetic fixture',
    addressLine:'Isolated test street',currency:'EGP',subtotal:90,shippingTotal:10,totalAmount:100,
    status:'Confirmed',paymentStatus:'Collected',paymentMethodCode:'PAYMOB',paymentMethodName:'Online',
    createdAt:'2026-11-17T10:00:00Z',vendorCount:1,tenantName:'Fixture vendor',
    platformCommissionTotal:9,vendorEarningsTotal:91,
    payments:[{paymentId:'fixture-payment',amount:100,currency:'EGP',status:'Paid',provider:'Paymob',
        providerReference:'safe-fixture-reference',isProcessed:true,createdAt:'2026-11-17T10:00:00Z',
        attempts:[{attemptId:'fixture-attempt',attemptNumber:1,provider:'Paymob',status:'Failed',
            providerReference:'fixture-attempt-reference',createdAt:'2026-11-17T10:00:00Z'}]}],
    subOrders:[{subOrderId:'fixture-sub',orderNumber:'CH-FIXTURE-A',vendorId:'fixture-vendor',
        vendorName:'Fixture vendor',amount:100,currency:'EGP',status:'Confirmed',paymentStatus:'Collected',
        platformCommissionAmount:9,platformCommissionRate:.1,vendorEarningAmount:91,paymentMethodName:'Online',itemCount:1}],
    orderItems:[{itemId:'fixture-item',subOrderId:'fixture-sub',vendorName:'Fixture vendor',productName:'Fixture product',
        quantity:1,unitPrice:90,totalPrice:90,refunds:[{refundId:'request-fixture',quantity:1,amount:10,
            status:'UnderReview',reason:'Fixture request',createdAt:'2026-11-17T10:00:00Z'}]}],
    statusHistory:[{historyId:'history-fixture',subOrderId:'fixture-sub',previousStatus:'Pending',
        newStatus:'Processing',changedAtUtc:'2026-11-17T10:00:00Z'}],
    refunds:[{refundId:'refund-fixture',subOrderId:'fixture-sub',amount:10,commissionReversalAmount:1,
        promoDiscountReversalAmount:0,vendorEarningReversalAmount:9,currency:'EGP',status:'Completed',
        reason:'Fixture execution',isFullRefund:false,isPostPayoutAdjustment:false,createdAt:'2026-11-17T10:00:00Z'}],
}
afterEach(() => {cleanup(); language.arabic=false})
describe('Order inspection remains read-only and accessible', () => {
    it('renders stored attempts, status history, requests and executions without a mutation button', () => {
        render(<OrderDetailsDrawer isOpen order={order} isLoading={false} isError={false} onClose={vi.fn()} onRetry={vi.fn()} />)
        expect(screen.getByRole('dialog',{name:'Order details'})).toBeInTheDocument()
        for(const text of ['Payment attempts','Lifecycle history','Refund history','Fixture request','Fixture execution'])
            expect(screen.getByText(new RegExp(text))).toBeInTheDocument()
        expect(screen.queryByRole('button',{name:/cancel order|refund order|capture|fulfill/i})).not.toBeInTheDocument()
        expect(screen.getByRole('button',{name:'Close order details'})).toHaveFocus()
    })
    it('does not leave an off-screen focusable drawer when closed', () => {
        render(<OrderDetailsDrawer isOpen={false} order={order} isLoading={false} isError={false} onClose={vi.fn()} onRetry={vi.fn()} />)
        expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
        expect(screen.queryByRole('button',{name:'Close order details'})).not.toBeInTheDocument()
    })
    it('supports Escape and restores background focus', () => {
        const previous=document.createElement('button');document.body.append(previous);previous.focus()
        const close=vi.fn()
        const view=render(<OrderDetailsDrawer isOpen order={order} isLoading={false} isError={false} onClose={close} onRetry={vi.fn()} />)
        fireEvent.keyDown(window,{key:'Escape'});expect(close).toHaveBeenCalledOnce()
        view.unmount();expect(previous).toHaveFocus();previous.remove()
    })
    it('localizes payment/return evidence in Arabic', () => {
        language.arabic=true
        render(<OrderDetailsDrawer isOpen order={order} isLoading={false} isError={false} onClose={vi.fn()} onRetry={vi.fn()} />)
        expect(screen.getByText('محاولات الدفع')).toBeInTheDocument()
        expect(screen.getByText('سجل دورة الطلب')).toBeInTheDocument()
        expect(screen.getByText('سجل الاسترداد')).toBeInTheDocument()
        expect(screen.queryByText('UnderReview')).not.toBeInTheDocument()
    })
    it('shows a detail error rather than rendering stale financial evidence', () => {
        render(<OrderDetailsDrawer isOpen order={order} isLoading={false} isError onClose={vi.fn()} onRetry={vi.fn()} />)
        expect(screen.getByText('Unable to load order details')).toBeInTheDocument()
        expect(screen.queryByText('Financial snapshot')).not.toBeInTheDocument()
    })
})
