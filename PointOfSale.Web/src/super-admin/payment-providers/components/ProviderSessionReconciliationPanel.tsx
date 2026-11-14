import { useState } from 'react'
import { AlertTriangle, ShieldAlert } from 'lucide-react'
import { useSuperAdminLocale } from '../../i18n/useSuperAdminLocale'
import { InlineNotice, saInput } from '../../ui/SuperAdminUI'
import type { PaymentProviderSessionReconciliationRequest } from '../paymentProviderTypes'

const guidPattern = /^[0-9a-f]{8}-[0-9a-f]{4}-[1-5][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i

export function ProviderSessionReconciliationPanel({
    submitting,
    disabled,
    onSubmit,
}: {
    submitting: boolean
    disabled: boolean
    onSubmit: (payload: PaymentProviderSessionReconciliationRequest) => void
}) {
    const { tr } = useSuperAdminLocale()
    const [paymentId, setPaymentId] = useState('')
    const [providerReference, setProviderReference] = useState('')
    const [evidenceNote, setEvidenceNote] = useState('')
    const [confirmed, setConfirmed] = useState(false)

    const normalizedPaymentId = paymentId.trim()
    const normalizedProviderReference = providerReference.trim()
    const normalizedEvidenceNote = evidenceNote.trim()
    const valid =
        guidPattern.test(normalizedPaymentId) &&
        normalizedProviderReference.length > 0 &&
        normalizedEvidenceNote.length >= 10 &&
        normalizedEvidenceNote.length <= 500 &&
        confirmed

    function submit() {
        if (!valid || submitting || disabled) {
            return
        }

        onSubmit({
            paymentId: normalizedPaymentId,
            providerReference: normalizedProviderReference,
            evidenceCode: 'PAYMOB_DASHBOARD_NO_ORDER',
            evidenceNote: normalizedEvidenceNote,
            administratorConfirmation: true,
        })
    }

    return (
        <section className="rounded-2xl border border-amber-300 bg-amber-50 p-4 shadow-sm">
            <div className="flex items-start gap-3">
                <span className="rounded-lg bg-amber-100 p-1.5 text-amber-800">
                    <ShieldAlert className="h-4 w-4" />
                </span>
                <div className="min-w-0">
                    <p className="text-[11px] font-semibold uppercase tracking-[0.18em] text-amber-800">
                        {tr('Manual provider reconciliation', 'مطابقة يدوية لمزود الدفع')}
                    </p>
                    <h3 className="mt-1 text-base font-semibold text-slate-950">
                        {tr('Confirm no Paymob session was created', 'تأكيد عدم إنشاء جلسة Paymob')}
                    </h3>
                    <p className="mt-1.5 text-sm leading-6 text-slate-700">
                        {tr(
                            'Use this only for a pending outcome-unknown payment after checking the matching Paymob environment, merchant, Orders page, and Transactions page. This action records operator evidence and closes only an evidence-free session gap; it cannot override a provider transaction or paid payment.',
                            'استخدم هذا فقط لدفعة معلّقة غير معروفة النتيجة بعد فحص بيئة Paymob المطابقة والتاجر وصفحة الطلبات وصفحة الحركات. يسجل هذا الإجراء دليل المشغّل ويغلق فقط فجوة جلسة بلا دليل؛ ولا يمكنه تجاوز حركة مزود أو دفعة مدفوعة.',
                        )}
                    </p>
                </div>
            </div>

            <div className="mt-3 grid gap-3">
                <label className="grid gap-1.5 text-sm font-medium text-slate-800">
                    {tr('Payment ID', 'معرّف الدفعة')}
                    <input
                        value={paymentId}
                        onChange={(event) => setPaymentId(event.target.value)}
                        placeholder="00000000-0000-0000-0000-000000000000"
                        autoComplete="off"
                        dir="ltr"
                        className={`${saInput} font-mono`}
                    />
                </label>
                <label className="grid gap-1.5 text-sm font-medium text-slate-800">
                    {tr('Exact provider reference', 'مرجع المزود الدقيق')}
                    <input
                        value={providerReference}
                        onChange={(event) => setProviderReference(event.target.value)}
                        autoComplete="off"
                        dir="ltr"
                        className={`${saInput} font-mono`}
                    />
                </label>
                <label className="grid gap-1.5 text-sm font-medium text-slate-800">
                    {tr('Dashboard evidence note', 'ملاحظة دليل لوحة المزود')}
                    <textarea
                        value={evidenceNote}
                        onChange={(event) => setEvidenceNote(event.target.value)}
                        rows={3}
                        maxLength={500}
                        placeholder={tr(
                            'Record merchant, environment, pages checked, and the observed result.',
                            'سجّل التاجر والبيئة والصفحات المفحوصة والنتيجة الملاحظة.',
                        )}
                        className={`${saInput} resize-y`}
                    />
                    <span className="text-xs font-normal text-slate-500">
                        {tr(
                            `${normalizedEvidenceNote.length}/500 characters`,
                            `${normalizedEvidenceNote.length}/500 حرفًا`,
                        )}
                    </span>
                </label>
                <label className="flex items-start gap-3 rounded-xl border border-amber-200 bg-white p-3 text-sm text-slate-800">
                    <input
                        type="checkbox"
                        checked={confirmed}
                        onChange={(event) => setConfirmed(event.target.checked)}
                        className="mt-0.5 h-4 w-4 rounded border-slate-300 text-amber-700 focus:ring-amber-500"
                    />
                    <span>
                        {tr(
                            'I checked the correct Paymob Test-mode merchant and found no matching order or transaction for this exact provider reference.',
                            'فحصت تاجر Paymob الصحيح في وضع الاختبار ولم أجد طلبًا أو حركة مطابقة لمرجع المزود هذا تحديدًا.',
                        )}
                    </span>
                </label>
            </div>

            {disabled ? (
                <InlineNotice tone="amber" title={tr('Unsaved configuration', 'إعداد غير محفوظ')}>
                    {tr(
                        'Save or discard provider configuration changes before reconciling a session.',
                        'احفظ تغييرات إعداد المزود أو تجاهلها قبل مطابقة الجلسة.',
                    )}
                </InlineNotice>
            ) : null}

            {!disabled && (paymentId || providerReference || evidenceNote) && !valid ? (
                <div className="mt-3 flex items-start gap-2 text-xs font-medium text-amber-900">
                    <AlertTriangle className="mt-0.5 h-3.5 w-3.5 shrink-0" />
                    {tr(
                        'A valid payment GUID, the exact provider reference, a 10–500 character evidence note, and confirmation are required.',
                        'يلزم معرّف دفعة صالح بصيغة GUID ومرجع المزود الدقيق وملاحظة دليل من 10 إلى 500 حرفًا والتأكيد.',
                    )}
                </div>
            ) : null}

            <div className="mt-3 flex justify-end">
                <button
                    type="button"
                    onClick={submit}
                    disabled={!valid || submitting || disabled}
                    className="inline-flex items-center justify-center gap-2 rounded-xl bg-amber-700 px-4 py-2 text-sm font-semibold text-white shadow-sm transition hover:bg-amber-800 disabled:cursor-not-allowed disabled:bg-slate-300"
                >
                    {submitting
                        ? tr('Reconciling…', 'جارٍ المطابقة…')
                        : tr('Reconcile as not created', 'المطابقة كغير مُنشأة')}
                </button>
            </div>
        </section>
    )
}
