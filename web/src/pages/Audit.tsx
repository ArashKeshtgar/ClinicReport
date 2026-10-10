import { useEffect, useState } from 'react'
import { Link } from 'react-router'
import { api, formatDateTime, type AuditRow } from '../api'

const TYPES: Record<string, string> = {
  LoginSucceeded: 'Signed in',
  LoginFailed: 'Sign-in failed',
  LockedOut: 'Account locked',
  Logout: 'Signed out',
  PasswordChanged: 'Changed password',
  PasswordReset: 'Password reset',
  TwoFactorEnabled: 'Two-factor on',
  TwoFactorDisabled: 'Two-factor off',
  TwoFactorReset: 'Two-factor reset',
  SessionsRevoked: 'Sessions ended',
  UserCreated: 'User created',
  UserUpdated: 'User changed',
  UserUnlocked: 'Account unlocked',
  RoleCreated: 'Role created',
  RoleUpdated: 'Role changed',
  RoleDeleted: 'Role deleted',
  ReportViewed: 'Opened report',
  ReportSigned: 'Signed report',
}

const WARN = new Set(['LoginFailed', 'LockedOut', 'PasswordReset', 'TwoFactorReset', 'TwoFactorDisabled'])

/** Who signed in, who changed accounts, and who opened which patient's report. */
export default function Audit() {
  const [rows, setRows] = useState<AuditRow[]>([])
  const [type, setType] = useState('')
  const [from, setFrom] = useState('')
  const [to, setTo] = useState('')
  const [more, setMore] = useState(false)

  useEffect(() => {
    api.audit({ type: type || undefined, from: from || undefined, to: to || undefined }).then(r => { setRows(r); setMore(r.length === 200) })
  }, [type, from, to])

  const loadMore = async () => {
    const next = await api.audit({ type: type || undefined, from: from || undefined, to: to || undefined, before: String(rows[rows.length - 1].id) })
    setRows([...rows, ...next])
    setMore(next.length === 200)
  }

  return (
    <>
      <div className="page-head"><h1>Audit log</h1></div>
      <p className="muted lead">Every sign-in, account and role change, and every time someone opens a patient's report (repeat views within 10 minutes count once). Entries can't be edited or deleted.</p>
      <div className="toolbar">
        <select value={type} onChange={e => setType(e.target.value)} aria-label="Event type">
          <option value="">All events</option>
          {Object.entries(TYPES).map(([k, v]) => <option key={k} value={k}>{v}</option>)}
        </select>
        <label className="inline-label">From<input type="date" value={from} onChange={e => setFrom(e.target.value)} /></label>
        <label className="inline-label">To<input type="date" value={to} onChange={e => setTo(e.target.value)} /></label>
      </div>
      <div className="table-wrap card">
        <table className="grid audit">
          <thead><tr><th>When</th><th>Event</th><th>By</th><th>About</th><th>Details</th><th>IP</th></tr></thead>
          <tbody>
            {rows.map(r => (
              <tr key={r.id}>
                <td className="nowrap muted">{formatDateTime(r.at)}</td>
                <td><span className={`badge ${WARN.has(r.type) ? 'warn' : ''}`}>{TYPES[r.type] ?? r.type}</span></td>
                <td>{r.actor ?? <span className="muted">—</span>}</td>
                <td>
                  {r.target && <div>{r.target}</div>}
                  {r.reportId && <div><Link to={`/reports/${r.reportId}`}>Report #{r.reportId}</Link>{r.patient && <span className="muted"> · {r.patient}</span>}</div>}
                </td>
                <td className="small">{r.detail}</td>
                <td className="mono muted small">{r.ipAddress}</td>
              </tr>
            ))}
            {rows.length === 0 && <tr><td colSpan={6} className="muted">Nothing recorded for this filter.</td></tr>}
          </tbody>
        </table>
      </div>
      {more && <div className="actions"><button onClick={loadMore}>Older entries</button></div>}
    </>
  )
}
