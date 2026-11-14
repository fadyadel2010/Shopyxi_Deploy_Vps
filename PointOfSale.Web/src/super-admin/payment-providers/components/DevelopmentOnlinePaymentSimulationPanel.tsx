import { CheckCircle2, Loader2, ShieldAlert } from 'lucide-react'
import { useState } from 'react'
import { useSuperAdminLocale } from '../../i18n/useSuperAdminLocale'
import type {
    DevelopmentOnlinePaymentSuccessRequest,
    DevelopmentOnlinePaymentSuccessResponse,
} from '../paymentProviderTypes'

interface Props {
    submitting: boolean
    result?: DevelopmentOnlinePaymentSuccessResponse | null
    onSubmit: (paymentId: string, request: DevelopmentOnlinePaymentSuccessRequest) => void
}

export function DevelopmentOnlinePaymentSimulationPanel({ submitting, result, onSubmit }: Props) {
    const { tr } = useSuperAdminLocale()
    const [paymentId, setPaymentId] = useState('')
    const [idempotencyKey, setIdempotencyKey] = useState('')
    const [reason, setReason] = useState('Gate 3R Development online success-path certification')
    const [confirmed, setConfirmed] = useState(false)

    const canSubmit =
        paymentId.trim().length > 0 &&
        idempotencyKey.trim().length >= 8 &&
        reason.trim().length > 0 &&
        confirmed &&
        !submitting

    return (
        <section className="space-y-3 rounded-2xl border border-violet-200 bg-violet-50 p-4 shadow-sm">
            <div className="flex items-start gap-3">
                <div className="rounded-lg bg-violet-100 p-1.5 text-violet-700">
                    <ShieldAlert className="h-4 w-4" />
                </div>
                <div className="min-w-0">
                    <p className="text-[11px] font-semibold uppercase tracking-[0.18em] text-violet-700">
                        {tr('Development only', 'بيئة التطوير فقط')}
                    </p>
                    <h3 className="mt-1 text-base font-semibold text-violet-950">
                        {tr('Online success-path simulation', 'محاكاة مسار النجاح الإلكتروني')}
                    </h3>
                    <p className="mt-1.5 text-sm leading-6 text-violet-900/80">
                        {tr(
                            'Enters the canonical platform payment-success lifecycle for an existing pending payment. This records Development evidence only; it does not claim a Paymob charge, card movement, callback, or external money movement.',
                            'يُدخل دورة نجاح الدفع القياسية لمنصة CommerceHub لدفعة معلّقة قائمة. يسجل هذا دليل بيئة التطوير فقط؛ ولا يدّعي خصمًا من Paymob أو حركة بطاقة أو استدعاءً أو حركة أموال خارجية.',
                        )}
                    </p>
                </div>
            </div>

            <div className="grid gap-2 md:grid-cols-2">
                <input
                    value={paymentId}
                    onChange={(event) => setPaymentId(event.target.value)}
                    placeholder={tr('CommerceHub payment ID', 'معرّف دفعة CommerceHub')}
                    dir="ltr"
                    className="rounded-xl border border-violet-200 bg-white px-3.5 py-2.5 font-mono text-sm outline-none focus:border-violet-400 focus:ring-4 focus:ring-violet-100"
                />
                <input
                    value={idempotencyKey}
                    onChange={(event) => setIdempotencyKey(event.target.value)}
                    placeholder={tr('Stable idempotency key (8–128 chars)', 'مفتاح منع تكرار ثابت (8–128 حرفًا)')}
                    dir="ltr"
                    className="rounded-xl border border-violet-200 bg-white px-3.5 py-2.5 font-mono text-sm outline-none focus:border-violet-400 focus:ring-4 focus:ring-violet-100"
                />
            </div>
            <textarea
                value={reason}
                onChange={(event) => setReason(event.target.value)}
                rows={3}
                placeholder={tr('Certification reason', 'سبب الاعتماد')}
                className="w-full rounded-xl border border-violet-200 bg-white px-3.5 py-2.5 text-sm outline-none focus:border-violet-400 focus:ring-4 focus:ring-violet-100"
            />
            <label className="flex items-start gap-3 rounded-xl border border-violet-200 bg-white px-3.5 py-2.5 text-sm text-violet-950">
                <input
                    type="checkbox"
                    checked={confirmed}
                    onChange={(event) => setConfirmed(event.target.checked)}
                    className="mt-0.5 h-4 w-4 rounded border-violet-300"
                />
                <span>
                    {tr(
                        'I confirm this is a Development simulation with no external Paymob charge or money movement.',
                        'أؤكد أن هذه محاكاة في بيئة التطوير دون خصم Paymob خارجي أو حركة أموال.',
                    )}
                </span>
            </label>
            <button
                type="button"
                disabled={!canSubmit}
                onClick={() =>
                    onSubmit(paymentId.trim(), {
                        idempotencyKey: idempotencyKey.trim(),
                        reason: reason.trim(),
                        administratorConfirmation: true,
                    })
                }
                className="inline-flex items-center gap-2 rounded-xl bg-violet-700 px-4 py-2.5 text-sm font-semibold text-white transition hover:bg-violet-800 disabled:cursor-not-allowed disabled:opacity-50"
            >
                {submitting ? <Loader2 className="h-4 w-4 animate-spin" /> : <CheckCircle2 className="h-4 w-4" />}
                {submitting
                    ? tr('Executing canonical lifecycle…', 'جارٍ تنفيذ الدورة القياسية…')
                    : tr('Simulate accepted online payment', 'محاكاة دفع إلكتروني مقبول')}
            </button>

            {result ? (
                <div className="rounded-xl border border-violet-200 bg-white px-3.5 py-2.5 text-sm text-violet-950">
                    <p className="font-semibold" dir="auto">
                        {result.message}
                    </p>
                    <p className="mt-1 break-all font-mono text-xs text-violet-700" dir="ltr">
                        {result.transactionId}
                    </p>
                    <p className="mt-2 text-[11px] font-semibold uppercase tracking-[0.14em] text-violet-700">
                        {tr(
                            'Development simulation · No external money movement',
                            'محاكاة تطوير · لا حركة أموال خارجية',
                        )}
                    </p>
                </div>
            ) : null}
        </section>
    )
}
