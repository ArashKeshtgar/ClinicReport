import { useEffect, useState } from 'react'
import { api, ApiError, type Role, type User } from '../api'

const ROLES: Role[] = ['Typist', 'Physician', 'Admin']

export default function Users() {
  const [users, setUsers] = useState<User[]>([])
  const [adding, setAdding] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const load = () => api.users().then(setUsers)
  useEffect(() => { load() }, [])

  const save = async (u: User) => {
    setError(null)
    try {
      await api.updateUser(u.id, u)
      await load()
    } catch (e) {
      setError(e instanceof ApiError ? Object.values(e.fieldErrors).flat()[0] ?? e.message : 'Not saved.')
      await load()
    }
  }

  const reset = async (u: User) => {
    const pw = window.prompt(`New password for ${u.displayName} (at least 10 characters):`)
    if (!pw) return
    try {
      await api.resetPassword(u.id, pw)
      setError(null)
      window.alert('Password changed.')
    } catch (e) {
      setError(e instanceof ApiError ? Object.values(e.fieldErrors).flat()[0] ?? e.message : 'Not changed.')
    }
  }

  return (
    <>
      <div className="page-head">
        <h1>Users</h1>
        <button className="primary" onClick={() => setAdding(true)}>Add user</button>
      </div>
      <p className="muted lead">Typists fill reports, physicians sign them, administrators manage templates and users.</p>
      {error && <p className="error banner">{error}</p>}
      {adding && <NewUser onDone={() => { setAdding(false); load() }} />}
      <div className="table-wrap card">
        <table className="grid">
          <thead><tr><th>Name</th><th>User name</th><th>Credentials</th><th>Role</th><th>Active</th><th></th></tr></thead>
          <tbody>
            {users.map(u => (
              <tr key={u.id} className={u.isActive ? '' : 'inactive'}>
                <td><input defaultValue={u.displayName} onBlur={e => e.target.value !== u.displayName && save({ ...u, displayName: e.target.value })} aria-label="Name" /></td>
                <td className="mono">{u.userName}</td>
                <td><input defaultValue={u.credentials ?? ''} placeholder="MD, FRCPC" onBlur={e => e.target.value !== (u.credentials ?? '') && save({ ...u, credentials: e.target.value || null })} aria-label="Credentials" /></td>
                <td>
                  <select value={u.role} onChange={e => save({ ...u, role: e.target.value as Role })} aria-label="Role">
                    {ROLES.map(r => <option key={r}>{r}</option>)}
                  </select>
                </td>
                <td><input type="checkbox" checked={u.isActive} onChange={e => save({ ...u, isActive: e.target.checked })} aria-label="Active" /></td>
                <td><button className="small-btn" onClick={() => reset(u)}>Reset password</button></td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </>
  )
}

function NewUser({ onDone }: { onDone: () => void }) {
  const [u, setU] = useState({ userName: '', displayName: '', credentials: '', role: 'Typist' as Role, password: '' })
  const [errors, setErrors] = useState<Record<string, string[]>>({})
  const set = (k: keyof typeof u) => (e: { target: { value: string } }) => setU({ ...u, [k]: e.target.value })
  const save = async () => {
    try {
      await api.createUser({ ...u, credentials: u.credentials || null })
      onDone()
    } catch (e) {
      if (e instanceof ApiError) setErrors(Object.keys(e.fieldErrors).length ? e.fieldErrors : { '': [e.message] })
    }
  }
  const err = (k: string) => errors[k]?.[0] && <span className="error small">{errors[k][0]}</span>
  return (
    <div className="card pad stack">
      <h2>New user</h2>
      <div className="form-grid">
        <label>Full name<input value={u.displayName} onChange={set('displayName')} autoFocus />{err('displayName')}</label>
        <label>User name<input value={u.userName} onChange={set('userName')} autoComplete="off" />{err('userName')}</label>
        <label>Role<select value={u.role} onChange={set('role')}>{ROLES.map(r => <option key={r}>{r}</option>)}</select>{err('role')}</label>
        <label>Credentials<input value={u.credentials} onChange={set('credentials')} placeholder="Printed under the signature" /></label>
        <label>Temporary password<input type="password" value={u.password} onChange={set('password')} autoComplete="new-password" />{err('password')}</label>
      </div>
      {err('')}
      <div className="actions">
        <button className="link" onClick={onDone}>Cancel</button>
        <button className="primary" onClick={save}>Create user</button>
      </div>
    </div>
  )
}
