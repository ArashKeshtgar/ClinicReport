import { useEffect, useRef, useState } from 'react'
import { Link, useNavigate } from 'react-router'
import { api, ApiError, can, type TemplateSummary } from '../api'
import { useSession } from '../App'

const BLANK = (key: string, name: string, service: string) => JSON.stringify({
  key, name, service,
  sections: [{
    id: 'findings', title: 'Findings', fields: [
      {
        id: 'overall', label: 'Overall', type: 'choice', required: true,
        options: [
          { value: 'normal', label: 'Normal', phrase: 'The study is normal.' },
          { value: 'abnormal', label: 'Abnormal', phrase: 'The study is abnormal.', abnormal: true },
        ],
      },
      { id: 'comments', label: 'Comments', type: 'textarea' },
    ],
  }],
}, null, 2)

export default function Templates() {
  const { me } = useSession()
  const navigate = useNavigate()
  const [list, setList] = useState<TemplateSummary[] | null>(null)
  const [creating, setCreating] = useState(false)
  const [form, setForm] = useState({ name: '', service: '' })
  const [error, setError] = useState<string | null>(null)
  const file = useRef<HTMLInputElement>(null)
  const isAdmin = can(me, 'templates.design')

  const load = () => api.templates().then(setList)
  useEffect(() => { load() }, [])

  const create = async (json: string) => {
    setError(null)
    try {
      const r = await api.createTemplate(json)
      navigate(`/templates/${r.template.id}`)
    } catch (e) {
      setError(e instanceof ApiError ? e.message : 'Could not create the template.')
    }
  }

  const slug = (s: string) => s.toLowerCase().normalize('NFKD').replace(/[^a-z0-9]+/g, '-').replace(/^-|-$/g, '')

  const importFile = async (f: File | undefined) => {
    if (f) await create(await f.text())
    if (file.current) file.current.value = ''
  }

  const byService = new Map<string, TemplateSummary[]>()
  for (const t of list ?? []) byService.set(t.service, [...(byService.get(t.service) ?? []), t])

  return (
    <>
      <div className="page-head">
        <h1>Templates</h1>
        {isAdmin && (
          <div className="actions">
            <button onClick={() => file.current?.click()}>Import JSON…</button>
            <input ref={file} type="file" accept="application/json,.json" hidden onChange={e => importFile(e.target.files?.[0])} />
            <button className="primary" onClick={() => setCreating(true)}>New template</button>
          </div>
        )}
      </div>
      <p className="muted lead">
        Each service the clinic reports on has a template: its sections, the answers to choose from, the sentence each answer
        writes, and the measurements with their normal ranges. Reports keep the template version they were started on.
      </p>

      {error && <p className="error">{error}</p>}

      {creating && (
        <form className="card pad stack" onSubmit={e => { e.preventDefault(); create(BLANK(slug(form.name), form.name, form.service)) }}>
          <h2>New template</h2>
          <div className="form-grid">
            <label>Name<input value={form.name} onChange={e => setForm({ ...form, name: e.target.value })} placeholder="e.g. Exercise stress test" required autoFocus /></label>
            <label>Service<input value={form.service} onChange={e => setForm({ ...form, service: e.target.value })} placeholder="e.g. Cardiology" required /></label>
          </div>
          <div className="muted small">Key: <span className="mono">{slug(form.name) || '…'}</span> (used in exports; cannot change later)</div>
          <div className="actions">
            <button type="button" className="link" onClick={() => setCreating(false)}>Cancel</button>
            <button className="primary" disabled={!slug(form.name) || !form.service.trim()}>Create and open designer</button>
          </div>
        </form>
      )}

      {list === null ? <p className="muted">Loading…</p> : [...byService].map(([service, items]) => (
        <section key={service}>
          <h2 className="group-title">{service}</h2>
          <div className="template-cards">
            {items.map(t => (
              <div key={t.id} className={`card template-card ${t.isActive ? '' : 'inactive'}`}>
                <div>
                  <strong>{t.name}</strong>
                  <div className="muted small mono">{t.key}</div>
                </div>
                <div className="badges">
                  {t.publishedVersion ? <span className="badge ok">v{t.publishedVersion} published</span> : <span className="badge">Not published</span>}
                  {t.draftVersionId && <span className="badge warn">Draft in progress</span>}
                  {!t.isActive && <span className="badge">Inactive</span>}
                </div>
                <div className="actions">
                  {t.publishedVersionId && <a href={`/api/templates/${t.id}/export`} download className="small-btn">Export JSON</a>}
                  {isAdmin && <Link to={`/templates/${t.id}`} className="small-btn">Design</Link>}
                  {isAdmin && (
                    <button className="small-btn" onClick={() => api.setTemplateActive(t.id, !t.isActive).then(load)}>
                      {t.isActive ? 'Deactivate' : 'Activate'}
                    </button>
                  )}
                </div>
              </div>
            ))}
          </div>
        </section>
      ))}
    </>
  )
}
