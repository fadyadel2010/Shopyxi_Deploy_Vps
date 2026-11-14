import { useState } from 'react'
import { superAdminApiClient } from '../core/superAdminApiClient'

/** Separate opt-in simulation controls. Nothing here confirms a bank transfer. */
export function P520FTerminalPanel() {
    const [run, setRun] = useState('P520F-20261028-TERMINAL01')
    const [vendor, setVendor] = useState('')
    const [earning, setEarning] = useState('')
    const [method, setMethod] = useState('')
    const [key, setKey] = useState('P520F-20261028-TERMINAL01-payout1')
    const [expected, setExpected] = useState('')
    const [target, setTarget] = useState('')
    const [confirmed, setConfirmed] = useState(false)
    const [pending, setPending] = useState(false)
    const [message, setMessage] = useState('')
    async function action(step: string) {
        setPending(true)
        try {
            const base = `/api/dev/commercehub/p520f/${encodeURIComponent(run)}`
            const result = step === 'clock'
                ? await superAdminApiClient.getRaw<unknown>(`${base}/clock`)
                : step === 'advance'
                  ? await superAdminApiClient.postRaw<unknown, { expectedUtc: string; targetUtc: string }>(`${base}/clock/advance`, { expectedUtc: expected, targetUtc: target })
                  : await superAdminApiClient.postRaw<unknown, { earningIds: string[]; payoutMethodId: string; method: string; idempotencyKey: string }>(`${base}/vendors/${vendor}/payout/${step}`, { earningIds: earning.split(',').map(v => v.trim()), payoutMethodId: method, method: 'BankTransfer', idempotencyKey: key })
            setMessage(JSON.stringify(result, (key, value) => /token/i.test(key) ? '[redacted operation token]' : value))
        } catch (error) { setMessage(error instanceof Error ? error.message : 'Simulation rejected') }
        finally { setPending(false) }
    }
    return <section className="space-y-3 rounded-2xl border border-violet-200 bg-violet-50 p-5">
        <h2 className="text-lg font-semibold">P5.20-F run-scoped simulated-time payout</h2>
        <p>Development opt-in only. New clock-enrolled orders only. Real policies remain enforced. These controls never certify external funds or real elapsed time.</p>
        {([['Run identifier', run, setRun], ['Simulation vendor ID', vendor, setVendor], ['Simulation earning IDs', earning, setEarning], ['Simulation payout method ID', method, setMethod], ['Simulation idempotency key', key, setKey], ['Expected virtual UTC', expected, setExpected], ['Target virtual UTC', target, setTarget]] as const).map(([label, value, setter]) => <label className="block" key={label}>{label}<input className="block w-full rounded border p-2" value={value} onChange={e => setter(e.target.value)} /></label>)}
        <label><input type="checkbox" checked={confirmed} onChange={e => setConfirmed(e.target.checked)} /> I confirm internal simulation only; no external transfer.</label>
        <div className="flex flex-wrap gap-2">{([['clock', 'Read virtual clock'], ['advance', 'Advance virtual clock'], ['reserve', 'Reserve simulated payout'], ['review', 'Approve simulated payout'], ['complete', 'Complete internal simulated payout']] as const).map(([step, label]) => <button className="rounded bg-violet-800 px-3 py-2 text-white disabled:opacity-50" key={step} disabled={!confirmed || pending} onClick={() => action(step)}>{label}</button>)}</div>
        <pre className="whitespace-pre-wrap break-all" role="status">{message}</pre>
    </section>
}
