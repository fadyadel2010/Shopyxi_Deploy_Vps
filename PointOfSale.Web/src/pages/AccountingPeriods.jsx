import { useCallback, useEffect, useRef, useState } from 'react'
import { Link } from 'react-router-dom'
import { useTranslation } from 'react-i18next'
import api, { isRequestCanceled } from '../services/api'
import { useAuthStore } from '../store/authStore'
import { hasPosPermission } from '../auth/posPermissions'
import { useRTL } from '../hooks/useRTL'

const statusNumber = value => typeof value === 'number' ? value : ['Draft', 'Open', 'Closed'].indexOf(value)
const emptyForm = { code: '', name: '', startDate: '', endDate: '', open: true, reason: '' }

export default function AccountingPeriods() {
    const { t } = useTranslation()
    const { dir } = useRTL()
    const user = useAuthStore(state => state.user)
    const canManage = hasPosPermission(user, 'finance.admin')
    const canReadJournals = hasPosPermission(user, 'reports.financial.view')
    const [search, setSearch] = useState('')
    const [status, setStatus] = useState('')
    const [page, setPage] = useState(1)
    const [list, setList] = useState(null)
    const [selected, setSelected] = useState(null)
    const [detail, setDetail] = useState(null)
    const [journals, setJournals] = useState(null)
    const [journalPage, setJournalPage] = useState(1)
    const [journal, setJournal] = useState(null)
    const [form, setForm] = useState(null)
    const [confirmation, setConfirmation] = useState(null)
    const [reason, setReason] = useState('')
    const [error, setError] = useState('')
    const [busy, setBusy] = useState(false)
    const [revision, setRevision] = useState(0)
    const submitting = useRef(false)
    const message = useCallback(e => e?.response?.data?.message || t('accountingPeriods.requestFailed'), [t])
    const statusLabel = value => t(`accountingPeriods.status${statusNumber(value)}`)

    useEffect(() => {
        const controller = new AbortController()
        setList(null)
        api.get('/accounting-periods', { params: { search, status: status || undefined, page }, signal: controller.signal })
            .then(r => { if (!controller.signal.aborted) setList(r.data) })
            .catch(e => { if (!isRequestCanceled(e)) setError(message(e)) })
        return () => controller.abort()
    }, [search, status, page, revision, message])

    useEffect(() => {
        const controller = new AbortController()
        setDetail(null)
        setJournals(null)
        setJournal(null)
        if (!selected) return () => controller.abort()
        Promise.all([
            api.get(`/accounting-periods/${selected}`, { signal: controller.signal }),
            canReadJournals ? api.get(`/accounting-periods/${selected}/journals`, { params: { page: journalPage }, signal: controller.signal }) : Promise.resolve({ data: null }),
        ]).then(([d, j]) => {
            if (!controller.signal.aborted) { setDetail(d.data); setJournals(j.data) }
        }).catch(e => { if (!isRequestCanceled(e)) setError(message(e)) })
        return () => controller.abort()
    }, [selected, journalPage, revision, message, canReadJournals])

    const submit = async event => {
        event.preventDefault()
        if (submitting.current) return
        submitting.current = true
        setBusy(true)
        setError('')
        try {
            const result = form
                ? await api.post('/accounting-periods', form)
                : await api.post(`/accounting-periods/${selected}/${confirmation}`, { version: detail.period.version, reason })
            setSelected(result.data.id)
            setJournalPage(1)
            setForm(null)
            setConfirmation(null)
            setReason('')
            setRevision(value => value + 1)
        } catch (e) {
            setError(message(e))
            // A conflict or lost response requires reloading; never auto-resubmit.
            setConfirmation(null)
            setRevision(value => value + 1)
        } finally { submitting.current = false; setBusy(false) }
    }

    const openJournal = async id => {
        setJournal(null)
        try {
            const r = await api.get(`/accounting-periods/${selected}/journals/${id}`)
            setJournal(r.data)
        } catch (e) { setError(message(e)) }
    }

    return <main dir={dir} className="p-4 md:p-6 space-y-5">
        <header className="flex flex-wrap justify-between gap-3 items-center">
            <h1 className="text-2xl font-bold">{t('accountingPeriods.title')}</h1>
            {canManage && <button className="btn-primary" disabled={busy} onClick={() => { setForm({ ...emptyForm }); setConfirmation(null) }}>{t('accountingPeriods.create')}</button>}
        </header>
        <p>{t('accountingPeriods.policy')}</p>
        {error && <div role="alert" className="p-3 bg-red-50 text-red-800">{error}</div>}
        <div className="flex flex-wrap gap-3">
            <label>{t('accountingPeriods.search')}<input className="input" value={search} onChange={e => { setSearch(e.target.value); setPage(1) }} /></label>
            <label>{t('accountingPeriods.status')}<select className="input" value={status} onChange={e => { setStatus(e.target.value); setPage(1) }}>
                <option value="">{t('accountingPeriods.all')}</option>
                {[0, 1, 2].map(s => <option key={s} value={s}>{statusLabel(s)}</option>)}
            </select></label>
            <button className="btn-secondary" onClick={() => { setError(''); setRevision(v => v + 1) }}>{t('accountingPeriods.reload')}</button>
        </div>
        {!list ? <p role="status">{t('accountingPeriods.loading')}</p> : <>
            <div className="overflow-x-auto"><table className="w-full text-start"><thead><tr>
                {['code', 'name', 'startDate', 'endDate', 'status'].map(k => <th className="p-2 text-start" key={k}>{t(`accountingPeriods.${k}`)}</th>)}
            </tr></thead><tbody>{list.items.map(p => <tr key={p.id} className="border-t">
                <td className="p-2"><button className="text-blue-700 underline" onClick={() => { setSelected(p.id); setJournalPage(1) }}>{p.code}</button></td>
                <td>{p.name}</td><td>{p.startDate}</td><td>{p.endDate}</td><td>{statusLabel(p.status)}</td>
            </tr>)}</tbody></table></div>
            {list.items.length === 0 && <p>{t('accountingPeriods.empty')}</p>}
            <div className="flex gap-3"><button disabled={page === 1} onClick={() => setPage(v => v - 1)}>{t('accountingPeriods.previous')}</button><span>{page}</span><button disabled={page * list.pageSize >= list.total} onClick={() => setPage(v => v + 1)}>{t('accountingPeriods.next')}</button></div>
        </>}
        {detail && <section className="border rounded p-4 space-y-3" aria-label={t('accountingPeriods.details')}>
            <h2 className="text-xl font-bold">{detail.period.code} — {detail.period.name}</h2>
            <p>{detail.period.startDate} — {detail.period.endDate} · {statusLabel(detail.period.status)}</p>
            {statusNumber(detail.period.status) === 2 && <p role="status" className="bg-amber-50 p-3">{t('accountingPeriods.closedWarning')}</p>}
            <p>{t('accountingPeriods.reason')}: {detail.period.reason}</p>
            <p>{t('accountingPeriods.journals')}: {detail.totals.journalCount} · {t('accountingPeriods.debit')}: {detail.totals.totalDebits} · {t('accountingPeriods.credit')}: {detail.totals.totalCredits}</p>
            {canReadJournals && <Link className="text-blue-700 underline" to={`/treasury/general-ledger?from=${detail.period.startDate}&to=${detail.period.endDate}`}>{t('accountingPeriods.report')}</Link>}
            {canManage && <div className="flex flex-wrap gap-3">{[statusNumber(detail.period.status) === 0 ? 'open' : statusNumber(detail.period.status) === 1 ? 'close' : 'reopen'].map(action =>
                <button key={action} disabled={busy} className="btn-secondary" onClick={() => { setConfirmation(action); setForm(null); setReason('') }}>{t(`accountingPeriods.${action}`)}</button>)}</div>}
            <h3 className="font-bold">{t('accountingPeriods.history')}</h3>
            <ol className="space-y-2">{detail.history.map(h => <li key={h.id} className="border-b py-2"><strong>{t(`accountingPeriods.${h.action.toLowerCase()}`)}</strong> · {h.createdAt} · <span className="break-all">{h.createdBy}</span><p>{h.reason}</p></li>)}</ol>
            <h3 className="font-bold">{t('accountingPeriods.journals')}</h3>
            {journals && <><div className="overflow-x-auto"><table className="w-full"><tbody>{journals.items.map(j => <tr key={j.id} className="border-t">
                <td className="p-2"><button className="text-blue-700 underline" onClick={() => openJournal(j.id)}>{j.journalNumber}</button></td>
                <td>{j.journalDate.slice(0, 10)}</td><td>{j.sourceType} · {j.sourceReference}</td><td>{j.debit} / {j.credit}</td>
            </tr>)}</tbody></table></div><div className="flex gap-3"><button disabled={journalPage === 1} onClick={() => setJournalPage(v => v - 1)}>{t('accountingPeriods.previous')}</button><span>{journalPage}</span><button disabled={journalPage * 50 >= journals.total} onClick={() => setJournalPage(v => v + 1)}>{t('accountingPeriods.next')}</button></div></>}
            {journal && <article className="border p-3 break-words"><h4>{journal.journalNumber}</h4><p>{journal.description}</p><p>{journal.sourceType} · {journal.sourceReference} · {journal.sourceId}</p><p>{t('accountingPeriods.actor')}: {journal.createdByUserId}</p><ul>{journal.lines.map(l => <li key={l.lineNumber}>{l.accountCode} — {l.accountName}: {l.debitAmount} / {l.creditAmount}</li>)}</ul></article>}
        </section>}
        {(form || confirmation) && <div role="dialog" aria-modal="true" aria-labelledby="period-dialog-title" className="fixed inset-0 z-50 bg-black/40 flex items-center justify-center p-4">
            <form onSubmit={submit} className="bg-white rounded p-5 max-w-xl w-full max-h-[90vh] overflow-y-auto space-y-3">
                <h2 id="period-dialog-title" className="text-xl font-bold">{t(`accountingPeriods.${form ? 'create' : confirmation}`)}</h2>
                {form && <>{['code', 'name', 'startDate', 'endDate'].map(key => <label className="block" key={key}>{t(`accountingPeriods.${key}`)}<input className="input w-full" required type={key.endsWith('Date') ? 'date' : 'text'} maxLength={key === 'code' ? 50 : key === 'name' ? 200 : undefined} value={form[key]} onChange={e => setForm(v => ({ ...v, [key]: e.target.value }))} /></label>)}
                    <label className="block"><input type="checkbox" checked={form.open} onChange={e => setForm(v => ({ ...v, open: e.target.checked }))} /> {t('accountingPeriods.openImmediately')}</label></>}
                {confirmation && <p>{t('accountingPeriods.confirmPolicy')}</p>}
                <label className="block">{t('accountingPeriods.reason')}<textarea className="input w-full" required maxLength={1000} value={form ? form.reason : reason} onChange={e => form ? setForm(v => ({ ...v, reason: e.target.value })) : setReason(e.target.value)} /></label>
                <div className="flex gap-3"><button className="btn-primary" disabled={busy}>{t('accountingPeriods.confirm')}</button><button type="button" className="btn-secondary" disabled={busy} onClick={() => { setForm(null); setConfirmation(null) }}>{t('accountingPeriods.cancel')}</button></div>
            </form>
        </div>}
    </main>
}
