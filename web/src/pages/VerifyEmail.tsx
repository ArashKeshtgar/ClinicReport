import { useEffect, useState } from 'react'
import { api, ApiError, type Settings } from '../api'
import { Logo } from '../components/Logo'

/** The link from the sign-up email: /verify-email?token=… at the clinic's own address. */
export default function VerifyEmail({ settings, onVerified }: { settings: Settings; onVerified: () => void }) {
  const [state, setState] = useState<'working' | 'done' | string>('working')

  useEffect(() => {
    const token = new URLSearchParams(window.location.search).get('token') ?? ''
    // Confirming twice is harmless: a confirmed clinic answers "verified" to any link.
    api.verifyEmail(token)
      .then(() => { setState('done'); onVerified() })
      .catch(e => setState(e instanceof ApiError ? e.message : 'Could not confirm the email address.'))
    // Once per page: App passes a new onVerified on every render.
  }, []) // eslint-disable-line react-hooks/exhaustive-deps

  return (
    <div className="login">
      <div className="login-card card stack">
        <div className="login-brand"><Logo size={40} /><h1>{settings.name}</h1></div>
        {state === 'working' && <p className="muted">Confirming your email address…</p>}
        {state === 'done' && <p>Your email address is confirmed. You can start writing reports.</p>}
        {state !== 'working' && state !== 'done' && <p className="error" role="alert">{state}</p>}
        <a className="button primary" href="/">Continue</a>
      </div>
    </div>
  )
}

/** Signed in, email not confirmed yet: says why reports cannot be started, and lets an administrator send a new link. */
export function VerifyEmailBanner({ canResend }: { canResend: boolean }) {
  const [message, setMessage] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  const resend = async () => {
    setBusy(true)
    try {
      const r = await api.resendVerification()
      setMessage(r.verified ? 'Already confirmed. Reload the page.' : r.sent ? 'Sent. Check your inbox.' : 'The email could not be sent. Try again later.')
    } catch (e) {
      setMessage(e instanceof ApiError ? e.message : 'The email could not be sent.')
    } finally {
      setBusy(false)
    }
  }

  return (
    <div className="demo-banner" role="note">
      Confirm the clinic's email address to start writing reports: open the link we sent when you signed up.{' '}
      {canResend && !message && <button className="link" disabled={busy} onClick={resend}>Send a new link</button>}
      {message && <strong>{message}</strong>}
    </div>
  )
}
