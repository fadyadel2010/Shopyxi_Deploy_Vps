import { useCallback, useEffect, useRef, useState } from 'react'
import { useTranslation } from 'react-i18next'
import api, { isRequestCanceled } from '../services/api'
import { useAuthStore } from '../store/authStore'
import { hasPosPermission } from '../auth/posPermissions'
import { useRTL } from '../hooks/useRTL'
import { invalidatePosRuntimeCache } from '../services/posRuntimeCache'

const statusNumber = value => typeof value === 'number' ? value : ['Draft', 'Posted', 'Cancelled', 'Reversed'].indexOf(value)
const dateToday = () => new Date().toISOString().slice(0, 10)

export default function StockCounts() {
    const { t } = useTranslation()
    const { dir } = useRTL()
    const user = useAuthStore(s => s.user)
    const canCount = hasPosPermission(user, 'inventory.adjust')
    const canPost = hasPosPermission(user, 'inventory.approve') && hasPosPermission(user, 'finance.operate')
    const canReadJournals = hasPosPermission(user, 'reports.financial.view')
    const [search, setSearch] = useState('')
    const [status, setStatus] = useState('')
    const [page, setPage] = useState(1)
    const [list, setList] = useState(null)
    const [selected, setSelected] = useState(null)
    const [detail, setDetail] = useState(null)
    const [counts, setCounts] = useState({})
    const [dirty, setDirty] = useState(false)
    const [warehouses, setWarehouses] = useState([])
    const [accounts, setAccounts] = useState([])
    const [form, setForm] = useState(null)
    const [variantSearch, setVariantSearch] = useState('')
    const [variantPage, setVariantPage] = useState(1)
    const [variants, setVariants] = useState(null)
    const [confirmation, setConfirmation] = useState(null)
    const [reason, setReason] = useState('')
    const [account, setAccount] = useState('')
    const [journal, setJournal] = useState(null)
    const [error, setError] = useState('')
    const [busy, setBusy] = useState(false)
    const [revision, setRevision] = useState(0)
    const submitting = useRef(false)
    const creating = form !== null
    const message = useCallback(e => e?.response?.data?.message || t('stockCounts.requestFailed'), [t])
    const label = key => t(`stockCounts.${key}`)
    const isDraft = detail && statusNumber(detail.session.status) === 0

    useEffect(() => {
        const controller = new AbortController()
        setList(null)
        api.get('/stock-counts', { params: { search, status: status || undefined, page }, signal: controller.signal })
            .then(r => { if (!controller.signal.aborted) setList(r.data) })
            .catch(e => { if (!isRequestCanceled(e)) setError(message(e)) })
        return () => controller.abort()
    }, [search, status, page, revision, message])

    useEffect(() => {
        const controller = new AbortController()
        setDetail(null); setJournal(null); setDirty(false)
        if (selected) api.get(`/stock-counts/${selected}`, { signal: controller.signal }).then(r => {
            if (controller.signal.aborted) return
            setDetail(r.data)
            setCounts(Object.fromEntries(r.data.session.lines.map(l => [l.id, { countedQuantity: l.countedQuantity ?? '', reason: l.varianceReason ?? '' }])))
        }).catch(e => { if (!isRequestCanceled(e)) setError(message(e)) })
        return () => controller.abort()
    }, [selected, revision, message])

    useEffect(() => {
        const controller = new AbortController()
        api.get('/Warehouses', { signal: controller.signal }).then(r => { if (!controller.signal.aborted) setWarehouses((r.data.items || r.data).filter(w => w.isActive !== false)) })
            .catch(e => { if (!isRequestCanceled(e)) setError(message(e)) })
        if (canPost) api.get('/stock-counts/variance-accounts', { signal: controller.signal })
            .then(r => { if (!controller.signal.aborted) setAccounts(r.data) }).catch(e => { if (!isRequestCanceled(e)) setError(message(e)) })
        return () => controller.abort()
    }, [canPost, message])

    useEffect(() => {
        const controller = new AbortController()
        setVariants(null)
        if (creating) api.get('/stock-counts/variants', { params: { search: variantSearch, page: variantPage }, signal: controller.signal })
            .then(r => { if (!controller.signal.aborted) setVariants(r.data) }).catch(e => { if (!isRequestCanceled(e)) setError(message(e)) })
        return () => controller.abort()
    }, [creating, variantSearch, variantPage, message])

    const mutate = async (event, save = false) => {
        event.preventDefault()
        if (submitting.current) return
        submitting.current = true; setBusy(true); setError('')
        try {
            let result
            if (form) result = await api.post('/stock-counts', { ...form, variantIds: Object.keys(form.variantIds) })
            else if (save) result = await api.put(`/stock-counts/${selected}/counts`, { version: detail.session.version,
                lines: detail.session.lines.map(l => ({ lineId: l.id, countedQuantity: counts[l.id].countedQuantity === '' ? null : Number(counts[l.id].countedQuantity), reason: counts[l.id].reason })) })
            else result = await api.post(`/stock-counts/${selected}/${confirmation}`, { version: detail.session.version, reason,
                ...(confirmation === 'post' ? { varianceAccountId: account || null } : {}) })
            setSelected(result.data.id); setForm(null); setConfirmation(null); setReason(''); setDirty(false)
            if (!save) invalidatePosRuntimeCache()
            setRevision(v => v + 1)
        } catch (e) {
            setError(message(e))
            setConfirmation(null)
            if (form) {
                // Read-only recovery of the same operation ID after response loss. Never auto-repeat a write.
                try {
                    await api.get(`/stock-counts/${form.operationId}`)
                    setSelected(form.operationId); setForm(null)
                } catch { /* Keep the same operation ID for an explicit retry. */ }
            }
            setRevision(v => v + 1)
        } finally { submitting.current = false; setBusy(false) }
    }
    const edit = (id, field, value) => { setDirty(true); setCounts(v => ({ ...v, [id]: { ...v[id], [field]: value } })) }
    const inspectJournal = async id => {
        const sessionId = selected
        setJournal(null)
        try { const r = await api.get(`/stock-counts/${sessionId}/journals/${id}`); setJournal({ ...r.data, sessionId }) }
        catch (e) { setError(message(e)) }
    }

    return <main dir={dir} className="p-4 md:p-6 space-y-5">
        <header className="flex flex-wrap justify-between gap-3 items-center"><h1 className="text-2xl font-bold">{label('title')}</h1>
            {canCount && <button className="btn-primary" disabled={busy || dirty} onClick={() => {
                setForm({ operationId: crypto.randomUUID(), warehouseId: '', countDate: dateToday(), variantIds: {}, reason: '' })
                setVariantSearch(''); setVariantPage(1); setConfirmation(null)
            }}>{label('create')}</button>}</header>
        <p className="bg-amber-50 p-3">{label('policy')}</p>
        {error && !form && !confirmation && <div role="alert" className="p-3 bg-red-50 text-red-800">{error}</div>}
        <div className="flex flex-wrap gap-3">
            <label>{label('search')}<input className="input" value={search} onChange={e => { setSearch(e.target.value); setPage(1) }} /></label>
            <label>{label('status')}<select className="input" value={status} onChange={e => { setStatus(e.target.value); setPage(1) }}><option value="">{label('all')}</option>
                {[0, 1, 2, 3].map(s => <option key={s} value={s}>{label(`status${s}`)}</option>)}</select></label>
            <button className="btn-secondary" disabled={busy} onClick={() => setRevision(v => v + 1)}>{label('reload')}</button>
        </div>
        {!list ? <p role="status">{label('loading')}</p> : <>
            <div className="overflow-x-auto"><table className="w-full"><thead><tr>{['number', 'warehouse', 'date', 'status'].map(k => <th className="p-2 text-start" key={k}>{label(k)}</th>)}</tr></thead>
                <tbody>{list.items.map(x => <tr key={x.id} className="border-t"><td className="p-2"><button disabled={busy || dirty} className="underline text-blue-700" onClick={() => setSelected(x.id)}>{x.number}</button></td>
                    <td>{warehouses.find(w => w.id === x.warehouseId)?.name || x.warehouseId}</td><td>{x.countDate}</td><td>{label(`status${statusNumber(x.status)}`)}</td></tr>)}</tbody></table></div>
            {!list.items.length && <p>{label('empty')}</p>}
            <div className="flex gap-3"><button disabled={page === 1 || busy} onClick={() => setPage(v => v - 1)}>{label('previous')}</button><span>{page}</span><button disabled={page * list.pageSize >= list.total || busy} onClick={() => setPage(v => v + 1)}>{label('next')}</button></div>
        </>}
        {detail && <section className="border rounded p-4 space-y-3" aria-label={label('details')}>
            <h2 className="text-xl font-bold">{detail.session.number} · {label(`status${statusNumber(detail.session.status)}`)}</h2>
            <p>{label('snapshot')}: {detail.session.snapshotAtUtc} · {label('date')}: {detail.session.countDate}</p><p>{detail.session.reason}</p>
            <p>{label('actor')}: {detail.session.createdBy} · {detail.session.createdAt}</p>
            {dirty && <p role="status">{label('unsaved')}</p>}
            <form onSubmit={e => mutate(e, true)} className="space-y-3">
                <div className="overflow-x-auto"><table className="w-full"><thead><tr>{['product', 'system', 'reserved', 'damaged', 'counted', 'variance', 'cost', 'value', 'reason'].map(k => <th key={k} className="p-2 text-start">{label(k)}</th>)}</tr></thead>
                    <tbody>{detail.session.lines.map(l => <tr key={l.id} className="border-t">
                        <td className="p-2">{l.productName}<br />{l.sku}<small className="block break-all">{l.movementId}{l.reversalMovementId && <><br />{l.reversalMovementId}</>}</small></td>
                        <td>{l.systemQuantity}</td><td>{l.reservedSnapshot}</td><td>{l.damagedSnapshot}</td>
                        <td>{isDraft && canCount ? <input aria-label={`${label('counted')} ${l.sku}`} className="input w-28" type="number" min="0" max="99999999999999.9999" step="0.0001" disabled={busy} value={counts[l.id]?.countedQuantity ?? ''} onChange={e => edit(l.id, 'countedQuantity', e.target.value)} /> : l.countedQuantity ?? '—'}</td>
                        <td>{isDraft ? counts[l.id]?.countedQuantity === '' ? '—' : Number(counts[l.id]?.countedQuantity) - l.systemQuantity : l.variance}</td><td>{l.unitCost}</td><td>{l.postedValue}</td>
                        <td>{isDraft && canCount ? <input aria-label={`${label('reason')} ${l.sku}`} className="input" maxLength={1000} disabled={busy} value={counts[l.id]?.reason ?? ''} onChange={e => edit(l.id, 'reason', e.target.value)} /> : l.varianceReason}</td>
                    </tr>)}</tbody></table></div>
                {isDraft && canCount && <button className="btn-primary" disabled={busy || !dirty}>{label('save')}</button>}
            </form>
            <div className="flex gap-3">{(isDraft ? [...(canPost ? ['post'] : []), ...(canCount ? ['cancel'] : [])] : statusNumber(detail.session.status) === 1 && canPost ? ['reverse'] : []).map(action =>
                <button className="btn-secondary" key={action} disabled={busy || dirty} onClick={() => { setConfirmation(action); setReason(''); setAccount(detail.session.varianceAccountId || '') }}>{label(action)}</button>)}</div>
            {detail.session.postingReason && <p>{label('post')}: {detail.session.postingReason} · {detail.session.postedBy} · {detail.session.postedAtUtc}</p>}
            {detail.session.reversalReason && <p>{label('reverse')}: {detail.session.reversalReason} · {detail.session.reversedBy} · {detail.session.reversedAtUtc}</p>}
            <div className="flex flex-wrap gap-3">{['journalId', 'reversalJournalId'].map(k => detail.session[k] && <span key={k}>{label(k)}: {canReadJournals ? <button className="text-blue-700 underline" onClick={() => inspectJournal(detail.session[k])}>{detail.session[k]}</button> : detail.session[k]}</span>)}</div>
            {journal && journal.sessionId === selected && <article className="border p-3"><h3>{journal.journalNumber}</h3><p>{journal.description} · {journal.sourceReference} · {journal.createdByUserId}</p><ul>{journal.lines.map(l => <li key={l.lineNumber}>{l.accountCode} — {l.accountName}: {l.debitAmount} / {l.creditAmount}</li>)}</ul></article>}
            <h3 className="font-bold">{label('history')}</h3><ol>{detail.history.map(h => <li className="border-b py-2" key={h.id}>{label(h.action.toLowerCase())} · {h.createdAt} · {h.createdBy}<p>{h.reason}</p></li>)}</ol>
        </section>}
        {(form || confirmation) && <div role="dialog" aria-modal="true" aria-labelledby="count-dialog-title" className="fixed inset-0 z-50 bg-black/40 flex items-center justify-center p-4">
            <form onSubmit={mutate} className="bg-white rounded p-5 max-w-3xl w-full max-h-[90vh] overflow-y-auto space-y-3">
                <h2 id="count-dialog-title" className="text-xl font-bold">{label(form ? 'create' : confirmation)}</h2>
                {error && <div role="alert" className="p-3 bg-red-50 text-red-800">{error}</div>}
                {form ? <>
                    <label className="block">{label('warehouse')}<select className="input w-full" required disabled={busy} value={form.warehouseId} onChange={e => setForm(v => ({ ...v, warehouseId: e.target.value }))}><option value="">{label('select')}</option>{warehouses.map(w => <option key={w.id} value={w.id}>{w.name}</option>)}</select></label>
                    <label className="block">{label('date')}<input className="input" required disabled={busy} type="date" value={form.countDate} onChange={e => setForm(v => ({ ...v, countDate: e.target.value }))} /></label>
                    <label className="block">{label('productSearch')}<input className="input w-full" disabled={busy} value={variantSearch} onChange={e => { setVariantSearch(e.target.value); setVariantPage(1) }} /></label>
                    <p>{label('selected')}: {Object.keys(form.variantIds).length} / 500</p>
                    {variants && <><div className="max-h-52 overflow-y-auto">{variants.items.map(v => <label className="block py-1" key={v.id}><input type="checkbox" disabled={busy || (!form.variantIds[v.id] && Object.keys(form.variantIds).length >= 500)} checked={!!form.variantIds[v.id]} onChange={e => setForm(f => {
                        const ids = { ...f.variantIds }; if (e.target.checked) ids[v.id] = true; else delete ids[v.id]; return { ...f, variantIds: ids }
                    })} /> {v.productName} · {v.sku}</label>)}</div><div className="flex gap-3"><button type="button" disabled={variantPage === 1 || busy} onClick={() => setVariantPage(v => v - 1)}>{label('previous')}</button><span>{variantPage}</span><button type="button" disabled={variantPage * variants.pageSize >= variants.total || busy} onClick={() => setVariantPage(v => v + 1)}>{label('next')}</button></div></>}
                </> : <><p>{label('confirmPolicy')}</p>{confirmation === 'post' && <label className="block">{label('varianceAccount')}<select className="input w-full" disabled={busy} value={account} onChange={e => setAccount(e.target.value)}><option value="">{label('select')}</option>{accounts.map(a => <option key={a.id} value={a.id}>{a.accountCode} — {a.name}</option>)}</select></label>}</>}
                <label className="block">{label('reason')}<textarea className="input w-full" required maxLength={1000} disabled={busy} value={form ? form.reason : reason} onChange={e => form ? setForm(v => ({ ...v, reason: e.target.value })) : setReason(e.target.value)} /></label>
                <div className="flex gap-3"><button className="btn-primary" disabled={busy || (form && !Object.keys(form.variantIds).length)}>{label('confirm')}</button><button type="button" className="btn-secondary" disabled={busy} onClick={() => { setForm(null); setConfirmation(null) }}>{label('dismiss')}</button></div>
            </form>
        </div>}
    </main>
}
