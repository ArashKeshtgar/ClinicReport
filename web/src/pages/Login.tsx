import { useState, type FormEvent } from 'react'
import { api, ApiError, type Me, type Settings } from '../api'
import { Logo } from '../components/Logo'

const DEMO_LABELS: Record<string, { title: string; text: string }> = {
  'demo.typist': { title: 'Typist', text: 'Fills reports while the study is done.' },
  'demo.physician': { title: 'Physician', text: 'Reviews, writes the conclusion and signs.' },
  'demo.admin': { title: 'Administrator', text: 'Templates, users, roles and the audit log.' },
  'demo.echo': { title: 'Echo-only typist', text: 'A typist limited to echocardiography: sees nothing else.' },
}

export default function Login({ settings, onSignedIn }: { settings: Settings; onSignedIn: (me: Me) => void }) {
  const [userName, setUserName] = useState('')
  const [password, setPassword] = useState('')
  const [code, setCode] = useState('')
  const [needCode, setNeedCode] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  const run = async (action: () => Promise<Me>) => {
    setBusy(true)
    setError(null)
    try {
      onSignedIn(await action())
    } catch (e) {
      if (e instanceof ApiError && e.data.twoFactorRequired) {
        // First answer of a two-factor account: ask for the code without showing an error.
        if (needCode) setError(e.message)
        setNeedCode(true)
        return
      }
      setError(e instanceof ApiError ? e.message : 'Could not sign in.')
    } finally {
      setBusy(false)
    }
  }

  const submit = (e: FormEvent) => {
    e.preventDefault()
    run(() => api.login(userName, password, needCode ? code : undefined))
  }

  return (
    <div className="login">
      <div className="login-card card">
        <div className="login-brand"><Logo size={40} /><h1>{settings.name}</h1></div>
        <p className="muted">Structured clinical reporting</p>

        {settings.demo && (
          <section className="demo-box">
            <h2>Try the demo</h2>
            <p className="muted small">Synthetic patients only. Open two windows as typist and physician to see live co-editing.</p>
            <div className="demo-users">
              {settings.demoUsers.map(u => (
                <button key={u} className="demo-user" disabled={busy} onClick={() => run(() => api.demoLogin(u))}>
                  <strong>{DEMO_LABELS[u]?.title ?? u}</strong>
                  <span>{DEMO_LABELS[u]?.text}</span>
                </button>
              ))}
            </div>
          </section>
        )}

        <form onSubmit={submit} className="stack">
          <label>User name<input value={userName} onChange={e => setUserName(e.target.value)} autoComplete="username" autoFocus={!settings.demo} required /></label>
          <label>Password<input type="password" value={password} onChange={e => setPassword(e.target.value)} autoComplete="current-password" required /></label>
          {needCode && (
            <label>Authenticator code
              <input inputMode="numeric" autoComplete="one-time-code" pattern="[0-9 ]{6,7}" maxLength={7} value={code}
                onChange={e => setCode(e.target.value)} autoFocus required placeholder="123 456" />
              <span className="muted small">The 6-digit code from your authenticator app.</span>
            </label>
          )}
          {error && <p className="error" role="alert">{error}</p>}
          <button className="primary" disabled={busy}>Sign in</button>
        </form>
      </div>
      <p className="muted small disclaimer">
        Open-source software, provided without warranty. It is not a certified medical device; each clinic is responsible
        for validating templates, reference ranges and reports before clinical use.
      </p>
    </div>
  )
}

/** Connected installations: one sign-in for every application of the clinic, at ClinicHub. */
export function ClinicHubSignIn({ settings }: { settings: Settings }) {
  const params = new URLSearchParams(window.location.search)
  const error = params.get('signInError')
  const returnUrl = window.location.pathname + (params.has('signInError') ? '' : window.location.search)
  return (
    <div className="login">
      <div className="login-card card">
        <div className="login-brand"><Logo size={40} /><h1>{settings.name}</h1></div>
        <p className="muted">Structured clinical reporting</p>
        <p>This installation signs you in with your clinic's ClinicHub account: the same password and two-factor as the
          rest of the clinic's applications. You choose the clinic there.</p>
        {error && <p className="error" role="alert">{error}</p>}
        <a className="button primary" href={`/api/auth/sso/start?returnUrl=${encodeURIComponent(returnUrl)}`}>Sign in with ClinicHub</a>
      </div>
    </div>
  )
}
