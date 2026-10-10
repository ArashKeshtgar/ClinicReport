import { useEffect, useMemo, useState } from 'react'
import { api, ApiError, formatDateTime, type Permission, type PermissionInfo, type RoleInfo, type User } from '../api'
import { useSession } from '../App'

const firstError = (err: unknown, fallback: string) =>
  err instanceof ApiError ? Object.values(err.fieldErrors).flat()[0] ?? err.message : fallback

/** Users & roles: who can sign in, what each role allows, and which services each person works on. */
export default function Users() {
  const [tab, setTab] = useState<'users' | 'roles'>('users')
  const [users, setUsers] = useState<User[]>([])
  const [roles, setRoles] = useState<RoleInfo[]>([])
  const [catalog, setCatalog] = useState<PermissionInfo[]>([])
  const [services, setServices] = useState<string[]>([])
  const { settings } = useSession()

  const load = () => Promise.all([api.users().then(setUsers), api.roles().then(setRoles)])
  useEffect(() => {
    load()
    api.permissionCatalog().then(setCatalog)
    api.services().then(setServices)
  }, [])

  return (
    <>
      <div className="page-head">
        <h1>Users &amp; roles</h1>
        <div className="segmented">
          <button className={tab === 'users' ? 'on' : ''} onClick={() => setTab('users')}>Users ({users.length})</button>
          <button className={tab === 'roles' ? 'on' : ''} onClick={() => setTab('roles')}>Roles ({roles.length})</button>
        </div>
      </div>
      {settings.accountsLocked && <p className="notice">Public demo: you can look around, but accounts, roles and passwords can't be changed here.</p>}
      {tab === 'users'
        ? <UsersTab users={users} roles={roles} services={services} reload={load} />
        : <RolesTab roles={roles} catalog={catalog} reload={load} />}
    </>
  )
}

// ---------------------------------------------------------------- users --

function UsersTab({ users, roles, services, reload }: { users: User[]; roles: RoleInfo[]; services: string[]; reload: () => Promise<unknown> }) {
  const [editing, setEditing] = useState<User | 'new' | null>(null)
  const [flash, setFlash] = useState<string | null>(null)
  const [q, setQ] = useState('')
  const shown = users.filter(u => !q || `${u.displayName} ${u.userName} ${u.roles.join(' ')}`.toLowerCase().includes(q.toLowerCase()))

  return (
    <div className="users-layout">
      <div className="stack">
        <div className="toolbar">
          <input type="search" placeholder="Find a user or role" value={q} onChange={e => setQ(e.target.value)} aria-label="Find user" />
          <button className="primary" onClick={() => { setFlash(null); setEditing('new') }}>Add user</button>
        </div>
        <div className="table-wrap card">
          <table className="grid">
            <thead><tr><th>Name</th><th>Roles</th><th>Services</th><th>Security</th><th>Last sign-in</th></tr></thead>
            <tbody>
              {shown.map(u => (
                <tr key={u.id} className={`clickable ${u.isActive ? '' : 'inactive'} ${editing !== 'new' && editing?.id === u.id ? 'selected' : ''}`}
                  onClick={() => { setFlash(null); setEditing(u) }}>
                  <td><strong>{u.displayName}</strong><div className="muted small mono">{u.userName}</div></td>
                  <td><div className="badges">{u.roles.map(r => <span key={r} className="badge">{r}</span>)}</div></td>
                  <td className="small">{u.services.length ? u.services.join(', ') : <span className="muted">All</span>}</td>
                  <td>
                    <div className="badges">
                      {!u.isActive && <span className="badge">Inactive</span>}
                      {u.isLockedOut && <span className="badge warn">Locked</span>}
                      {u.mustChangePassword && <span className="badge warn" title="Signs in with a temporary password">Temp password</span>}
                      {u.twoFactorEnabled
                        ? <span className="badge ok">2FA</span>
                        : u.twoFactorRequired ? <span className="badge warn">2FA pending</span> : null}
                    </div>
                  </td>
                  <td className="muted small nowrap">{u.lastLoginAt ? formatDateTime(u.lastLoginAt) : 'Never'}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      </div>
      {editing && (
        <UserEditor key={editing === 'new' ? 'new' : editing.id} user={editing === 'new' ? null : editing}
          roles={roles} services={services} flash={flash} onClose={() => setEditing(null)}
          onSaved={async (u, message) => { await reload(); setFlash(message ?? null); setEditing(u) }} />
      )}
    </div>
  )
}

function UserEditor({ user, roles, services, flash, onClose, onSaved }: {
  user: User | null; roles: RoleInfo[]; services: string[]; flash: string | null
  onClose: () => void; onSaved: (u: User | null, message?: string) => Promise<void>
}) {
  const [form, setForm] = useState({
    userName: '', password: '',
    displayName: user?.displayName ?? '', credentials: user?.credentials ?? '',
    roleIds: user?.roleIds ?? [], services: user?.services ?? [], isActive: user?.isActive ?? true,
  })
  const [errors, setErrors] = useState<Record<string, string[]>>({})
  const [msg, setMsg] = useState<{ ok: boolean; text: string } | null>(flash ? { ok: true, text: flash } : null)
  const limited = form.services.length > 0

  const effective = useMemo(() => {
    const perms = new Set<Permission>()
    roles.filter(r => form.roleIds.includes(r.id)).forEach(r => r.permissions.forEach(p => perms.add(p)))
    return [...perms]
  }, [roles, form.roleIds])

  const toggle = <T,>(list: T[], v: T) => list.includes(v) ? list.filter(x => x !== v) : [...list, v]

  const save = async () => {
    setErrors({})
    setMsg(null)
    try {
      const body = { displayName: form.displayName, credentials: form.credentials || null, roleIds: form.roleIds, services: form.services }
      const saved = user
        ? await api.updateUser(user.id, { ...body, isActive: form.isActive })
        : await api.createUser({ ...body, userName: form.userName, password: form.password })
      const text = user ? 'Saved.' : 'User created. Give them the temporary password; they choose their own at first sign-in.'
      setMsg({ ok: true, text })
      await onSaved(saved, text)
    } catch (e) {
      if (e instanceof ApiError && Object.keys(e.fieldErrors).length) setErrors(e.fieldErrors)
      else setMsg({ ok: false, text: firstError(e, 'Not saved.') })
    }
  }

  const action = async (label: string, run: () => Promise<void>, done: string) => {
    if (!window.confirm(label)) return
    try {
      await run()
      setMsg({ ok: true, text: done })
      await onSaved(user, done)
    } catch (e) {
      setMsg({ ok: false, text: firstError(e, 'Failed.') })
    }
  }

  const resetPassword = async () => {
    const pw = window.prompt(`Temporary password for ${user!.displayName} (at least 10 characters). They must change it at next sign-in.`)
    if (!pw) return
    try {
      await api.resetPassword(user!.id, pw)
      setMsg({ ok: true, text: 'Password reset and all their sessions ended.' })
      await onSaved(user, 'Password reset and all their sessions ended.')
    } catch (e) {
      setMsg({ ok: false, text: firstError(e, 'Not changed.') })
    }
  }

  const err = (k: string) => errors[k]?.[0] && <span className="error small">{errors[k][0]}</span>

  return (
    <aside className="card pad stack user-editor">
      <div className="section-head">
        <h2>{user ? user.displayName : 'New user'}</h2>
        <button className="link" onClick={onClose} aria-label="Close">✕</button>
      </div>
      {user && <div className="muted small mono">{user.userName} · created {formatDateTime(user.createdAt)}</div>}

      {!user && (
        <div className="form-grid">
          <label>User name<input value={form.userName} onChange={e => setForm({ ...form, userName: e.target.value })} autoComplete="off" />{err('userName')}</label>
          <label>Temporary password<input type="password" value={form.password} onChange={e => setForm({ ...form, password: e.target.value })} autoComplete="new-password" />{err('password')}</label>
        </div>
      )}
      <div className="form-grid">
        <label>Full name<input value={form.displayName} onChange={e => setForm({ ...form, displayName: e.target.value })} />{err('displayName')}</label>
        <label>Credentials<input value={form.credentials} onChange={e => setForm({ ...form, credentials: e.target.value })} placeholder="MD, FRCPC" /></label>
      </div>

      <fieldset>
        <legend>Roles</legend>
        {roles.map(r => (
          <label key={r.id} className="check">
            <input type="checkbox" checked={form.roleIds.includes(r.id)} onChange={() => setForm({ ...form, roleIds: toggle(form.roleIds, r.id) })} />
            <span><strong>{r.name}</strong>{r.requireTwoFactor && <span className="badge tiny">2FA</span>} <span className="muted small">{r.description}</span></span>
          </label>
        ))}
        {err('roleIds')}
        <div className="muted small">Allows: {effective.length ? effective.join(', ') : 'nothing'}</div>
      </fieldset>

      <fieldset>
        <legend>Services</legend>
        <label className="check"><input type="radio" checked={!limited} onChange={() => setForm({ ...form, services: [] })} /> All services</label>
        <label className="check"><input type="radio" checked={limited} onChange={() => setForm({ ...form, services: services.slice(0, 1) })} /> Only these:</label>
        {limited && (
          <div className="chips">
            {services.map(s => (
              <button key={s} type="button" className={`chip multi ${form.services.includes(s) ? 'on' : ''}`}
                onClick={() => setForm({ ...form, services: toggle(form.services, s) })}>{s}</button>
            ))}
          </div>
        )}
        {err('services')}
      </fieldset>

      {user && (
        <label className="check"><input type="checkbox" checked={form.isActive} onChange={e => setForm({ ...form, isActive: e.target.checked })} /> Active (can sign in)</label>
      )}

      {msg && <p className={msg.ok ? 'notice' : 'error'}>{msg.text}</p>}
      <div className="actions"><button className="primary" onClick={save}>{user ? 'Save' : 'Create user'}</button></div>

      {user && (
        <>
          <hr />
          <h3>Security</h3>
          <div className="security-actions">
            <button onClick={resetPassword}>Reset password</button>
            {user.isLockedOut && <button onClick={() => action(`Unlock ${user.displayName}?`, () => api.unlockUser(user.id), 'Unlocked.')}>Unlock</button>}
            {user.twoFactorEnabled && <button onClick={() => action(`Reset two-factor for ${user.displayName}? Use this when they lost their phone.`, () => api.resetTwoFactor(user.id), 'Two-factor reset; their sessions ended.')}>Reset two-factor</button>}
            <button onClick={() => action(`Sign ${user.displayName} out on every device?`, () => api.revokeSessions(user.id), 'All their sessions ended.')}>Sign out everywhere</button>
          </div>
        </>
      )}
    </aside>
  )
}

// ---------------------------------------------------------------- roles --

function RolesTab({ roles, catalog, reload }: { roles: RoleInfo[]; catalog: PermissionInfo[]; reload: () => Promise<unknown> }) {
  const [editing, setEditing] = useState<RoleInfo | 'new' | null>(null)
  return (
    <div className="users-layout">
      <div className="stack">
        <div className="toolbar"><button className="primary" onClick={() => setEditing('new')}>New role</button></div>
        <div className="role-cards">
          {roles.map(r => (
            <button key={r.id} className={`card role-card ${editing !== 'new' && editing?.id === r.id ? 'on' : ''}`} onClick={() => setEditing(r)}>
              <div className="section-head">
                <strong>{r.name}</strong>
                <div className="badges">
                  {r.isSystem && <span className="badge">Built-in</span>}
                  {r.requireTwoFactor && <span className="badge ok">2FA required</span>}
                </div>
              </div>
              <span className="muted small">{r.description}</span>
              <span className="small">{r.permissions.length} of {catalog.length} permissions · {r.userCount} user{r.userCount === 1 ? '' : 's'}</span>
            </button>
          ))}
        </div>
      </div>
      {editing && (
        <RoleEditor key={editing === 'new' ? 'new' : editing.id} role={editing === 'new' ? null : editing} catalog={catalog}
          onClose={() => setEditing(null)} onSaved={async () => { await reload(); setEditing(null) }} />
      )}
    </div>
  )
}

function RoleEditor({ role, catalog, onClose, onSaved }: { role: RoleInfo | null; catalog: PermissionInfo[]; onClose: () => void; onSaved: () => Promise<void> }) {
  const [form, setForm] = useState({
    name: role?.name ?? '', description: role?.description ?? '',
    permissions: role?.permissions ?? ([] as Permission[]), requireTwoFactor: role?.requireTwoFactor ?? false,
  })
  const [msg, setMsg] = useState<string | null>(null)
  const isAdministrator = role?.isSystem && role.name === 'Administrator'
  const groups = [...new Set(catalog.map(p => p.group))]

  const save = async () => {
    setMsg(null)
    const body = { ...form, description: form.description || null }
    try {
      if (role) await api.updateRole(role.id, body)
      else await api.createRole(body)
      await onSaved()
    } catch (e) {
      setMsg(firstError(e, 'Not saved.'))
    }
  }

  const remove = async () => {
    if (!role || !window.confirm(`Delete the role "${role.name}"?`)) return
    try {
      await api.deleteRole(role.id)
      await onSaved()
    } catch (e) {
      setMsg(firstError(e, 'Not deleted.'))
    }
  }

  return (
    <aside className="card pad stack user-editor">
      <div className="section-head">
        <h2>{role ? role.name : 'New role'}</h2>
        <button className="link" onClick={onClose} aria-label="Close">✕</button>
      </div>
      <div className="form-grid">
        <label>Name<input value={form.name} disabled={role?.isSystem} onChange={e => setForm({ ...form, name: e.target.value })} placeholder="e.g. Echo typist" /></label>
        <label>Description<input value={form.description} onChange={e => setForm({ ...form, description: e.target.value })} /></label>
      </div>
      {isAdministrator && <p className="muted small">Administrator always has every permission, so the clinic can never lock itself out.</p>}
      {groups.map(g => (
        <fieldset key={g}>
          <legend>{g}</legend>
          {catalog.filter(p => p.group === g).map(p => (
            <label key={p.key} className="check perm">
              <input type="checkbox" disabled={isAdministrator} checked={form.permissions.includes(p.key)}
                onChange={() => setForm({ ...form, permissions: form.permissions.includes(p.key) ? form.permissions.filter(x => x !== p.key) : [...form.permissions, p.key] })} />
              <span><strong>{p.label}</strong><span className="muted small"> {p.description}</span></span>
            </label>
          ))}
        </fieldset>
      ))}
      <label className="check">
        <input type="checkbox" checked={form.requireTwoFactor} onChange={e => setForm({ ...form, requireTwoFactor: e.target.checked })} />
        <span><strong>Require two-factor sign-in</strong><span className="muted small"> Members must set up an authenticator app before they can work.</span></span>
      </label>
      {msg && <p className="error">{msg}</p>}
      <div className="actions">
        {role && !role.isSystem && <button className="link" onClick={remove}>Delete role</button>}
        <button className="primary" onClick={save}>{role ? 'Save' : 'Create role'}</button>
      </div>
    </aside>
  )
}
