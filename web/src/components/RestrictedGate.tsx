import { useState } from 'react'

/**
 * Shown instead of a report the clinic has restricted (the patient's consent directive: R restricted, V very
 * restricted). Opening it takes a reason, which is logged with the user's name and shown to the clinic's privacy officer.
 */
export function RestrictedGate({ label, onOpen }: { label: string; onOpen: (reason: string) => Promise<void> }) {
  const [reason, setReason] = useState('')
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const submit = async () => {
    setBusy(true)
    setError(null)
    try { await onOpen(reason.trim()) } catch (e) { setError(e instanceof Error ? e.message : 'Not opened.') } finally { setBusy(false) }
  }
  return (
    <section className="card" role="region" aria-label="Restricted report">
      <h2>{label === 'V' ? 'Very restricted' : 'Restricted'}</h2>
      <p>The patient has a consent directive at the clinic. Open this report only if you are caring for them or have another lawful reason. Your name and your reason are logged.</p>
      <label>Why do you need to open it?
        <textarea value={reason} onChange={e => setReason(e.target.value)} rows={3} maxLength={300} />
      </label>
      {error && <p className="error" role="alert">{error}</p>}
      <button className="primary" disabled={busy || reason.trim().length < 10} onClick={submit}>Open this report</button>
      <p className="muted small">At least 10 characters. The access lasts one hour.</p>
    </section>
  )
}
