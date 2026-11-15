import { useEffect, useRef, useState } from 'react'
import { useTranslation } from 'react-i18next'
import adminAffiliateApi from '../../../services/adminAffiliateApi'

export default function AdminManualPayoutReturnDialog({ requestId, formatCurrency, onClose, onCommitted }) {
    const { t } = useTranslation()
    const [review, setReview] = useState(null)
    const [reference, setReference] = useState('')
    const [reason, setReason] = useState('')
    const [attested, setAttested] = useState(false)
    const [busy, setBusy] = useState(false)
    const [error, setError] = useState(null)
    const inFlight = useRef(false)
    const title = key => t(`adminAffiliateDetail.manualReturn.${key}`)
    useEffect(() => {
        let active = true
        setReview(null); setError(null); setAttested(false); setReference(''); setReason('')
        adminAffiliateApi.reviewManualPayoutReturn(requestId).then(data => { if (active) setReview(data) })
            .catch(() => { if (active) setError(title('loadFailed')) })
        return () => { active = false }
        // The parent keys this dialog by request; no stale request authority is reused.
        // eslint-disable-next-line react-hooks/exhaustive-deps
    }, [requestId])
    const canConfirm = review?.requestId === requestId && review.canReturn && !!review.reviewFingerprint &&
        attested && !!reference.trim() && !!reason.trim() && !review.receipt
    const confirm = async () => {
        if (!canConfirm || inFlight.current) return
        inFlight.current = true; setBusy(true); setError(null)
        try {
            const committed = await adminAffiliateApi.confirmManualPayoutReturn(requestId, {
                reviewFingerprint: review.reviewFingerprint, receivedManually: true, attestation: review.attestation,
                receiptReference: reference.trim(), reason: reason.trim(),
            })
            const saved = await adminAffiliateApi.reviewManualPayoutReturn(requestId)
            if (saved.status !== 'Returned' || saved.receipt?.id !== committed.returnId) throw new Error('Unconfirmed receipt')
            setReview(saved); setAttested(false); onCommitted?.()
        } catch { setError(title('unconfirmed')) }
        finally { inFlight.current = false; setBusy(false) }
    }
    return <div className="fixed inset-0 bg-black/50 z-50 flex items-center justify-center p-4">
        <section role="dialog" aria-modal="true" aria-labelledby="manual-payout-return-title" className="bg-white dark:bg-gray-800 rounded-xl p-6 w-full max-w-xl max-h-[90vh] overflow-auto space-y-4">
            <h3 id="manual-payout-return-title" className="font-bold text-lg">{title('title')}</h3>
            <p className="text-sm text-amber-900 bg-amber-50 p-3 rounded">{title('warning')}</p>
            {error && <p role="alert" className="text-red-700">{error}</p>}
            {!review && !error && <p>{title('loading')}</p>}
            {review && <>
                <p>{formatCurrency(review.amount, review.currency)} — {review.sourceAccountCode}</p>
                {review.receipt ? <div role="status" className="text-green-800 space-y-1">
                    <p>{title('recorded')}</p><code>{review.receipt.id}</code>
                    <p>{review.receipt.recordedAtUtc}</p><p>{review.receipt.receiptReference}</p><p>{review.receipt.reason}</p>
                </div> : review.canReturn ? <>
                    <label className="block" htmlFor="payout-return-reference">{title('reference')}</label>
                    <input id="payout-return-reference" className="input w-full" value={reference} maxLength={200} disabled={busy} onChange={e => setReference(e.target.value)} />
                    <label className="block" htmlFor="payout-return-reason">{title('reason')}</label>
                    <textarea id="payout-return-reason" className="input w-full" value={reason} maxLength={1000} disabled={busy} onChange={e => setReason(e.target.value)} />
                    <label className="flex gap-2 text-sm font-semibold"><input type="checkbox" checked={attested} disabled={busy} onChange={e => setAttested(e.target.checked)} />{title('attestation')}</label>
                </> : <p>{title('unavailable')}</p>}
            </>}
            <div className="flex justify-end gap-3">
                <button type="button" className="btn-secondary" disabled={busy} onClick={() => { if (!inFlight.current) onClose() }}>{t('adminAffiliateDetail.actions.cancel')}</button>
                {review?.canReturn && !review.receipt && <button type="button" className="btn-primary" disabled={busy || !canConfirm} onClick={confirm}>{busy ? t('adminAffiliateDetail.actions.processing') : title('confirm')}</button>}
            </div>
        </section>
    </div>
}
