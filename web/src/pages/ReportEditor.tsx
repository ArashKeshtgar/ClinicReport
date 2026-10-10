import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { Link, useParams } from 'react-router'
import { ageOn, api, ApiError, can, formatDateTime, type Change, type Header, type Report } from '../api'
import { useSession } from '../App'
import { FieldInput, isNumberText, type FieldContext } from '../components/FieldInput'
import { ReportView } from '../components/ReportView'
import { useReportLive } from '../live'
import { allFields, visibleFields, type FieldDef, type Value, type Values } from '../template'

type SaveState = 'saved' | 'saving' | 'error'

export default function ReportEditor() {
  const id = Number(useParams().id)
  const { me } = useSession()
  const [report, setReport] = useState<Report | null>(null)
  const [values, setValues] = useState<Values>({})
  const [conclusion, setConclusion] = useState('')
  const [fieldErrors, setFieldErrors] = useState<Record<string, string>>({})
  const [saveState, setSaveState] = useState<SaveState>('saved')
  const [error, setError] = useState<string | null>(null)
  const [showHistory, setShowHistory] = useState(false)

  // Answers typed here but not sent yet. They win over whatever the server sends back until they are sent.
  const pending = useRef(new Map<string, Value | null>())
  const inflight = useRef(0)
  const revision = useRef(-1)
  const flushTimer = useRef<number | undefined>(undefined)
  const conclusionEdits = useRef(0)
  const conclusionTimer = useRef<number | undefined>(undefined)

  /** Takes a report from the server (our save, someone else's, or a reload) without losing local typing. */
  const accept = useCallback((r: Report) => {
    if (r.revision < revision.current) return // an older response overtaken by a newer one
    revision.current = r.revision
    setReport(r)
    setValues(() => {
      const v: Values = { ...r.values }
      for (const [k, val] of pending.current) {
        if (val == null) delete v[k]
        else v[k] = val
      }
      return v
    })
    if (conclusionEdits.current === 0) setConclusion(r.conclusion ?? '')
  }, [])

  const reload = useCallback(() => {
    api.report(id).then(accept).catch(e => setError(e.message))
  }, [id, accept])

  useEffect(() => {
    revision.current = -1
    pending.current.clear()
    reload()
  }, [reload])

  const live = useReportLive(id, accept, reload)

  const flush = useCallback(async () => {
    window.clearTimeout(flushTimer.current)
    if (pending.current.size === 0) return
    const batch = Object.fromEntries(pending.current)
    pending.current.clear()
    inflight.current++
    setSaveState('saving')
    try {
      const r = await api.setValues(id, batch)
      setFieldErrors(e => {
        const next = { ...e }
        for (const k of Object.keys(batch)) delete next[k]
        return next
      })
      accept(r)
      setSaveState(pending.current.size || inflight.current > 1 ? 'saving' : 'saved')
    } catch (e) {
      const message = e instanceof Error ? e.message : 'Not saved.'
      setFieldErrors(errs => ({ ...errs, ...Object.fromEntries(Object.keys(batch).map(k => [k, message])) }))
      setSaveState('error')
      if (e instanceof ApiError && e.status === 409) reload() // signed meanwhile
    } finally {
      inflight.current--
    }
  }, [id, accept, reload])

  // Send whatever is left when leaving the page.
  useEffect(() => () => { flush() }, [flush])
  useEffect(() => {
    const warn = (e: BeforeUnloadEvent) => {
      if (pending.current.size || inflight.current || conclusionEdits.current) e.preventDefault()
    }
    window.addEventListener('beforeunload', warn)
    return () => window.removeEventListener('beforeunload', warn)
  }, [])

  const onChange = useCallback((field: FieldDef, value: Value | null) => {
    setValues(v => {
      const next = { ...v }
      if (value == null) delete next[field.id]
      else next[field.id] = value
      return next
    })
    if (field.type === 'number' && typeof value === 'string' && !isNumberText(value)) {
      pending.current.delete(field.id)
      setFieldErrors(e => ({ ...e, [field.id]: 'Enter a number, e.g. 42 or 3.5' }))
      return
    }
    setFieldErrors(e => {
      if (!(field.id in e)) return e
      const next = { ...e }
      delete next[field.id]
      return next
    })
    pending.current.set(field.id, value)
    setSaveState('saving')
    window.clearTimeout(flushTimer.current)
    // Clicks save at once; typing waits for a pause.
    flushTimer.current = window.setTimeout(flush, field.type === 'choice' || field.type === 'multi' ? 0 : 500)
  }, [flush])

  const saveConclusion = useCallback(async (text: string) => {
    const edit = conclusionEdits.current
    setSaveState('saving')
    try {
      const r = await api.setConclusion(id, text)
      if (conclusionEdits.current === edit) conclusionEdits.current = 0
      accept(r)
      setSaveState('saved')
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Conclusion not saved.')
      setSaveState('error')
    }
  }, [id, accept])

  const onConclusion = (text: string) => {
    setConclusion(text)
    conclusionEdits.current++
    window.clearTimeout(conclusionTimer.current)
    conclusionTimer.current = window.setTimeout(() => saveConclusion(text), 700)
  }

  const visible = useMemo(
    () => report ? visibleFields(report.definition, values, report.rendered.computed) : new Set<string>(),
    [report, values])

  if (error && !report) return <p className="error">{error}</p>
  if (!report) return <p className="muted">Loading…</p>

  const readOnly = report.status !== 'Draft' || !can(me, 'reports.edit')
  const myConnection = live.myConnectionId()
  const others = live.viewers.filter(v => v.connectionId !== myConnection)
  const othersIn: Record<string, string[]> = {}
  for (const v of others) if (v.fieldId) (othersIn[v.fieldId] ??= []).push(v.name)

  const ctx: FieldContext = {
    values, visible, computed: report.rendered.computed, sex: report.patient.sex, readOnly,
    othersIn, errors: fieldErrors, onChange, onFocus: live.focus,
  }

  const applyPreset = async (sectionId: string) => {
    await flush()
    try {
      accept(await api.applyPreset(id, sectionId))
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Could not apply.')
    }
  }

  const sign = async () => {
    await flush()
    window.clearTimeout(conclusionTimer.current)
    if (conclusionEdits.current) await saveConclusion(conclusion)
    if (!window.confirm('Sign this report? It cannot be edited afterwards; corrections become addenda.')) return
    try {
      accept(await api.sign(id))
      setError(null)
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Could not sign.')
    }
  }

  const draftFromFindings = () => {
    const lines = report.rendered.abnormalFindings
    if (lines.length === 0) return
    onConclusion([conclusion.trim(), ...lines].filter(Boolean).join('\n'))
  }

  const p = report.patient
  return (
    <div className="editor">
      <div className="editor-head card">
        <div className="who-block">
          <div className="patient-name">{p.lastName}, {p.firstName}</div>
          <div className="muted">
            MRN <span className="mono">{p.mrn}</span> · {p.dateOfBirth} ({ageOn(p.dateOfBirth, report.header.examDate)} y) · {p.sex}
          </div>
        </div>
        <div className="study-block">
          <div><strong>{report.templateName}</strong> <span className="muted small">v{report.templateVersion}</span></div>
          <div className="muted small">Started by {report.createdBy} · {formatDateTime(report.createdAt)}</div>
        </div>
        <div className="status-block">
          {report.status === 'Final'
            ? <span className="badge ok">Signed</span>
            : readOnly ? <span className="badge">Read only</span>
            : <span className={`save-state ${saveState}`}>{saveState === 'saving' ? 'Saving…' : saveState === 'error' ? 'Not saved' : 'All changes saved'}</span>}
          <span className={`live-dot ${live.connected ? 'on' : ''}`} title={live.connected ? 'Live: changes from others appear instantly' : 'Reconnecting…'} />
          {others.length > 0 && (
            <div className="viewers" aria-label="Also here">
              {others.map(v => <span key={v.connectionId} className="avatar" title={`${v.name} (${v.role})${v.fieldId ? ' – editing' : ''}`}>{initials(v.name)}</span>)}
            </div>
          )}
        </div>
      </div>

      <HeaderEditor report={report} readOnly={readOnly} onSaved={accept} onError={setError} />

      {error && <p className="error banner" role="alert">{error} <button className="link" onClick={() => setError(null)}>Dismiss</button></p>}

      {report.status === 'Final' && (
        <div className="signed-banner">
          Signed by <strong>{report.signedBy}</strong>{report.signedByCredentials ? `, ${report.signedByCredentials}` : ''} on {formatDateTime(report.signedAt)}.
          <Link to={`/reports/${id}/print`} className="button">Print / PDF</Link>
        </div>
      )}

      <div className="editor-body">
        <div className="form-col">
          {!readOnly && (
            <nav className="section-nav" aria-label="Sections">
              {report.definition.sections.map(s => <a key={s.id} href={`#sec-${s.id}`}>{s.title}</a>)}
            </nav>
          )}
          {report.definition.sections.map(s => (
            <section key={s.id} id={`sec-${s.id}`} className="card section">
              <div className="section-head">
                <h2>{s.title}</h2>
                {!readOnly && s.normalPreset && (
                  <button className="small-btn" onClick={() => applyPreset(s.id)} title="Fill this section with its normal answers">All normal</button>
                )}
              </div>
              {s.fields.map(f => <FieldInput key={f.id} field={f} ctx={ctx} />)}
            </section>
          ))}
        </div>

        <aside className="preview-col">
          <div className="card preview">
            <div className="preview-head">
              <h2>Report preview</h2>
              <Link to={`/reports/${id}/print`} className="small-btn">Print view</Link>
            </div>
            <ReportView rendered={report.rendered} />

            <section className="conclusion-edit">
              <div className="section-head">
                <h3>Conclusion</h3>
                {!readOnly && report.rendered.abnormalFindings.length > 0 && (
                  <button className="small-btn" onClick={draftFromFindings} title={report.rendered.abnormalFindings.join('\n')}>
                    Insert abnormal findings ({report.rendered.abnormalFindings.length})
                  </button>
                )}
              </div>
              <textarea rows={6} value={conclusion} readOnly={readOnly} maxLength={4000}
                onChange={e => onConclusion(e.target.value)} onFocus={() => live.focus('$conclusion')} onBlur={() => live.focus(null)}
                placeholder="Summary for the referring physician" aria-label="Conclusion" />
              {othersIn['$conclusion'] && <span className="presence-pill">{othersIn['$conclusion'].join(', ')} typing</span>}
            </section>

            {!readOnly && report.rendered.missingRequired.length > 0 && (
              <div className="missing small">
                <strong>Still required to sign:</strong>
                <ul>{report.rendered.missingRequired.map(m => <li key={m}>{m}</li>)}</ul>
              </div>
            )}

            {report.status === 'Draft' && (
              can(me, 'reports.sign')
                ? <button className="primary wide" onClick={sign}>Sign report</button>
                : <p className="muted small">A physician reviews and signs this report.</p>
            )}

            {report.addenda.length > 0 && (
              <section>
                <h3>Addenda</h3>
                {report.addenda.map(a => (
                  <div key={a.id} className="addendum"><p>{a.text}</p><span className="muted small">{a.by} · {formatDateTime(a.createdAt)}</span></div>
                ))}
              </section>
            )}
            {report.status === 'Final' && can(me, 'reports.sign') && <AddendumForm reportId={id} onSaved={accept} />}

            <button className="link small" onClick={() => setShowHistory(h => !h)}>{showHistory ? 'Hide' : 'Show'} change history</button>
            {showHistory && <History reportId={id} revision={report.revision} fields={allFields(report.definition)} />}
          </div>
        </aside>
      </div>
    </div>
  )
}

