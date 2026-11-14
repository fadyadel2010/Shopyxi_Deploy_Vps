import { useEffect, useState } from 'react'
import { KeyRound, X } from 'lucide-react'
import { useSuperAdminLocale } from '../../i18n/useSuperAdminLocale'
import { InlineNotice, saButton, saInput } from '../../ui/SuperAdminUI'
import type { RotatePaymentProviderSecretRequest } from '../paymentProviderTypes'

export function RotateSecretDialog({
    open,
    secretType,
    rotating,
    onClose,
    onRotate,
}: {
    open: boolean
    secretType: string
    rotating: boolean
    onClose: () => void
    onRotate: (payload: RotatePaymentProviderSecretRequest) => void
}) {
    const { tr } = useSuperAdminLocale()
    const [newSecret, setNewSecret] = useState('')
    const [confirmNewSecret, setConfirmNewSecret] = useState('')
    const [reason, setReason] = useState('')
    const [administratorConfirmation, setAdministratorConfirmation] = useState(false)

    useEffect(() => {
        if (!open) {
            setNewSecret('')
            setConfirmNewSecret('')
            setReason('')
            setAdministratorConfirmation(false)
        }
    }, [open])

    if (!open) {
        return null
    }

    const validationErrors = [
        !newSecret.trim() ? tr('New secret is required.', 'السر الجديد مطلوب.') : null,
        newSecret.trim() !== confirmNewSecret.trim()
            ? tr('Confirm new secret must match.', 'يجب أن يطابق تأكيد السر الجديد.')
            : null,
        !administratorConfirmation
            ? tr('Administrator confirmation is required.', 'تأكيد المسؤول مطلوب.')
            : null,
    ].filter((error): error is string => Boolean(error))

    return (
        <div className="fixed inset-0 z-50 flex items-center justify-center bg-slate-950/50 p-4 backdrop-blur-sm">
            <div className="w-full max-w-lg rounded-2xl border border-slate-200 bg-white shadow-2xl">
                <div className="flex items-center justify-between border-b border-slate-100 px-4 py-3.5 sm:px-5">
                    <div className="flex items-center gap-3">
                        <div className="flex h-9 w-9 items-center justify-center rounded-lg bg-emerald-50 text-emerald-700">
                            <KeyRound className="h-4 w-4" />
                        </div>
                        <div>
                            <p className="text-[11px] font-semibold uppercase tracking-[0.18em] text-slate-500">
                                {tr('Rotate secret', 'تدوير السر')}
                            </p>
                            <h3 className="text-base font-semibold text-slate-950" dir="auto">
                                {secretType}
                            </h3>
                        </div>
                    </div>
                    <button
                        type="button"
                        onClick={onClose}
                        className="rounded-lg p-2 text-slate-500 transition hover:bg-slate-100"
                        aria-label={tr('Close rotate secret dialog', 'إغلاق نافذة تدوير السر')}
                    >
                        <X className="h-4 w-4" />
                    </button>
                </div>

                <div className="space-y-3 px-4 py-4 sm:px-5">
                    <InlineNotice tone="amber" title={tr('Write-only credential', 'اعتماد للكتابة فقط')}>
                        {tr(
                            'Existing secret values are never displayed. The new value replaces the active version only after the rotation succeeds.',
                            'لا تُعرض قيم الأسرار الحالية أبدًا. تحل القيمة الجديدة محل الإصدار النشط فقط بعد نجاح التدوير.',
                        )}
                    </InlineNotice>

                    <SecretInput
                        label={tr('New secret', 'السر الجديد')}
                        value={newSecret}
                        onChange={setNewSecret}
                    />
                    <SecretInput
                        label={tr('Confirm new secret', 'تأكيد السر الجديد')}
                        value={confirmNewSecret}
                        onChange={setConfirmNewSecret}
                    />
                    <label className="block space-y-1.5">
                        <span className="text-[10px] font-semibold uppercase tracking-[0.16em] text-slate-500">
                            {tr('Reason', 'السبب')}
                        </span>
                        <textarea
                            value={reason}
                            onChange={(event) => setReason(event.target.value)}
                            rows={3}
                            className={saInput}
                            placeholder={tr(
                                'Why is this rotation required?',
                                'لماذا يلزم هذا التدوير؟',
                            )}
                        />
                    </label>
                    <label className="flex items-start gap-3 rounded-xl border border-slate-200 bg-slate-50 p-3 text-sm text-slate-700">
                        <input
                            type="checkbox"
                            checked={administratorConfirmation}
                            onChange={(event) => setAdministratorConfirmation(event.target.checked)}
                            className="mt-0.5 h-4 w-4 rounded border-slate-300 text-emerald-600 focus:ring-emerald-500"
                        />
                        <span>
                            {tr(
                                'I confirm this rotation is intentional and authorized.',
                                'أؤكد أن هذا التدوير مقصود ومصرّح به.',
                            )}
                        </span>
                    </label>
                    {validationErrors.length > 0 ? (
                        <ul className="space-y-1 rounded-xl border border-amber-200 bg-amber-50 px-3.5 py-2.5 text-sm text-amber-900">
                            {validationErrors.map((error) => (
                                <li key={error}>{error}</li>
                            ))}
                        </ul>
                    ) : null}
                </div>

                <div className="flex justify-end gap-2 border-t border-slate-100 px-4 py-3.5 sm:px-5">
                    <button type="button" onClick={onClose} className={saButton.secondary}>
                        {tr('Cancel', 'إلغاء')}
                    </button>
                    <button
                        type="button"
                        disabled={rotating || validationErrors.length > 0}
                        onClick={() =>
                            onRotate({
                                secretType,
                                newSecret,
                                confirmNewSecret,
                                reason,
                                administratorConfirmation,
                            })
                        }
                        className={saButton.primary}
                    >
                        {rotating ? tr('Rotating…', 'جارٍ التدوير…') : tr('Rotate', 'تدوير')}
                    </button>
                </div>
            </div>
        </div>
    )
}

function SecretInput({
    label,
    value,
    onChange,
}: {
    label: string
    value: string
    onChange: (value: string) => void
}) {
    return (
        <label className="block space-y-1.5">
            <span className="text-[10px] font-semibold uppercase tracking-[0.16em] text-slate-500">{label}</span>
            <input
                type="password"
                value={value}
                autoComplete="new-password"
                dir="ltr"
                onChange={(event) => onChange(event.target.value)}
                className={saInput}
            />
        </label>
    )
}
