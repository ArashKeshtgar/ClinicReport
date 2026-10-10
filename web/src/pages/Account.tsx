import { useState, type FormEvent } from 'react'
import { api, ApiError } from '../api'
import { useSession } from '../App'

export default function Account() {
  const { me } = useSession()
  const [current, setCurrent] = useState('')
  const [next, setNext] = useState('')
  const [msg, setMsg] = useState<{ ok: boolean; text: string } | null>(null)

  const submit = async (e: FormEvent) => {
    e.preventDefault()
    try {
      await api.changePassword(current, next)
      setCurrent('')
      setNext('')
      setMsg({ ok: true, text: 'Password changed.' })
    } catch (err) {
      setMsg({ ok: false, text: err instanceof ApiError ? Object.values(err.fieldErrors).flat()[0] ?? err.message : 'Not changed.' })
    }
  }

  return (
    <>
      <div className="page-head"><h1>My account</h1></div>
      <div className="card pad narrow-card stack">
        <div><strong>{me.displayName}</strong>{me.credentials && `, ${me.credentials}`}</div>
        <div className="muted">{me.userName} · {me.role}</div>
        <form onSubmit={submit} className="stack">
          <h2>Change password</h2>
          <label>Current password<input type="password" value={current} onChange={e => setCurrent(e.target.value)} autoComplete="current-password" required /></label>
          <label>New password<input type="password" value={next} onChange={e => setNext(e.target.value)} autoComplete="new-password" minLength={10} required /></label>
          {msg && <p className={msg.ok ? 'notice' : 'error'}>{msg.text}</p>}
          <button className="primary">Change password</button>
        </form>
      </div>
    </>
  )
}
