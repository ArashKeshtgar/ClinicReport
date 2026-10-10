import { useEffect, useState, type FormEvent } from 'react'
import { useNavigate } from 'react-router'
import { ageOn, api, ApiError, can, today, type Patient, type Sex, type TemplateSummary } from '../api'
import { useSession } from '../App'

export default function NewReport() {
  const navigate = useNavigate()
  const [patient, setPatient] = useState<Patient | null>(null)
  const [templates, setTemplates] = useState<TemplateSummary[]>([])
  const [templateId, setTemplateId] = useState<number | null>(null)
  const [examDate, setExamDate] = useState(today())
  const [indication, setIndication] = useState('')
  const [referring, setReferring] = useState('')
  const [height, setHeight] = useState('')
  const [weight, setWeight] = useState('')
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  useEffect(() => {
    api.templates().then(t => {
      const usable = t.filter(x => x.isActive && x.publishedVersionId)
      setTemplates(usable)
      if (usable.length === 1) setTemplateId(usable[0].id)
    })
  }, [])

  const create = async (e: FormEvent) => {
    e.preventDefault()
    if (!patient || !templateId) return
    setBusy(true)
    setError(null)
    try {
      const r = await api.createReport({
        patientId: patient.id, templateId, examDate,
        indication: indication || null, referringPhysician: referring || null,
        heightCm: height ? Number(height) : null, weightKg: weight ? Number(weight) : null,
      })
      navigate(`/reports/${r.id}`, { replace: true })
    } catch (err) {
      setError(err instanceof ApiError ? err.message : 'Could not create the report.')
      setBusy(false)
    }
  }

  return (
    <>
      <div className="page-head"><h1>New report</h1></div>
      <div className="new-report">
        <section className="card pad">
          <h2><span className="step">1</span> Patient</h2>
          {patient ? (
            <div className="picked">
              <div>
                <strong>{patient.lastName}, {patient.firstName}</strong>
                <div className="muted">{patient.mrn} · {patient.dateOfBirth} ({ageOn(patient.dateOfBirth, examDate)} y) · {patient.sex}</div>
              </div>
              <button className="link" onClick={() => setPatient(null)}>Change</button>
            </div>
          ) : <PatientPicker onPick={setPatient} />}
        </section>

        <section className="card pad">
          <h2><span className="step">2</span> Study</h2>
          {templates.length === 0 && <p className="muted">No published templates yet. An administrator can add one under Templates.</p>}
          <div className="template-choices">
            {templates.map(t => (
              <button key={t.id} type="button" className={`template-choice ${templateId === t.id ? 'on' : ''}`} onClick={() => setTemplateId(t.id)}>
                <span className="muted small">{t.service}</span>
                <strong>{t.name}</strong>
              </button>
            ))}
          </div>
        </section>

        <form className="card pad" onSubmit={create}>
          <h2><span className="step">3</span> Exam</h2>
          <div className="form-grid">
            <label>Exam date<input type="date" value={examDate} onChange={e => setExamDate(e.target.value)} required /></label>
            <label>Referring physician<input value={referring} onChange={e => setReferring(e.target.value)} maxLength={128} /></label>
            <label>Height (cm)<input type="number" inputMode="decimal" min={30} max={250} step="any" value={height} onChange={e => setHeight(e.target.value)} /></label>
            <label>Weight (kg)<input type="number" inputMode="decimal" min={1} max={400} step="any" value={weight} onChange={e => setWeight(e.target.value)} /></label>
            <label className="span2">Indication<input value={indication} onChange={e => setIndication(e.target.value)} maxLength={500} /></label>
          </div>
          {error && <p className="error">{error}</p>}
          <div className="actions">
            <button className="primary" disabled={!patient || !templateId || busy}>Start report</button>
          </div>
        </form>
      </div>
    </>
  )
}

function PatientPicker({ onPick }: { onPick: (p: Patient) => void }) {
  const { me } = useSession()
  const [q, setQ] = useState('')
  const [results, setResults] = useState<Patient[]>([])
  const [adding, setAdding] = useState(false)

  useEffect(() => {
    const t = setTimeout(() => api.patients(q).then(setResults), 200)
    return () => clearTimeout(t)
  }, [q])

  if (adding) return <NewPatientForm onCreated={onPick} onCancel={() => setAdding(false)} />

  return (
    <div className="stack">
      <input type="search" placeholder="Last name, first name or MRN" value={q} onChange={e => setQ(e.target.value)} autoFocus aria-label="Find patient" />
      <ul className="pick-list">
        {results.map(p => (
          <li key={p.id}>
            <button type="button" onClick={() => onPick(p)}>
              <strong>{p.lastName}, {p.firstName}</strong>
              <span className="muted">{p.mrn} · {p.dateOfBirth} · {p.sex}</span>
            </button>
          </li>
        ))}
        {results.length === 0 && <li className="muted small pad-s">No match.</li>}
      </ul>
      {can(me, 'patients.manage') && <button type="button" className="link" onClick={() => setAdding(true)}>+ New patient</button>}
    </div>
  )
}

function NewPatientForm({ onCreated, onCancel }: { onCreated: (p: Patient) => void; onCancel: () => void }) {
  const [p, setP] = useState({ mrn: '', firstName: '', lastName: '', dateOfBirth: '', sex: 'U' as Sex })
  const [errors, setErrors] = useState<Record<string, string[]>>({})
  const set = (k: keyof typeof p) => (e: { target: { value: string } }) => setP({ ...p, [k]: e.target.value })

  const save = async () => {
    try {
      onCreated(await api.createPatient(p))
    } catch (e) {
      if (e instanceof ApiError) setErrors(Object.keys(e.fieldErrors).length ? e.fieldErrors : { '': [e.message] })
    }
  }
  const err = (k: string) => errors[k]?.[0] && <span className="error small">{errors[k][0]}</span>

  return (
    <div className="stack">
      <div className="form-grid">
        <label>MRN<input value={p.mrn} onChange={set('mrn')} maxLength={32} autoFocus />{err('mrn')}</label>
        <label>Date of birth<input type="date" value={p.dateOfBirth} onChange={set('dateOfBirth')} />{err('dateOfBirth')}</label>
        <label>First name<input value={p.firstName} onChange={set('firstName')} maxLength={64} />{err('firstName')}</label>
        <label>Last name<input value={p.lastName} onChange={set('lastName')} maxLength={64} />{err('lastName')}</label>
        <label>Sex
          <select value={p.sex} onChange={set('sex')}>
            <option value="F">Female</option><option value="M">Male</option><option value="X">X</option><option value="U">Unknown</option>
          </select>
          <span className="muted small">Sex picks sex-specific normal ranges.</span>
        </label>
      </div>
      {err('')}
      <div className="actions">
        <button type="button" className="link" onClick={onCancel}>Cancel</button>
        <button type="button" className="primary" onClick={save}>Add patient</button>
      </div>
    </div>
  )
}
