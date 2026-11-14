import { useState } from 'react'
import { superAdminApiClient } from '../core/superAdminApiClient'

interface Result {
    evidenceType: string
    isDevelopmentSimulation: boolean
    externalMoneyMovementConfirmed: boolean
    payout: { payoutId: string; amount: number; currency: string; status: string; reference: string }
}

/** This is not the external-transfer execution UI and never asks an operator to attest money moved. */
export function DevelopmentSettlementPanel() {
    const [vendor, setVendor] = useState('')
    const [earnings, setEarnings] = useState('')
    const [key, setKey] = useState('P520D-20261028-E2E01-')
    const [confirmed, setConfirmed] = useState(false)
    const [pending, setPending] = useState(false)
    const [message, setMessage] = useState('')
    async function execute() {
        setPending(true)
        try {
            const result = await superAdminApiClient.postRaw<Result, { earningIds: string[]; idempotencyKey: string; method: string }>(
                `/api/dev/commercehub/acceptance/vendors/${encodeURIComponent(vendor.trim())}/settle`,
                { earningIds: earnings.split(',').map(value => value.trim()).filter(Boolean), idempotencyKey: key.trim(), method: 'Manual' },
            )
            if (result.evidenceType !== 'DevelopmentSyntheticVendorSettlement' || !result.isDevelopmentSimulation || result.externalMoneyMovementConfirmed)
                throw new Error('Synthetic settlement provenance was rejected.')
            setMessage(`${result.payout.status}: ${result.payout.amount} ${result.payout.currency}. ${result.payout.reference}. No external money movement.`)
        } catch (error) {
            setMessage(error instanceof Error ? error.message : 'Development settlement failed.')
        } finally {
            setPending(false)
        }
    }
    return <section className="space-y-3 rounded-2xl border border-violet-200 bg-violet-50 p-5">
        <h2 className="text-lg font-semibold">Development synthetic vendor settlement</h2>
        <p>Run-owned synthetic funds only. Normal ownership, delivery, settlement delay, balance and accounting gates still apply. This is not proof of a bank transfer or cash handover.</p>
        <label className="block">Test vendor ID<input className="block w-full rounded border p-2" value={vendor} onChange={event => setVendor(event.target.value)} /></label>
        <label className="block">Test earning IDs (comma separated)<input className="block w-full rounded border p-2" value={earnings} onChange={event => setEarnings(event.target.value)} /></label>
        <label className="block">Test settlement idempotency key<input className="block w-full rounded border p-2" value={key} onChange={event => setKey(event.target.value)} /></label>
        <label className="block"><input type="checkbox" checked={confirmed} onChange={event => setConfirmed(event.target.checked)} /> I confirm this records only synthetic Development settlement, with no external transfer.</label>
        <button type="button" className="rounded bg-violet-800 px-4 py-2 text-white disabled:opacity-50" disabled={!confirmed || !vendor.trim() || !earnings.trim() || pending} onClick={execute}>{pending ? 'Processing test settlement…' : 'Execute synthetic test settlement'}</button>
        {message && <p role="status">{message}</p>}
    </section>
}
