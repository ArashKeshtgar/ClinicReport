import { useCallback, useEffect, useState, type FormEvent } from 'react'
import { api, ApiError, type OperatorEvent, type OperatorTenant } from '../api'

const size = (bytes: number) => bytes >= 1048576 ? `${(bytes / 1048576).toFixed(1)} MB` : `${Math.ceil(bytes / 1024)} KB`
const day = (at: string | null) => at ? new Date(at).toLocaleDateString() : '—'

function SignIn({ onSignedIn }: { onSignedIn: (name: string) => void }) {
  const [userName, setUserName] = useState('')
  const [password, setPassword] = useState('')
  const [code, setCode] = useState('')
  const [error, setError] = useState<string | null>(null)

  const submit = async (e: FormEvent) => {
    e.preventDefault()
    setError(null)
    try {
      onSignedIn((await api.operatorLogin({ userName, password, code })).userName)
    } catch (e) {
      setCode('')
      setError(e instanceof ApiError ? e.message : 'Could not sign in.')
    }
  }

  return (
    <main className="page">
      <form className="card pad stack narrow-card" onSubmit={submit}>
        <h1>Operator console</h1>
        <label>User name<input value={userName} onChange={e => setUserName(e.target.value)} required autoComplete="username" /></label>
        <label>Password<input type="password" value={password} onChange={e => setPassword(e.target.value)} required autoComplete="current-password" /></label>
        <label>Two-factor code<input value={code} onChange={e => setCode(e.target.value)} required inputMode="numeric" autoComplete="one-time-code" /></label>
        {error && <p className="error" role="alert">{error}</p>}
        <button className="primary">Sign in</button>
      </form>
    </main>
  )
}

/** Hosted, at admin.{domain}: the clinics the service runs. Metadata only; nothing here reaches a clinic's patients. */
export default function OperatorConsole() {
  const [name, setName] = useState<string | null | undefined>(undefined)
  const [tenants, setTenants] = useState<OperatorTenant[]>([])
  const [events, setEvents] = useState<OperatorEvent[]>([])
  const [error, setError] = useState<string | null>(null)

  const load = useCallback(async () => {
    const [t, e] = await Promise.all([api.operatorTenants(), api.operatorEvents()])
    setTenants(t)
    setEvents(e)
  }, [])

  useEffect(() => { api.operatorMe().then(m => setName(m.userName)).catch(() => setName(null)) }, [])
  useEffect(() => { if (name) load().catch(() => setError('Could not load the clinics.')) }, [name, load])

  const act = async (action: () => Promise<void>) => {
    setError(null)
    try {
      await action()
      await load()
    } catch (e) {
      setError(e instanceof ApiError ? e.message : 'That did not work.')
    }
  }

  const remove = (t: OperatorTenant) => {
    const typed = window.prompt(`This permanently deletes ${t.name}'s database, with its patients and reports. It cannot be undone. Type ${t.slug} to confirm.`)
    if (typed) void act(() => api.operatorDelete(t.id, typed))
  }

  if (name === undefined) return <div className="center muted">Loading…</div>
  if (name === null) return <SignIn onSignedIn={setName} />
  return (
    <main className="page">
      <div className="page-head">
        <h1>Operator console</h1>
        <span className="muted small">{name}{' '}
          <button className="link" onClick={() => api.operatorLogout().then(() => setName(null))}>Sign out</button>
        </span>
      </div>
      {error && <p className="error" role="alert">{error}</p>}
      <div className="card">
        <table>
          <thead>
            <tr><th>Clinic</th><th>Plan</th><th>Status</th><th>Users</th><th>Signed this month</th><th>Last activity</th><th>Database</th><th /></tr>
          </thead>
          <tbody>
            {tenants.map(t => (
              <tr key={t.id}>
                <td>{t.name}<div className="muted small">{t.slug}</div></td>
                <td>{t.plan}</td>
                <td>{t.deletedAt ? `Deleted ${day(t.deletedAt)}` : t.status}{t.databaseFailed && <div className="error small">Database failed</div>}</td>
                <td>{t.activeUsers ?? '—'}</td>
                <td>{t.signedReports ?? '—'}</td>
                <td>{day(t.lastActivityAt)}</td>
                <td>{size(t.databaseBytes)}</td>
                <td>
                  {!t.deletedAt && t.status !== 'Suspended' && t.status !== 'Cancelled' && <button onClick={() => act(() => api.operatorSuspend(t.id))}>Suspend</button>}
                  {!t.deletedAt && t.status === 'Suspended' && <button onClick={() => act(() => api.operatorResume(t.id))}>Resume</button>}
                  {t.canDelete && <button onClick={() => remove(t)}>Delete data</button>}
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
      <h2>Recent actions</h2>
      <ul className="card pad muted small">
        {events.map((e, i) => <li key={i}>{new Date(e.at).toLocaleString()}: {e.type}{e.tenant ? ` (${e.tenant})` : ''}{e.operator ? ` by ${e.operator}` : ''}{e.detail ? `: ${e.detail}` : ''}</li>)}
      </ul>
    </main>
  )
}
