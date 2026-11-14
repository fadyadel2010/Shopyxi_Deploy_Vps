import { useEffect, useRef, useState } from 'react'
import { useTranslation } from 'react-i18next'
import adminAffiliateApi from '../../../services/adminAffiliateApi'

export default function AdminManualPayoutDialog({ requestId, formatCurrency, onClose, onCommitted }) {
    const { t } = useTranslation()
    const [review, setReview] = useState(null)
    const [error, setError] = useState(null)
    const [cashboxId, setCashboxId] = useState('')
    const [attested, setAttested] = useState(false)
    const [reference, setReference] = useState('')
    const [notes, setNotes] = useState('')
    const [busy, setBusy] = useState(false)
    const inFlight = useRef(false)
    const { i18n } = useTranslation()
    const fail = error => setError({ code: error?.response?.data?.code || (error?.response?.status === 403 ? 'AFFILIATE_MANUAL_ADMIN_REQUIRED' : ''),
        message: error?.response?.data?.message })
    useEffect(() => {
        let active = true
        adminAffiliateApi.reviewManualPayout(requestId).then(data => { if (active) setReview(data) })
            .catch(error => { if (active) fail(error) })
        return () => { active = false }
    }, [requestId])
    const selected = review?.sources?.find(s => s.cashboxId === cashboxId)
    const funded = selected && !!selected.sourceFingerprint && selected.availableFunds >= review.amount
    const confirm = async () => {
        if (inFlight.current || !attested || !funded || review.status !== 'Approved') return
        inFlight.current = true; setBusy(true); setError(null)
        try {
            await adminAffiliateApi.confirmManualPayout(requestId, { cashboxId, amount: review.amount, currency: review.currency,
                destinationFingerprint: review.destinationFingerprint, sourceFingerprint: selected.sourceFingerprint, method: 'Cash', performedManually: true,
                attestation: review.attestation, internalReference: reference || null, notes: notes || null })
            // Re-read the durable receipt; no optimistic Paid transition.
            const saved = await adminAffiliateApi.reviewManualPayout(requestId)
            setReview(saved); setAttested(false); onCommitted?.()
        } catch (error) { fail(error) }
        finally { inFlight.current = false; setBusy(false) }
    }
    const receipt = review?.confirmation
    const errorKey = `adminAffiliateDetail.financialErrors.${error?.code}`
    return <div className="fixed inset-0 bg-black/50 z-50 flex items-center justify-center p-4">
        <section role="dialog" aria-modal="true" aria-labelledby="manual-payout-title" className="bg-white dark:bg-gray-800 rounded-xl p-6 w-full min-w-0 max-w-2xl max-h-[90vh] overflow-auto [overflow-wrap:anywhere] space-y-4">
            <h3 id="manual-payout-title" className="font-bold text-lg">{t('adminAffiliateDetail.manual.title')}</h3>
            <p className="text-sm text-amber-900 bg-amber-50 p-3 rounded">{t('adminAffiliateDetail.manual.warning')}</p>
            {error && <div role="alert" className="p-3 bg-red-50 text-red-800 rounded">
                {i18n.exists(errorKey) ? t(errorKey) : error.message || t('adminAffiliateDetail.toast.actionFailed')}
                {error.code && <details><summary>{t('adminAffiliateDetail.workflow.supportDetails')}</summary><code dir="ltr">{error.code}</code></details>}
            </div>}
            {!review && !error && <p>{t('adminAffiliateDetail.manual.loading')}</p>}
            {review && <>
                <dl className="text-sm space-y-1">
                    <div><dt className="inline font-semibold">{t('adminAffiliateDetail.manual.affiliate')}: </dt><dd className="inline">{review.affiliateName}</dd></div>
                    <div><dt className="inline font-semibold">{t('adminAffiliateDetail.manual.amount')}: </dt><dd className="inline">{formatCurrency(review.amount, review.currency)}</dd></div>
                    <div><dt className="inline font-semibold">{t('adminAffiliateDetail.manual.method')}: </dt><dd className="inline">{t('adminAffiliateDetail.manual.cash')}</dd></div>
                    <div><dt className="inline font-semibold">{t('adminAffiliateDetail.manual.destination')}: </dt><dd className="inline" dir="ltr">{review.destination?.type} — {review.destination?.number}</dd></div>
                    <div><dt className="inline font-semibold">{t('adminAffiliateDetail.manual.status')}: </dt><dd className="inline">{t(`adminAffiliateDetail.statuses.${review.status.toLowerCase()}`, { defaultValue: review.status })}</dd></div>
                </dl>
                {receipt ? <div role="status" className="p-3 bg-green-50 text-green-900 rounded space-y-1">
                    <p>{t('adminAffiliateDetail.manual.recorded')}</p>
                    <p>{t('adminAffiliateDetail.manual.audit')}: <code>{receipt.id}</code></p>
                    <p>{t('adminAffiliateDetail.manual.timestamp')}: <span dir="ltr">{receipt.executedAtUtc}</span></p>
                    <p>{receipt.sourceAccountName} — {receipt.sourceAccountCode}</p>
                    <p>{t('adminAffiliateDetail.manual.actor')}: {receipt.administratorId} ({receipt.administratorRole})</p>
                    {review.journals?.map(j => <p key={j.id}>{t('adminAffiliateDetail.manual.journal')}: {j.journalNumber} <code>{j.id}</code></p>)}
                </div> : <>
                    <p className="text-sm">{t('adminAffiliateDetail.manual.approveFirst')}</p>
                    <label className="block text-sm" htmlFor="manual-source">{t('adminAffiliateDetail.manual.source')}</label>
                    <select id="manual-source" className="input w-full" value={cashboxId} disabled={busy} onChange={e => { setCashboxId(e.target.value); setAttested(false) }}>
                        <option value="">{t('adminAffiliateDetail.manual.selectSource')}</option>
                        {review.sources.map(s => <option key={s.cashboxId} value={s.cashboxId}>{s.cashboxName} — {s.accountCode} — {formatCurrency(s.availableFunds, review.currency)}</option>)}
                    </select>
                    {(!review.sources.length || (selected && !selected.sourceFingerprint)) && <p className="text-amber-800">{t('adminAffiliateDetail.financialErrors.AFFILIATE_MANUAL_SOURCE_INVALID')}</p>}
                    {!review.sources.length && <p className="text-sm">
                        <a className="text-primary-600 underline" href="/treasury/cashboxes">
                            {t('adminAffiliateDetail.manual.openCashbox', 'Open Cashbox management to create and open a cashbox')}
                        </a>
                    </p>}
                    {selected && <p className="text-sm">{t('adminAffiliateDetail.manual.available')}: {formatCurrency(selected.availableFunds, review.currency)} — {selected.cashboxName} ({selected.accountCode})</p>}
                    {selected?.sourceFingerprint && !funded && <p role="alert" className="text-red-700">{t('adminAffiliateDetail.financialErrors.AFFILIATE_MANUAL_SOURCE_UNDERFUNDED')}</p>}
                    <details className="text-sm"><summary>{t('adminAffiliateDetail.manual.commissions')} ({review.eligibleCommissions.length})</summary>
                        {review.eligibleCommissions.map(e => <p key={e.id}><code>{e.orderId}</code> — {formatCurrency(e.commissionAmount, e.currency)}</p>)}
                    </details>
                    <label className="block text-sm" htmlFor="manual-reference">{t('adminAffiliateDetail.manual.reference')}</label>
                    <input id="manual-reference" className="input w-full" maxLength={200} value={reference} disabled={busy} onChange={e => setReference(e.target.value)} />
                    <label className="block text-sm" htmlFor="manual-notes">{t('adminAffiliateDetail.withdrawals.modal.adminNotes')}</label>
                    <textarea id="manual-notes" className="input w-full" maxLength={2000} value={notes} disabled={busy} onChange={e => setNotes(e.target.value)} />
                    {review.status === 'Approved' && <label className="flex gap-2 text-sm font-semibold"><input type="checkbox" checked={attested} disabled={busy || !funded} onChange={e => setAttested(e.target.checked)} />{t('adminAffiliateDetail.manual.attestation')}</label>}
                </>}
            </>}
            <div className="flex gap-3 justify-end">
                <button type="button" className="btn-secondary" disabled={busy} onClick={() => { if (!inFlight.current) onClose() }}>{t('adminAffiliateDetail.actions.cancel')}</button>
                {review?.status === 'Approved' && !receipt && <button type="button" className="btn-primary" disabled={busy || !attested || !funded} onClick={confirm}>{busy ? t('adminAffiliateDetail.actions.processing') : t('adminAffiliateDetail.manual.confirm')}</button>}
            </div>
        </section>
    </div>
}
