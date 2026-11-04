import { useEffect, useState, type FormEvent } from 'react'
import { Link } from 'react-router'
import { api, ApiError, type Onboarding, type OnboardingStep } from '../api'
import { useSession } from '../App'

/** Where each onboarding step is done. The email is confirmed from the link (the banner sends a new one). */
const STEP_LINKS: Record<OnboardingStep['key'], string | null> = {
  email: null, letterhead: '/clinic', template: '/templates', invite: '/users', report: '/reports/new',
}

/** Hosted: what a new clinic still has to set up, shown to its administrators until every step is done. */
export function OnboardingChecklist() {
  const [onboarding, setOnboarding] = useState<Onboarding | null>(null)

  useEffect(() => { api.onboarding().then(setOnboarding).catch(() => setOnboarding(null)) }, [])

  if (!onboarding || onboarding.complete) return null
  const done = onboarding.steps.filter(s => s.done).length
  return (
    <section className="card onboarding" aria-label="Getting started">
      <h2>Getting started <span className="muted small">{done} of {onboarding.steps.length} done</span></h2>
      <ol>
        {onboarding.steps.map(s => {
          const link = STEP_LINKS[s.key]
          return (
            <li key={s.key} className={s.done ? 'done' : ''}>
              <span aria-hidden="true">{s.done ? '✓' : '○'}</span>{' '}
              {!s.done && link ? <Link to={link}>{s.title}</Link> : s.title}
              <span className="visually-hidden">{s.done ? ' (done)' : ' (to do)'}</span>
            </li>
          )
        })}
      </ol>
    </section>
  )
}

/** Hosted: the clinic's name and letterhead, printed on every report. */
export default function Clinic() {
  const { settings, refreshSettings } = useSession()
  const [name, setName] = useState(settings.name)
  const [address, setAddress] = useState(settings.address ?? '')
  const [phone, setPhone] = useState(settings.phone ?? '')
  const [reportFooter, setReportFooter] = useState(settings.reportFooter ?? '')
  const [message, setMessage] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  const submit = async (e: FormEvent) => {
    e.preventDefault()
    setBusy(true)
    setMessage(null)
    setError(null)
    try {
      await api.saveClinic({ name, address: address || null, phone: phone || null, reportFooter: reportFooter || null })
      await refreshSettings()
      setMessage('Saved. New reports and prints use it.')
    } catch (e) {
      setError(e instanceof ApiError ? e.message : 'Could not save.')
    } finally {
      setBusy(false)
    }
  }

  return (
    <>
      <div className="page-head"><h1>Clinic</h1></div>
      <form className="card pad stack narrow-card" onSubmit={submit}>
        <p className="muted small">Printed at the top and bottom of every report.</p>
        <label>Name<input value={name} onChange={e => setName(e.target.value)} required maxLength={200} /></label>
        <label>Address<input value={address} onChange={e => setAddress(e.target.value)} maxLength={500} /></label>
        <label>Phone<input value={phone} onChange={e => setPhone(e.target.value)} maxLength={50} /></label>
        <label>Report footer<textarea value={reportFooter} onChange={e => setReportFooter(e.target.value)} maxLength={1000} rows={3} /></label>
        {error && <p className="error" role="alert">{error}</p>}
        {message && <p className="muted" role="status">{message}</p>}
        <button className="primary" disabled={busy}>Save</button>
      </form>
    </>
  )
}
