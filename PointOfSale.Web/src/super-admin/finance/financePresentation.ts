type Translate = (english: string, arabic: string) => string

export function financeIntegrityLabel(status: string | undefined, tr: Translate): string {
    switch (status) {
        case 'OK': return tr('Selected checks passed', 'اجتازت الفحوص المحددة')
        case 'NO_RECORDS': return tr('No records checked', 'لم تُفحص سجلات')
        case 'ISSUES_DETECTED': return tr('Issues detected', 'تم اكتشاف مشاكل')
        case 'UNAVAILABLE': return tr('Unavailable', 'غير متاح')
        default: return tr('Checking', 'جارٍ الفحص')
    }
}

export function financeSourceLabel(source: string | undefined, tr: Translate): string {
    switch (source) {
        case 'LIVE_LEDGER': return tr('Live ledger', 'دفتر الأستاذ المباشر')
        case 'DAILY_AGGREGATE': return tr('Validated daily projection', 'التجميع اليومي المتحقق منه')
        default: return tr('Unavailable', 'غير متاح')
    }
}

const issueLabels: Record<string, [string, string]> = {
    ORDER_MATH_MISMATCH: ['Order arithmetic mismatch', 'عدم تطابق حسابات الطلب'],
    ORDER_LEDGER_MISMATCH: ['Order ledger mismatch', 'عدم تطابق دفتر الطلب'],
    PROMO_USAGE_MISSING: ['Missing promo usage', 'استخدام العرض مفقود'],
    PROMO_USAGE_WITHOUT_LEDGER: ['Promo usage without ledger', 'استخدام عرض دون قيد'],
    PROMO_LEDGER_WITHOUT_USAGE: ['Promo ledger without usage', 'قيد عرض دون استخدام'],
    DUPLICATE_PROMO_USAGE: ['Duplicate promo usage', 'استخدام عرض مكرر'],
    REFUND_MATH_MISMATCH: ['Refund arithmetic mismatch', 'عدم تطابق حسابات الاسترداد'],
    REFUND_LEDGER_MISMATCH: ['Refund ledger mismatch', 'عدم تطابق دفتر الاسترداد'],
    MISSING_REFUND_REVERSAL: ['Missing refund reversal', 'عكس الاسترداد مفقود'],
    ORPHAN_ORDER_LEDGER_ENTRY: ['Missing source sub-order', 'الطلب الفرعي المصدر مفقود'],
    ORPHAN_REFUND_LEDGER_ENTRY: ['Missing source refund', 'الاسترداد المصدر مفقود'],
    AGGREGATE_DRIFT: ['Daily projection mismatch', 'عدم تطابق التجميع اليومي'],
}

export function financeIssueLabel(type: string, tr: Translate): string {
    const labels = issueLabels[type]
    return labels ? tr(...labels) : tr('Unclassified issue — investigate', 'مشكلة غير مصنفة — يلزم التحقيق')
}
