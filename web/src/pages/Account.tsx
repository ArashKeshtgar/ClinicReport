import { useState, type FormEvent } from 'react'
import { api, ApiError } from '../api'
import { useSession } from '../App'
import { TwoFactorSetup } from '../components/TwoFactorSetup'

type Msg = { ok: boolean; text: string } | null

export default function Account() {
  const { me, refreshMe, settings } = useSession()
  const [current, setCurrent] = useState('')
  const [next, setNext] = useState('')
  const [msg, setMsg] = useState<Msg>(null)
  const [settingUp, setSettingUp] = useState(false)
  const [twoMsg, setTwoMsg] = useState<Msg>(null)
  const [sessionMsg, setSessionMsg] = useState<Msg>(null)

  // Connected: the account (password, two-factor, sessions) is the person's ClinicHub account.
  if (settings.signIn === 'clinichub') return (
    <>
      <div className="page-head"><h1>My account</h1></div>
      <section className="card pad stack" style={{ maxWidth: 560 }}>
        <div><strong>{me.displayName}</strong>{me.credentials && `, ${me.credentials}`}</div>
        <div className="muted small mono">{me.userName}</div>
        <div>Clinic: <strong>{me.clinic}</strong></div>
        <div className="badges">{me.roles.map(r => <span key={r} className="badge">{r}</span>)}</div>
        <div className="muted small">Services: {me.services ? me.services.join(', ') : 'all'}</div>
        <p className="muted">You sign in to ClinicReport with your ClinicHub account. Change your password, set up two-factor sign-in,
          switch clinic or sign out on other devices in ClinicHub.</p>
        {settings.clinicHubUrl && <div><a className="button primary" href={new URL('account', settings.clinicHubUrl).toString()}>Open my ClinicHub account</a></div>}
      </section>
    </>
  )

  const firstError = (err: unknown, fallback: string) =>
    err instanceof ApiError ? Object.values(err.fieldErrors).flat()[0] ?? err.message : fallback

  const changePassword = async (e: FormEvent) => {
    e.preventDefault()
    try {
      refreshMe(await api.changePassword(current, next))
      setCurrent('')
      setNext('')
      setMsg({ ok: true, text: 'Password changed. Your other sessions were signed out.' })
    } catch (err) {
      setMsg({ ok: false, text: firstError(err, 'Not changed.') })
    }
  }

  const disableTwoFactor = async () => {
    const password = window.prompt('Enter your password to turn two-factor sign-in off:')
    if (!password) return
    try {
      refreshMe(await api.twoFactorDisable(password))
      setTwoMsg({ ok: true, text: 'Two-factor sign-in is off.' })
    } catch (err) {
      setTwoMsg({ ok: false, text: firstError(err, 'Not changed.') })
    }
  }

  const revoke = async () => {
    try {
      await api.revokeOtherSessions()
      setSessionMsg({ ok: true, text: 'Signed out on every other browser and device.' })
    } catch (err) {
      setSessionMsg({ ok: false, text: firstError(err, 'Failed.') })
    }
  }

  return (
    <>
      <div className="page-head"><h1>My account</h1></div>
      <div className="account-grid">
        <section className="card pad stack">
          <div><strong>{me.displayName}</strong>{me.credentials && `, ${me.credentials}`}</div>
          <div className="muted">{me.userName}</div>
          <div className="badges">{me.roles.map(r => <span key={r} className="badge">{r}</span>)}</div>
          <div className="muted small">Services: {me.services ? me.services.join(', ') : 'all'}</div>
        </section>

        <section className="card pad stack">
          <h2>Two-factor sign-in</h2>
          {me.twoFactorEnabled ? (
            <>
              <p><span className="badge ok">On</span> A code from your authenticator app is needed at every sign-in.</p>
              {me.twoFactorRequired
                ? <p className="muted small">Your role requires it, so it can't be turned off. If you lose your phone, ask an administrator to reset it.</p>
                : <button onClick={disableTwoFactor}>Turn off</button>}
            </>
          ) : settingUp ? (
            <TwoFactorSetup onEnabled={m => { refreshMe(m); setSettingUp(false); setTwoMsg({ ok: true, text: 'Two-factor sign-in is on.' }) }} />
          ) : (
            <>
              <p className="muted">Protect your account with a code from your phone in addition to your password.</p>
              <button className="primary" onClick={() => setSettingUp(true)}>Set up</button>
            </>
          )}
          {twoMsg && <p className={twoMsg.ok ? 'notice' : 'error'}>{twoMsg.text}</p>}
        </section>

        <form onSubmit={changePassword} className="card pad stack">
          <h2>Change password</h2>
          <label>Current password<input type="password" value={current} onChange={e => setCurrent(e.target.value)} autoComplete="current-password" required /></label>
          <label>New password<input type="password" value={next} onChange={e => setNext(e.target.value)} autoComplete="new-password" minLength={10} required /></label>
          {msg && <p className={msg.ok ? 'notice' : 'error'}>{msg.text}</p>}
          <button className="primary">Change password</button>
        </form>

        <section className="card pad stack">
          <h2>Sessions</h2>
          <p className="muted">Signed in on a shared or lost computer? End every session except this one.</p>
          <button onClick={revoke}>Sign out everywhere else</button>
          {sessionMsg && <p className={sessionMsg.ok ? 'notice' : 'error'}>{sessionMsg.text}</p>}
        </section>
      </div>
    </>
  )
}
