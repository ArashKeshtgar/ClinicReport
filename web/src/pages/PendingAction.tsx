import { useState, type FormEvent } from 'react'
import { api, ApiError, type Me, type Settings } from '../api'
import { Logo } from '../components/Logo'
import { TwoFactorSetup } from '../components/TwoFactorSetup'

/** Shown instead of the app until the user has chosen their own password or set up two-factor sign-in. */
export default function PendingActionPage({ me, settings, onDone, onSignOut }: {
  me: Me; settings: Settings; onDone: (me: Me) => void; onSignOut: () => void
}) {
  return (
    <div className="login">
      <div className="login-card card">
        <div className="login-brand"><Logo size={40} /><h1>{settings.name}</h1></div>
        <p className="muted">Signed in as <strong>{me.displayName}</strong> ({me.userName})</p>
        {me.pendingAction === 'change-password'
          ? <ChoosePassword onDone={onDone} />
          : (
            <section className="stack">
              <h2>Set up two-factor sign-in</h2>
              <p className="muted small">Your role ({me.roles.join(', ')}) requires a code from your phone each time you sign in.</p>
              <TwoFactorSetup onEnabled={onDone} />
            </section>
          )}
        <button className="link" onClick={onSignOut}>Sign out</button>
      </div>
    </div>
  )
}

function ChoosePassword({ onDone }: { onDone: (me: Me) => void }) {
  const [current, setCurrent] = useState('')
  const [next, setNext] = useState('')
  const [again, setAgain] = useState('')
  const [error, setError] = useState<string | null>(null)

  const submit = async (e: FormEvent) => {
    e.preventDefault()
    if (next !== again) return setError('The new passwords differ.')
    try {
      onDone(await api.changePassword(current, next))
    } catch (err) {
      setError(err instanceof ApiError ? Object.values(err.fieldErrors).flat()[0] ?? err.message : 'Not changed.')
    }
  }

  return (
    <form className="stack" onSubmit={submit}>
      <h2>Choose your own password</h2>
      <p className="muted small">Your administrator set a temporary password. Replace it with one only you know: at least 10 characters, not containing your user name.</p>
      <label>Temporary password<input type="password" value={current} onChange={e => setCurrent(e.target.value)} autoComplete="current-password" required /></label>
      <label>New password<input type="password" value={next} onChange={e => setNext(e.target.value)} autoComplete="new-password" minLength={10} required /></label>
      <label>New password again<input type="password" value={again} onChange={e => setAgain(e.target.value)} autoComplete="new-password" minLength={10} required /></label>
      {error && <p className="error">{error}</p>}
      <button className="primary">Save and continue</button>
    </form>
  )
}
