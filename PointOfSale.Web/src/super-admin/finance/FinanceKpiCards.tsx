import type { LucideIcon } from 'lucide-react'
import {
    BadgeDollarSign,
    Coins,
    RotateCcw,
    ShoppingBag,
    TicketPercent,
    WalletCards,
} from 'lucide-react'
import { useSuperAdminLocale } from '../i18n/useSuperAdminLocale'
import { KpiCard } from '../ui/SuperAdminUI'
import type { SuperAdminFinancialOverview } from './financeService'

interface FinanceKpiCardsProps {
    data?: SuperAdminFinancialOverview
    isLoading?: boolean
}

type Tone = 'slate' | 'emerald' | 'amber' | 'rose' | 'sky' | 'violet'

interface MetricCard {
    key: keyof Pick<
        SuperAdminFinancialOverview,
        | 'totalGrossCommission'
        | 'totalPromoCost'
        | 'totalNetCommission'
        | 'netRevenue'
        | 'totalRefundAmount'
        | 'averageOrderValue'
    >
    label: string
    labelAr: string
    description: string
    descriptionAr: string
    icon: LucideIcon
    tone: Tone
}

const cards: MetricCard[] = [
    {
        key: 'totalGrossCommission',
        label: 'Gross Commission',
        labelAr: 'إجمالي العمولة',
        description: 'Platform commission before promo funding and refund reversals.',
        descriptionAr: 'عمولة المنصة قبل تمويل العروض وعكس المرتجعات.',
        icon: BadgeDollarSign,
        tone: 'sky',
    },
    {
        key: 'totalPromoCost',
        label: 'Promo Cost',
        labelAr: 'تكلفة العروض',
        description: 'Platform-funded commission discounts net of promo reversals.',
        descriptionAr: 'خصومات العمولة الممولة من المنصة بعد عكس العروض.',
        icon: TicketPercent,
        tone: 'amber',
    },
    {
        key: 'totalNetCommission',
        label: 'Net Commission',
        labelAr: 'صافي العمولة',
        description: 'Gross commission after promo funding.',
        descriptionAr: 'إجمالي العمولة بعد تمويل العروض.',
        icon: Coins,
        tone: 'emerald',
    },
    {
        key: 'netRevenue',
        label: 'Net Revenue',
        labelAr: 'صافي الإيراد',
        description: 'Net commission after refund-driven commission reversals.',
        descriptionAr: 'صافي العمولة بعد عكس العمولات الناتج عن المرتجعات.',
        icon: WalletCards,
        tone: 'slate',
    },
    {
        key: 'totalRefundAmount',
        label: 'Refund Value',
        labelAr: 'قيمة المرتجعات',
        description: 'Completed refund amounts in the selected reporting scope.',
        descriptionAr: 'قيم المرتجعات المكتملة في نطاق التقرير المحدد.',
        icon: RotateCcw,
        tone: 'rose',
    },
    {
        key: 'averageOrderValue',
        label: 'AOV',
        labelAr: 'متوسط قيمة الطلب',
        description: 'Average financially recognized sub-order value.',
        descriptionAr: 'متوسط قيمة الطلب الفرعي المعترف به ماليًا.',
        icon: ShoppingBag,
        tone: 'violet',
    },
]

export default function FinanceKpiCards({ data, isLoading }: FinanceKpiCardsProps) {
    const { tr, formatCurrency, formatNumber, formatPercent } = useSuperAdminLocale()

    const formatScopedCurrency = (value: number | undefined) => {
        if (value == null || !Number.isFinite(value)) return '—'
        if (String(data?.currency ?? 'EGP').trim().toUpperCase() !== 'EGP') {
            return tr('CommerceHub currency blocked', 'عملة CommerceHub غير مدعومة')
        }
        return formatCurrency(value, 'EGP')
    }

    return (
        <section className="space-y-3">
            <div className="grid gap-3 sm:grid-cols-2 xl:grid-cols-3">
                {cards.map((card) => {
                    const Icon = card.icon
                    return (
                        <KpiCard
                            key={card.key}
                            label={tr(card.label, card.labelAr)}
                            value={formatScopedCurrency(data?.[card.key])}
                            hint={tr(card.description, card.descriptionAr)}
                            tone={card.tone}
                            isLoading={isLoading && !data}
                            icon={<Icon className={`h-4 w-4 ${card.tone === 'slate' ? 'text-slate-700' : ''}`} />}
                        />
                    )
                })}
            </div>

            <div className="grid gap-3 lg:grid-cols-3">
                <div className="rounded-xl border border-slate-200 bg-white px-3.5 py-3 shadow-sm">
                    <p className="text-[10px] font-semibold uppercase tracking-[0.16em] text-slate-500">
                        {tr('Orders in scope', 'الطلبات داخل النطاق')}
                    </p>
                    <p className="mt-1 text-xl font-semibold text-slate-950" dir="auto">
                        {data ? formatNumber(data.totalOrdersCount) : '—'}
                    </p>
                    <p className="mt-0.5 text-xs leading-5 text-slate-500">
                        {tr(
                            'Distinct financially recognized CommerceHub sub-orders, including zero-rate orders.',
                            'طلبات CommerceHub الفرعية المعترف بها ماليًا دون تكرار، بما فيها الطلبات بعمولة صفرية.',
                        )}
                    </p>
                </div>

                <div className="rounded-xl border border-slate-200 bg-white px-3.5 py-3 shadow-sm">
                    <p className="text-[10px] font-semibold uppercase tracking-[0.16em] text-slate-500">
                        {tr('Refund rate', 'معدل المرتجعات')}
                    </p>
                    <p className="mt-1 text-xl font-semibold text-slate-950" dir="auto">
                        {data ? formatPercent(data.refundRate) : '—'}
                    </p>
                    <p className="mt-0.5 text-xs leading-5 text-slate-500">
                        {data ? formatNumber(data.refundedOrdersCount) : '—'}{' '}
                        {tr('recognized sub-orders in this window with a completed refund in this window.', 'طلب فرعي معترف به في هذه الفترة وله استرداد مكتمل في الفترة نفسها.')}
                    </p>
                </div>

                <div className="rounded-xl border border-slate-200 bg-white px-3.5 py-3 shadow-sm">
                    <p className="text-[10px] font-semibold uppercase tracking-[0.16em] text-slate-500">
                        {tr('Revenue drag', 'تأثير الإيراد')}
                    </p>
                    <p className="mt-1 text-xl font-semibold text-slate-950" dir="auto">
                        {formatScopedCurrency(data?.refundImpactOnRevenue)}
                    </p>
                    <p className="mt-0.5 text-xs leading-5 text-slate-500">
                        {tr(
                            'Commission revenue reversed because of completed refunds.',
                            'إيراد عمولة معكوس بسبب المرتجعات المكتملة.',
                        )}
                    </p>
                </div>
            </div>
        </section>
    )
}
