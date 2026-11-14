import { useCallback, useEffect, useRef, useState } from 'react'
import { useReactToPrint } from 'react-to-print'
import { useTranslation } from 'react-i18next'
import api, { isRequestCanceled } from '../services/api'
import { useAuthStore } from '../store/authStore'
import { hasPosPermission } from '../auth/posPermissions'
import { useRTL } from '../hooks/useRTL'
import { invalidatePosRuntimeCache } from '../services/posRuntimeCache'

const numberStatus = value => typeof value === 'number' ? value : ['Issued', 'Reversed'].indexOf(value)
const numberKind = value => typeof value === 'number' ? value : ['Receipt', 'Payment'].indexOf(value)

export default function FinancialVouchers() {
    const { t } = useTranslation()
    const { dir } = useRTL()
    const user = useAuthStore(s => s.user)
    const canOperate = hasPosPermission(user, 'finance.operate')
    const canReadJournals = hasPosPermission(user, 'reports.financial.view')
    const [search, setSearch] = useState('')
    const [kind, setKind] = useState('')
    const [page, setPage] = useState(1)
    const [list, setList] = useState(null)
    const [selected, setSelected] = useState(null)
    const [detail, setDetail] = useState(null)
    const [journal, setJournal] = useState(null)
    const [form, setForm] = useState(null)
    const [reversing, setReversing] = useState(false)
    const [reason, setReason] = useState('')
    const [sourceSearch, setSourceSearch] = useState('')
    const [sourcePage, setSourcePage] = useState(1)
    const [sources, setSources] = useState(null)
    const [busy, setBusy] = useState(false)
    const [error, setError] = useState('')
    const [revision, setRevision] = useState(0)
    const submitting = useRef(false)
    const printArea = useRef(null)
    const issuingKind = form ? form.kind : null
    const message = useCallback(e => e?.response?.data?.message || t('financialVouchers.requestFailed'), [t])
    const text = key => t(`financialVouchers.${key}`)
    const printVoucher = useReactToPrint({
        contentRef: printArea,
        documentTitle: detail?.voucher.number || 'Voucher',
        onPrintError: () => setError(t('financialVouchers.printFailed')),
    })

    useEffect(() => {
        const controller = new AbortController(); setList(null)
        api.get('/financial-vouchers', { params: { search, kind: kind || undefined, page }, signal: controller.signal })
            .then(r => { if (!controller.signal.aborted) setList(r.data) }).catch(e => { if (!isRequestCanceled(e)) setError(message(e)) })
        return () => controller.abort()
    }, [search, kind, page, revision, message])
    useEffect(() => {
        const controller = new AbortController(); setDetail(null); setJournal(null)
        if (selected) api.get(`/financial-vouchers/${selected}`, { signal: controller.signal })
            .then(r => { if (!controller.signal.aborted) setDetail(r.data) }).catch(e => { if (!isRequestCanceled(e)) setError(message(e)) })
        return () => controller.abort()
    }, [selected, revision, message])
    useEffect(() => {
        const controller = new AbortController(); setSources(null)
        if (issuingKind !== null) api.get('/financial-vouchers/sources', { params: { kind: issuingKind, search: sourceSearch, page: sourcePage }, signal: controller.signal })
            .then(r => { if (!controller.signal.aborted) setSources(r.data) }).catch(e => { if (!isRequestCanceled(e)) setError(message(e)) })
        return () => controller.abort()
    }, [issuingKind, sourceSearch, sourcePage, message])

    const submit = async event => {
        event.preventDefault(); if (submitting.current) return
        submitting.current = true; setBusy(true); setError('')
        try {
            const result = form ? await api.post('/financial-vouchers', form)
                : await api.post(`/financial-vouchers/${selected}/reverse`, { version: detail.voucher.version, reason })
            setSelected(result.data.id); setForm(null); setReversing(false); setReason(''); setRevision(v => v + 1)
            invalidatePosRuntimeCache({ reason: 'voucher-workflow' })
        } catch (e) {
            setError(message(e)); setReversing(false)
            if (form) {
                try {
                    const recovered = await api.get(`/financial-vouchers/${form.operationId}`)
                    if (recovered.data?.voucher?.id === form.operationId) {
                        setSelected(form.operationId); setForm(null); setError('')
                        invalidatePosRuntimeCache({ reason: 'voucher-workflow-recovered' })
                    }
                }
                catch { /* Keep the original operation ID for an explicit retry, never auto-write. */ }
            }
            setRevision(v => v + 1)
        } finally { submitting.current = false; setBusy(false) }
    }
    const openJournal = async id => {
        const voucherId = selected; setJournal(null)
        try { const r = await api.get(`/financial-vouchers/${voucherId}/journals/${id}`); setJournal({ ...r.data, voucherId }) }
        catch (e) { setError(message(e)) }
    }
    const canReverse = detail && canOperate && hasPosPermission(user, numberKind(detail.voucher.kind) === 0 ? 'customers.balance.manage' : 'suppliers.balance.manage')

    return <main dir={dir} className="p-4 md:p-6 space-y-5">
        <header className="flex flex-wrap justify-between gap-3 print:hidden"><h1 className="text-2xl font-bold">{text('title')}</h1>
            {canOperate && <div className="flex gap-3">{[0, 1].filter(k => hasPosPermission(user, k === 0 ? 'customers.balance.manage' : 'suppliers.balance.manage')).map(k => <button key={k} className="btn-primary" disabled={busy} onClick={() => {
                setForm({ operationId: crypto.randomUUID(), kind: k, sourcePaymentId: '', reason: '' }); setSourceSearch(''); setSourcePage(1); setReversing(false); setError('')
            }}>{text(k === 0 ? 'issueReceipt' : 'issuePayment')}</button>)}</div>}</header>
        <p className="bg-amber-50 p-3 print:hidden">{text('policy')}</p>
        {error && !form && !reversing && <div role="alert" className="p-3 bg-red-50 text-red-800">{error}</div>}
        <div className="flex flex-wrap gap-3 print:hidden">
            <label>{text('search')}<input className="input" value={search} onChange={e => { setSearch(e.target.value); setPage(1) }} /></label>
            <label>{text('kind')}<select className="input" value={kind} onChange={e => { setKind(e.target.value); setPage(1) }}><option value="">{text('all')}</option>{[0, 1].map(k => <option key={k} value={k}>{text(`kind${k}`)}</option>)}</select></label>
            <button className="btn-secondary" disabled={busy} onClick={() => setRevision(v => v + 1)}>{text('reload')}</button>
        </div>
        <div className="print:hidden">{!list ? <p role="status">{text('loading')}</p> : <><div className="overflow-x-auto"><table className="w-full"><thead><tr>{['number', 'kind', 'party', 'amount', 'currency', 'status'].map(k => <th className="text-start p-2" key={k}>{text(k)}</th>)}</tr></thead><tbody>{list.items.map(v => <tr key={v.id} className="border-t"><td className="p-2"><button disabled={busy} className="text-blue-700 underline" onClick={() => setSelected(v.id)}>{v.number}</button></td><td>{text(`kind${numberKind(v.kind)}`)}</td><td>{v.partyName}</td><td>{v.amount}</td><td>{v.currency}</td><td>{text(`status${numberStatus(v.status)}`)}</td></tr>)}</tbody></table></div>
            {!list.items.length && <p>{text('empty')}</p>}<div className="flex gap-3"><button disabled={page === 1 || busy} onClick={() => setPage(v => v - 1)}>{text('previous')}</button><span>{page}</span><button disabled={busy || page * list.pageSize >= list.total} onClick={() => setPage(v => v + 1)}>{text('next')}</button></div></>}</div>
        {detail && <section ref={printArea} dir={dir} className="border rounded p-4 space-y-3" aria-label={text('details')}>
            <h2 className="text-xl font-bold">{detail.voucher.number} — {text(`kind${numberKind(detail.voucher.kind)}`)}</h2>
            <p>{text(`status${numberStatus(detail.voucher.status)}`)} · {detail.voucher.partyName} · {detail.voucher.amount} {detail.voucher.currency}</p>
            <p>{text('paymentDate')}: {detail.voucher.paymentDate}</p><p>{text('source')}: {detail.voucher.sourcePaymentId}</p>
            <p>{text('actor')}: {detail.voucher.createdBy} · {detail.voucher.createdAt}</p><p>{detail.voucher.reason}</p>
            <div className="flex gap-3 print:hidden"><button className="btn-secondary" onClick={() => printVoucher()}>{text('print')}</button>
                {canReverse && numberStatus(detail.voucher.status) === 0 && <button className="btn-secondary" disabled={busy} onClick={() => { setReversing(true); setReason(''); setError('') }}>{text('reverse')}</button>}</div>
            {detail.voucher.reversalReason && <p>{text('reverse')}: {detail.voucher.reversalReason} · {detail.voucher.reversedBy} · {detail.voucher.reversedAtUtc}</p>}
            {['journalId', 'reversalJournalId'].map(key => detail.voucher[key] && <p className="break-all" key={key}>{text(key)}: {canReadJournals ? <button className="text-blue-700 underline" onClick={() => openJournal(detail.voucher[key])}>{detail.voucher[key]}</button> : detail.voucher[key]}</p>)}
            {journal && journal.voucherId === selected && <article className="border p-3"><h3>{journal.journalNumber}</h3><p>{journal.description}</p><ul>{journal.lines.map(l => <li key={l.lineNumber}>{l.accountCode} — {l.accountName}: {l.debitAmount} / {l.creditAmount}</li>)}</ul></article>}
            <h3 className="font-bold">{text('history')}</h3><ol>{detail.history.map(h => <li key={h.id} className="border-b py-2">{text(h.action.toLowerCase())} · {h.createdBy} · {h.createdAt}<p>{h.reason}</p><p className="break-all">{h.journalId}</p></li>)}</ol>
        </section>}
        {(form || reversing) && <div role="dialog" aria-modal="true" aria-labelledby="voucher-dialog-title" className="fixed inset-0 z-50 bg-black/40 flex items-center justify-center p-4 print:hidden"><form onSubmit={submit} className="bg-white rounded p-5 max-w-3xl w-full max-h-[90vh] overflow-y-auto space-y-3">
            <h2 id="voucher-dialog-title" className="text-xl font-bold">{text(form ? numberKind(form.kind) === 0 ? 'issueReceipt' : 'issuePayment' : 'reverse')}</h2>
            {error && <div role="alert" className="bg-red-50 p-3 text-red-800">{error}</div>}
            <p>{text(form ? 'issuePolicy' : 'reversePolicy')}</p>
            {form && <><label className="block">{text('sourceSearch')}<input className="input w-full" disabled={busy} value={sourceSearch} onChange={e => { setSourceSearch(e.target.value); setSourcePage(1); setForm(v => ({ ...v, sourcePaymentId: '' })) }} /></label>
                <label className="block">{text('source')}<select className="input w-full" required disabled={busy || !sources} value={form.sourcePaymentId} onChange={e => setForm(v => ({ ...v, sourcePaymentId: e.target.value }))}><option value="">{text('select')}</option>{sources?.items.map(s => <option key={s.id} value={s.id} disabled={!s.posted}>{s.referenceNumber || s.id} — {s.partyName} — {s.amount} {!s.posted ? text('awaitingAccounting') : ''}</option>)}</select></label>
                <div className="flex gap-3"><button type="button" disabled={sourcePage === 1 || busy} onClick={() => { setSourcePage(v => v - 1); setForm(v => ({ ...v, sourcePaymentId: '' })) }}>{text('previous')}</button><span>{sourcePage}</span><button type="button" disabled={!sources || busy || sourcePage * sources.pageSize >= sources.total} onClick={() => { setSourcePage(v => v + 1); setForm(v => ({ ...v, sourcePaymentId: '' })) }}>{text('next')}</button></div></>}
            <label className="block">{text('reason')}<textarea className="input w-full" required maxLength={1000} disabled={busy} value={form ? form.reason : reason} onChange={e => form ? setForm(v => ({ ...v, reason: e.target.value })) : setReason(e.target.value)} /></label>
            <div className="flex gap-3"><button className="btn-primary" disabled={busy}>{text('confirm')}</button><button type="button" className="btn-secondary" disabled={busy} onClick={() => { setForm(null); setReversing(false) }}>{text('dismiss')}</button></div>
        </form></div>}
    </main>
}