function initials(name: string) {
  return name.replace(/^Dr\.?\s+/i, '').split(/\s+/).map(w => w[0]).slice(0, 2).join('').toUpperCase()
}

function HeaderEditor({ report, readOnly, onSaved, onError }: {
  report: Report; readOnly: boolean; onSaved: (r: Report) => void; onError: (m: string) => void
}) {
  const toForm = (h: Header) => ({
    examDate: h.examDate, indication: h.indication ?? '', referringPhysician: h.referringPhysician ?? '',
    heightCm: h.heightCm?.toString() ?? '', weightKg: h.weightKg?.toString() ?? '',
  })
  const [form, setForm] = useState(toForm(report.header))
  const [editing, setEditing] = useState(false)
  useEffect(() => { if (!editing) setForm(toForm(report.header)) }, [report.header, editing])

  const save = async () => {
    setEditing(false)
    const h: Header = {
      examDate: form.examDate, indication: form.indication || null, referringPhysician: form.referringPhysician || null,
      heightCm: form.heightCm ? Number(form.heightCm) : null, weightKg: form.weightKg ? Number(form.weightKg) : null,
    }
    if (JSON.stringify(h) === JSON.stringify(report.header)) return
    try { onSaved(await api.setHeader(report.id, h)) } catch (e) { onError(e instanceof Error ? e.message : 'Not saved.') }
  }
  const field = (k: keyof typeof form) => ({
    value: form[k], readOnly,
    onChange: (e: { target: { value: string } }) => { setEditing(true); setForm({ ...form, [k]: e.target.value }) },
    onBlur: save,
  })

  return (
    <div className="exam-header card">
      <label>Exam date<input type="date" {...field('examDate')} /></label>
      <label className="grow">Indication<input {...field('indication')} maxLength={500} /></label>
      <label>Referring<input {...field('referringPhysician')} maxLength={128} /></label>
      <label className="narrow">Height cm<input inputMode="decimal" {...field('heightCm')} /></label>
      <label className="narrow">Weight kg<input inputMode="decimal" {...field('weightKg')} /></label>
    </div>
  )
}

function AddendumForm({ reportId, onSaved }: { reportId: number; onSaved: (r: Report) => void }) {
  const [text, setText] = useState('')
  const [error, setError] = useState<string | null>(null)
  const save = async () => {
    try {
      onSaved(await api.addAddendum(reportId, text))
      setText('')
    } catch (e) { setError(e instanceof Error ? e.message : 'Not saved.') }
  }
  return (
    <section className="stack">
      <h3>Add addendum</h3>
      <textarea rows={3} value={text} onChange={e => setText(e.target.value)} maxLength={4000} aria-label="Addendum" />
      {error && <p className="error small">{error}</p>}
      <button className="primary" disabled={!text.trim()} onClick={save}>Add signed addendum</button>
    </section>
  )
}

function History({ reportId, revision, fields }: { reportId: number; revision: number; fields: FieldDef[] }) {
  const [rows, setRows] = useState<Change[]>([])
  useEffect(() => { api.history(reportId).then(setRows) }, [reportId, revision])
  const labels = new Map(fields.map(f => [f.id, f.label]))
  const name = (f: string) => labels.get(f) ?? ({ $header: 'Exam details', $conclusion: 'Conclusion', $status: 'Signed', $addendum: 'Addendum' } as Record<string, string>)[f] ?? f
  return (
    <ol className="history small">
      {rows.map((c, i) => (
        <li key={i}><span className="muted">{formatDateTime(c.at)}</span> <strong>{c.user}</strong> {name(c.field)}: <span className="mono">{c.value ?? 'cleared'}</span></li>
      ))}
    </ol>
  )
}
