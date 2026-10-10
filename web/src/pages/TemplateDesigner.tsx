import { useCallback, useEffect, useMemo, useState } from 'react'
import { Link, useParams } from 'react-router'
import { api, ApiError, type Rendered, type TemplateDetail } from '../api'
import { FieldInput, type FieldContext } from '../components/FieldInput'
import { ReportView } from '../components/ReportView'
import {
  allFields, FIELD_TYPES, flatten, visibleFields,
  type Bounds, type Condition, type FieldDef, type FieldType, type OptionDef, type SectionDef, type TemplateDefinition, type Value, type Values,
} from '../template'

/** Section index followed by child indexes: [2] is a section, [2, 0, 1] its first field's second child. */
type Path = number[]
type Selection = { kind: 'section'; path: Path } | { kind: 'field'; path: Path } | null

export default function TemplateDesigner() {
  const id = Number(useParams().id)
  const [detail, setDetail] = useState<TemplateDetail | null>(null)
  const [def, setDef] = useState<TemplateDefinition | null>(null)
  const [mode, setMode] = useState<'visual' | 'json'>('visual')
  const [jsonText, setJsonText] = useState('')
  const [jsonError, setJsonError] = useState<string | null>(null)
  const [selection, setSelection] = useState<Selection>(null)
  const [dirty, setDirty] = useState(false)
  const [errors, setErrors] = useState<string[]>([])
  const [message, setMessage] = useState<{ ok: boolean; text: string } | null>(null)
  const [sample, setSample] = useState({ sex: 'M', heightCm: 175, weightKg: 80, ageYears: 60 })
  const [previewValues, setPreviewValues] = useState<Values>({})
  const [rendered, setRendered] = useState<Rendered | null>(null)

  const load = useCallback(async () => {
    const d = await api.template(id)
    setDetail(d)
    const source = d.draftJson ? JSON.parse(d.draftJson) as TemplateDefinition : d.published
    setDef(source)
    setJsonText(JSON.stringify(source, null, 2))
    setErrors(d.draftErrors)
    setDirty(false)
  }, [id])

  useEffect(() => { load() }, [load])

  // Live preview: the server validates and writes the narrative for the sample answers.
  useEffect(() => {
    if (!def) return
    const t = setTimeout(() => {
      api.preview(JSON.stringify(def), previewValues, sample.sex, sample.heightCm, sample.weightKg, sample.ageYears)
        .then(r => { setErrors(r.errors); if (r.rendered) setRendered(r.rendered) })
        .catch(() => {})
    }, 350)
    return () => clearTimeout(t)
  }, [def, previewValues, sample])

  const update = useCallback((fn: (d: TemplateDefinition) => void) => {
    setDef(d => {
      if (!d) return d
      const copy = structuredClone(d)
      fn(copy)
      return copy
    })
    setDirty(true)
  }, [])

  const currentJson = () => mode === 'json' ? jsonText : JSON.stringify(def, null, 2)

  const switchMode = (next: 'visual' | 'json') => {
    if (next === mode) return
    if (next === 'json') {
      setJsonText(JSON.stringify(def, null, 2))
    } else {
      try {
        setDef(JSON.parse(jsonText))
        setJsonError(null)
      } catch (e) {
        setJsonError(`Fix the JSON first: ${(e as Error).message}`)
        return
      }
    }
    setMode(next)
  }

  const save = async () => {
    setMessage(null)
    try {
      const r = await api.saveDraft(id, currentJson())
      setDetail(r.template)
      setErrors(r.errors)
      setDirty(false)
      if (mode === 'json') setDef(JSON.parse(jsonText))
      setMessage({ ok: true, text: r.errors.length ? 'Draft saved. Fix the problems listed before publishing.' : 'Draft saved.' })
    } catch (e) {
      setMessage({ ok: false, text: e instanceof ApiError ? e.message : 'Not saved.' })
    }
  }

  const publish = async () => {
    if (dirty) await save()
    if (!window.confirm('Publish this version? New reports will use it; reports already started keep their version.')) return
    try {
      await api.publish(id)
      await load()
      setMessage({ ok: true, text: 'Published.' })
    } catch (e) {
      setMessage({ ok: false, text: e instanceof ApiError ? e.message : 'Not published.' })
    }
  }

  const discard = async () => {
    if (!window.confirm('Discard the draft and go back to the published version?')) return
    try {
      await api.discardDraft(id)
      await load()
      setSelection(null)
    } catch (e) {
      setMessage({ ok: false, text: e instanceof ApiError ? e.message : 'Could not discard.' })
    }
  }

  const visible = useMemo(() => def ? visibleFields(def, previewValues, rendered?.computed ?? {}) : new Set<string>(), [def, previewValues, rendered])

  if (!detail || !def) return <p className="muted">Loading…</p>

  const ctx: FieldContext = {
    values: previewValues, visible, computed: rendered?.computed ?? {}, sex: sample.sex, readOnly: false,
    othersIn: {}, errors: {}, onFocus: () => {},
    onChange: (f: FieldDef, v: Value | null) => setPreviewValues(prev => {
      const next = { ...prev }
      if (v == null) delete next[f.id]
      else next[f.id] = f.type === 'number' && typeof v === 'string' && v !== '' && !Number.isNaN(Number(v)) ? Number(v) : v
      return next
    }),
  }

  return (
    <div className="designer">
      <div className="page-head">
        <div>
          <Link to="/templates" className="muted small">← Templates</Link>
          <h1>{def.name || detail.name}</h1>
          <div className="muted small">
            <span className="mono">{detail.key}</span> · {def.service}
            {detail.publishedVersionId ? ` · published v${detail.versions.find(v => v.id === detail.publishedVersionId)?.version}` : ' · never published'}
            {detail.draftVersionId ? ' · editing draft' : ''}
            {dirty && ' · unsaved changes'}
          </div>
        </div>
        <div className="actions">
          <div className="segmented">
            <button className={mode === 'visual' ? 'on' : ''} onClick={() => switchMode('visual')}>Visual</button>
            <button className={mode === 'json' ? 'on' : ''} onClick={() => switchMode('json')}>JSON</button>
          </div>
          {detail.draftVersionId && <button onClick={discard}>Discard draft</button>}
          <button onClick={save} disabled={!dirty && mode === 'visual'}>Save draft</button>
          <button className="primary" onClick={publish} disabled={errors.length > 0 || (!detail.draftVersionId && !dirty)}>Publish</button>
        </div>
      </div>

      {message && <p className={message.ok ? 'notice' : 'error banner'}>{message.text}</p>}
      {jsonError && <p className="error banner">{jsonError}</p>}
      {errors.length > 0 && (
        <details className="problems" open>
          <summary>{errors.length} problem{errors.length > 1 ? 's' : ''} to fix before publishing</summary>
          <ul>{errors.map(e => <li key={e}>{e}</li>)}</ul>
        </details>
      )}

      <div className="designer-body">
        {mode === 'json' ? (
          <div className="card pad json-pane">
            <textarea className="mono" spellCheck={false} value={jsonText}
              onChange={e => { setJsonText(e.target.value); setDirty(true) }} aria-label="Template JSON" />
          </div>
        ) : (
          <>
            <Outline def={def} selection={selection} onSelect={setSelection} update={update} />
            <div className="card pad props">
              <TemplateProps def={def} update={update} />
              <hr />
              {selection?.kind === 'section' && def.sections[selection.path[0]] && (
                <SectionProps def={def} index={selection.path[0]} update={update} previewValues={previewValues} />
              )}
              {selection?.kind === 'field' && fieldAt(def, selection.path) && (
                <FieldProps def={def} path={selection.path} update={update} />
              )}
              {!selection && <p className="muted">Pick a section or field in the outline to edit it.</p>}
            </div>
          </>
        )}

        <div className="card pad designer-preview">
          <div className="section-head">
            <h2>Try it</h2>
            <button className="small-btn" onClick={() => setPreviewValues({})}>Clear answers</button>
          </div>
          <div className="sample-patient">
            <label>Sex<select value={sample.sex} onChange={e => setSample({ ...sample, sex: e.target.value })}>
              <option value="M">Male</option><option value="F">Female</option><option value="U">Unknown</option></select></label>
            <label>Height<input type="number" value={sample.heightCm} onChange={e => setSample({ ...sample, heightCm: Number(e.target.value) })} /></label>
            <label>Weight<input type="number" value={sample.weightKg} onChange={e => setSample({ ...sample, weightKg: Number(e.target.value) })} /></label>
            <label>Age<input type="number" value={sample.ageYears} onChange={e => setSample({ ...sample, ageYears: Number(e.target.value) })} /></label>
          </div>
          <div className="preview-form">
            {def.sections.map(s => (
              <section key={s.id} className="section">
                <div className="section-head">
                  <h3>{s.title}</h3>
                  {s.normalPreset && (
                    <button className="small-btn" onClick={() => setPreviewValues(v => ({ ...v, ...s.normalPreset }))}>All normal</button>
                  )}
                </div>
                {s.fields.map(f => <FieldInput key={f.id} field={f} ctx={ctx} />)}
              </section>
            ))}
          </div>
          <hr />
          {rendered && <ReportView rendered={rendered} compact />}
        </div>
      </div>
    </div>
  )
}

// ---------- tree helpers ----------

function siblingsOf(def: TemplateDefinition, path: Path): FieldDef[] {
  let list = def.sections[path[0]].fields
  for (const i of path.slice(1, -1)) list = (list[i].children ??= [])
  return list
}

function fieldAt(def: TemplateDefinition, path: Path): FieldDef | undefined {
  if (path.length < 2) return undefined
  return siblingsOf(def, path)?.[path[path.length - 1]]
}

function newId(def: TemplateDefinition, base: string): string {
  const taken = new Set(allFields(def).map(f => f.id))
  for (let n = 1; ; n++) if (!taken.has(`${base}_${n}`)) return `${base}_${n}`
}

function move<T>(list: T[], from: number, to: number) {
  if (to < 0 || to >= list.length) return
  const [item] = list.splice(from, 1)
  list.splice(to, 0, item)
}

type Update = (fn: (d: TemplateDefinition) => void) => void

// ---------- outline ----------

function Outline({ def, selection, onSelect, update }: {
  def: TemplateDefinition; selection: Selection; onSelect: (s: Selection) => void; update: Update
}) {
  const same = (p: Path) => selection?.path.length === p.length && selection.path.every((v, i) => v === p[i])

  const addSection = () => {
    const sid = `section_${def.sections.length + 1}`
    update(d => d.sections.push({ id: sid, title: 'New section', fields: [{ id: newId(d, 'field'), label: 'New field', type: 'text' }] }))
    onSelect({ kind: 'section', path: [def.sections.length] })
  }

  const addField = (parent: Path) => {
    update(d => {
      const f: FieldDef = { id: newId(d, 'field'), label: 'New field', type: 'choice', options: [{ value: 'normal', label: 'Normal' }, { value: 'abnormal', label: 'Abnormal', abnormal: true }] }
      if (parent.length === 1) d.sections[parent[0]].fields.push(f)
      else {
        const host = fieldAt(d, parent)!
        // A follow-up question: by default only shown when the parent is answered with something abnormal.
        if (host.type === 'choice' || host.type === 'multi') {
          const abn = (host.options ?? []).filter(o => o.abnormal).map(o => o.value)
          f.showIf = abn.length ? { field: host.id, anyOf: abn } : { field: host.id, noneOf: [host.options?.[0]?.value ?? ''] }
        }
        ;(host.children ??= []).push(f)
      }
    })
  }

  const remove = (p: Path) => {
    if (p.length === 1) {
      if (!window.confirm(`Delete section "${def.sections[p[0]].title}" and its fields?`)) return
      update(d => d.sections.splice(p[0], 1))
    } else {
      update(d => siblingsOf(d, p).splice(p[p.length - 1], 1))
    }
    onSelect(null)
  }

  const shift = (p: Path, by: number) => {
    const last = p[p.length - 1]
    update(d => p.length === 1 ? move(d.sections, last, last + by) : move(siblingsOf(d, p), last, last + by))
    onSelect({ kind: p.length === 1 ? 'section' : 'field', path: [...p.slice(0, -1), last + by] })
  }

  const tools = (p: Path, canAddChild: boolean) => (
    <span className="tree-tools">
      <button title="Move up" onClick={e => { e.stopPropagation(); shift(p, -1) }}>↑</button>
      <button title="Move down" onClick={e => { e.stopPropagation(); shift(p, 1) }}>↓</button>
      {canAddChild && <button title={p.length === 1 ? 'Add field' : 'Add follow-up field'} onClick={e => { e.stopPropagation(); addField(p) }}>+</button>}
      <button title="Delete" onClick={e => { e.stopPropagation(); remove(p) }}>×</button>
    </span>
  )

  const renderFields = (fields: FieldDef[], base: Path) => (
    <ul>
      {fields.map((f, i) => {
        const p = [...base, i]
        return (
          <li key={`${f.id}-${i}`}>
            <div className={`tree-item ${same(p) ? 'on' : ''}`} onClick={() => onSelect({ kind: 'field', path: p })}>
              <span className="tree-label">{f.label || f.id}{f.showIf && <span className="cond" title={`Shown when ${f.showIf.field} …`}> ⤷</span>}</span>
              <span className="tree-type">{f.type}</span>
              {tools(p, true)}
            </div>
            {(f.children?.length ?? 0) > 0 && renderFields(f.children!, p)}
          </li>
        )
      })}
    </ul>
  )

  return (
    <div className="card pad outline">
      <div className="section-head"><h2>Outline</h2><button className="small-btn" onClick={addSection}>+ Section</button></div>
      <ul className="tree">
        {def.sections.map((s, si) => (
          <li key={`${s.id}-${si}`}>
            <div className={`tree-item section ${same([si]) ? 'on' : ''}`} onClick={() => onSelect({ kind: 'section', path: [si] })}>
              <span className="tree-label">{s.title || s.id}</span>
              {tools([si], true)}
            </div>
            {renderFields(s.fields, [si])}
          </li>
        ))}
      </ul>
    </div>
  )
}

// ---------- property editors ----------

function TemplateProps({ def, update }: { def: TemplateDefinition; update: Update }) {
  return (
    <div className="stack">
      <h2>Template</h2>
      <div className="form-grid">
        <label>Name<input value={def.name} onChange={e => update(d => { d.name = e.target.value })} /></label>
        <label>Service<input value={def.service} onChange={e => update(d => { d.service = e.target.value })} /></label>
        <label className="span2">Description<input value={def.description ?? ''} onChange={e => update(d => { d.description = e.target.value || null })} /></label>
      </div>
    </div>
  )
}

function SectionProps({ def, index, update, previewValues }: { def: TemplateDefinition; index: number; update: Update; previewValues: Values }) {
  const s = def.sections[index]
  const set = (fn: (s: SectionDef) => void) => update(d => fn(d.sections[index]))
  const ids = new Set([...flatten(s.fields)].map(f => f.id))
  const capture = () => set(sec => {
    const preset = Object.fromEntries(Object.entries(previewValues).filter(([k]) => ids.has(k)))
    sec.normalPreset = Object.keys(preset).length ? preset : null
  })

  return (
    <div className="stack">
      <h2>Section</h2>
      <div className="form-grid">
        <label>Title<input value={s.title} onChange={e => set(sec => { sec.title = e.target.value })} /></label>
        <label>Id<input className="mono" value={s.id} onChange={e => set(sec => { sec.id = e.target.value })} /></label>
      </div>
      <label className="check">
        <input type="checkbox" checked={s.narrative !== false} onChange={e => set(sec => { sec.narrative = e.target.checked ? undefined : false })} />
        Write this section's sentences into the findings (untick for a measurements-only section)
      </label>
      <h3>“All normal” answers</h3>
      <p className="muted small">Answer this section in the “Try it” panel the way a normal study looks, then capture it. The typist gets an “All normal” button that fills these answers in one click.</p>
      {s.normalPreset ? (
        <ul className="preset-list small">
          {Object.entries(s.normalPreset).map(([k, v]) => <li key={k}><span className="mono">{k}</span> = {Array.isArray(v) ? v.join(', ') : String(v)}</li>)}
        </ul>
      ) : <p className="muted small">None yet.</p>}
      <div className="actions">
        <button onClick={capture}>Capture from “Try it”</button>
        {s.normalPreset && <button className="link" onClick={() => set(sec => { sec.normalPreset = null })}>Remove</button>}
      </div>
    </div>
  )
}

function FieldProps({ def, path, update }: { def: TemplateDefinition; path: Path; update: Update }) {
  const f = fieldAt(def, path)!
  const set = (fn: (f: FieldDef) => void) => update(d => fn(fieldAt(d, path)!))
  const hasOptions = f.type === 'choice' || f.type === 'multi'
  const numeric = f.type === 'number' || f.type === 'computed'

  const rename = (next: string) => update(d => {
    const old = fieldAt(d, path)!.id
    fieldAt(d, path)!.id = next
    // Keep references working: conditions, formulas and presets that pointed at the old id.
    for (const other of allFields(d)) {
      if (other.showIf?.field === old) other.showIf.field = next
      if (other.formula) other.formula = other.formula.replace(new RegExp(`\\b${old}\\b`, 'g'), next)
    }
    for (const s of d.sections) if (s.normalPreset && old in s.normalPreset) {
      s.normalPreset[next] = s.normalPreset[old]
      delete s.normalPreset[old]
    }
  })

  const changeType = (type: FieldType) => set(x => {
    x.type = type
    if (type === 'choice' || type === 'multi') {
      if (!x.options?.length) x.options = [{ value: 'normal', label: 'Normal' }, { value: 'abnormal', label: 'Abnormal', abnormal: true }]
    } else delete x.options
    if (type === 'computed') x.formula ??= ''
    else delete x.formula
    if (type !== 'number' && type !== 'computed') { delete x.normal; delete x.unit; delete x.decimals; delete x.measurement }
  })

  return (
    <div className="stack">
      <h2>Field</h2>
      <div className="form-grid">
        <label>Label<input value={f.label} onChange={e => set(x => { x.label = e.target.value })} /></label>
        <label>Id <span className="muted small">(a–z, 0–9, _)</span><input className="mono" value={f.id} onChange={e => rename(e.target.value.toLowerCase().replace(/[^a-z0-9_]/g, '_'))} /></label>
        <label>Type
          <select value={f.type} onChange={e => changeType(e.target.value as FieldType)}>
            {FIELD_TYPES.map(t => <option key={t.value} value={t.value}>{t.label}</option>)}
          </select>
        </label>
        <label>Hint for the typist<input value={f.hint ?? ''} onChange={e => set(x => { x.hint = e.target.value || null })} /></label>
      </div>
      <label className="check"><input type="checkbox" checked={!!f.required} onChange={e => set(x => { x.required = e.target.checked || undefined })} /> Required before signing</label>

      {numeric && (
        <div className="form-grid">
          <label>Unit<input value={f.unit ?? ''} onChange={e => set(x => { x.unit = e.target.value || null })} placeholder="mm, %, mL/m²…" /></label>
          <label>Decimals<input type="number" min={0} max={6} value={f.decimals ?? ''} onChange={e => set(x => { x.decimals = e.target.value === '' ? null : Number(e.target.value) })} /></label>
          <label className="check span2"><input type="checkbox" checked={f.measurement !== false} onChange={e => set(x => { x.measurement = e.target.checked ? undefined : false })} /> Show in the measurements table</label>
        </div>
      )}

      {f.type === 'computed' && (
        <label>Formula
          <input className="mono" value={f.formula ?? ''} onChange={e => set(x => { x.formula = e.target.value })} placeholder="la_volume / bsa" />
          <span className="muted small">Use other number fields by id, plus height, weight, age. Operators + − × / ^ and sqrt, abs, min, max, avg, round(x, digits).</span>
        </label>
      )}

      {numeric && <RangeEditor range={f.normal ?? null} onChange={r => set(x => { x.normal = r })} />}

      <PhraseEditor
        value={f.phrase}
        onChange={p => set(x => { x.phrase = p })}
        help={hasOptions ? (f.type === 'multi' ? 'Used once for all picked options, e.g. “The mitral valve shows {values}.”' : 'Used when the option has no sentence of its own.')
          : numeric ? 'Numbers reach the findings only with a sentence, e.g. “The ejection fraction is {value}%.”' : 'e.g. “{label}: {value}.”'}
      />

      {hasOptions && <OptionsEditor field={f} set={set} />}

      <ConditionEditor def={def} field={f} onChange={c => set(x => { x.showIf = c })} />
    </div>
  )
}

function PhraseEditor({ value, onChange, help }: { value: string | null | undefined; onChange: (v: string | null) => void; help: string }) {
  const mode = value == null ? 'default' : value === '' ? 'silent' : 'custom'
  return (
    <div className="stack">
      <label>Sentence
        <select value={mode} onChange={e => onChange(e.target.value === 'default' ? null : e.target.value === 'silent' ? '' : '{label}: {value}.')}>
          <option value="default">Default</option>
          <option value="custom">Custom sentence</option>
          <option value="silent">Say nothing</option>
        </select>
      </label>
      {mode === 'custom' && <input value={value ?? ''} onChange={e => onChange(e.target.value)} aria-label="Sentence" />}
      <span className="muted small">{help} Placeholders: {'{label} {value} {unit} {values}'}.</span>
    </div>
  )
}

function RangeEditor({ range, onChange }: { range: TemplateDefinition['sections'][0]['fields'][0]['normal']; onChange: (r: FieldDef['normal']) => void }) {
  const r = range ?? {}
  const num = (v: string) => v === '' ? null : Number(v)
  const setB = (key: 'male' | 'female' | null, side: 'min' | 'max', v: string) => {
    const next = structuredClone(r)
    if (key) {
      const b: Bounds = { ...(next[key] ?? {}), [side]: num(v) }
      next[key] = b.min == null && b.max == null ? null : b
    } else next[side] = num(v)
    const empty = next.min == null && next.max == null && !next.male && !next.female
    onChange(empty ? null : next)
  }
  const row = (label: string, key: 'male' | 'female' | null) => {
    const b = key ? r[key] ?? {} : r
    return (
      <tr>
        <th>{label}</th>
        <td><input type="number" step="any" value={b.min ?? ''} onChange={e => setB(key, 'min', e.target.value)} aria-label={`${label} min`} /></td>
        <td><input type="number" step="any" value={b.max ?? ''} onChange={e => setB(key, 'max', e.target.value)} aria-label={`${label} max`} /></td>
      </tr>
    )
  }
  return (
    <div className="stack">
      <h3>Normal range</h3>
      <table className="range-table">
        <thead><tr><th></th><th>Min</th><th>Max</th></tr></thead>
        <tbody>{row('Everyone', null)}{row('Male', 'male')}{row('Female', 'female')}</tbody>
      </table>
      <label>Source<input value={r.source ?? ''} onChange={e => onChange({ ...r, source: e.target.value || null })} placeholder="e.g. ASE/EACVI 2015" /></label>
      <span className="muted small">Sex-specific rows win when the patient's sex is known. Values outside the range are flagged H or L.</span>
    </div>
  )
}

function OptionsEditor({ field, set }: { field: FieldDef; set: (fn: (f: FieldDef) => void) => void }) {
  const opts = field.options ?? []
  const setOpt = (i: number, fn: (o: OptionDef) => void) => set(x => fn(x.options![i]))
  return (
    <div className="stack">
      <h3>Options</h3>
      <table className="options-table">
        <thead><tr><th>Label</th><th>Value</th><th>{field.type === 'multi' ? 'Wording in list' : 'Sentence'}</th><th title="Carried into the conclusion draft">Abnormal</th><th></th></tr></thead>
        <tbody>
          {opts.map((o, i) => (
            <tr key={i}>
              <td><input value={o.label} onChange={e => setOpt(i, x => { x.label = e.target.value })} /></td>
              <td><input className="mono" value={o.value} onChange={e => setOpt(i, x => { x.value = e.target.value.toLowerCase().replace(/\s+/g, '_') })} /></td>
              <td>
                <input value={o.phrase ?? ''} placeholder={o.phrase === '' ? '(says nothing)' : '(default)'}
                  onChange={e => setOpt(i, x => { x.phrase = e.target.value === '' ? null : e.target.value })} />
                {field.type === 'choice' && (
                  <label className="check tiny"><input type="checkbox" checked={o.phrase === ''} onChange={e => setOpt(i, x => { x.phrase = e.target.checked ? '' : null })} /> silent</label>
                )}
              </td>
              <td className="center"><input type="checkbox" checked={!!o.abnormal} onChange={e => setOpt(i, x => { x.abnormal = e.target.checked || undefined })} aria-label="Abnormal" /></td>
              <td className="row-tools">
                <button title="Up" onClick={() => set(x => move(x.options!, i, i - 1))}>↑</button>
                <button title="Down" onClick={() => set(x => move(x.options!, i, i + 1))}>↓</button>
                <button title="Delete" onClick={() => set(x => { x.options!.splice(i, 1) })}>×</button>
              </td>
            </tr>
          ))}
        </tbody>
      </table>
      <button onClick={() => set(x => { (x.options ??= []).push({ value: `option_${opts.length + 1}`, label: 'New option' }) })}>+ Option</button>
    </div>
  )
}

function ConditionEditor({ def, field, onChange }: { def: TemplateDefinition; field: FieldDef; onChange: (c: Condition | null) => void }) {
  const c = field.showIf
  const candidates = allFields(def).filter(f => f.id !== field.id)
  const target = candidates.find(f => f.id === c?.field)
  const kind = !c ? 'always' : c.anyOf ? 'anyOf' : c.noneOf ? 'noneOf' : c.greaterThan != null ? 'greaterThan' : 'lessThan'
  const targetNumeric = target?.type === 'number' || target?.type === 'computed'

  const setKind = (k: string, targetId = c?.field ?? candidates[0]?.id) => {
    if (k === 'always' || !targetId) return onChange(null)
    const t = candidates.find(f => f.id === targetId)
    const numericTarget = t?.type === 'number' || t?.type === 'computed'
    if (numericTarget) return onChange({ field: targetId, [k === 'lessThan' ? 'lessThan' : 'greaterThan']: 0 })
    onChange({ field: targetId, [k === 'noneOf' ? 'noneOf' : 'anyOf']: [] })
  }
  const listKey = kind === 'noneOf' ? 'noneOf' : 'anyOf'
  const list = (c?.[listKey] ?? []) as string[]

  return (
    <div className="stack">
      <h3>Show this field</h3>
      <div className="form-grid">
        <label>When
          <select value={kind === 'always' ? 'always' : 'cond'} onChange={e => setKind(e.target.value === 'always' ? 'always' : 'anyOf')}>
            <option value="always">Always</option>
            <option value="cond">Only when another answer…</option>
          </select>
        </label>
        {c && (
          <label>Field
            <select value={c.field} onChange={e => setKind(kind === 'noneOf' ? 'noneOf' : kind, e.target.value)}>
              {candidates.map(f => <option key={f.id} value={f.id}>{f.label} ({f.id})</option>)}
            </select>
          </label>
        )}
      </div>
      {c && target && !targetNumeric && (
        <>
          <div className="segmented small">
            <button className={kind === 'anyOf' ? 'on' : ''} onClick={() => onChange({ field: c.field, anyOf: list })}>is one of</button>
            <button className={kind === 'noneOf' ? 'on' : ''} onClick={() => onChange({ field: c.field, noneOf: list })}>is anything except</button>
          </div>
          <div className="chips">
            {(target.options ?? []).map(o => {
              const on = list.includes(o.value)
              return (
                <button key={o.value} className={`chip multi ${on ? 'on' : ''}`}
                  onClick={() => onChange({ field: c.field, [listKey]: on ? list.filter(v => v !== o.value) : [...list, o.value] })}>
                  {o.label}
                </button>
              )
            })}
          </div>
        </>
      )}
      {c && targetNumeric && (
        <div className="form-grid">
          <label>Is
            <select value={kind} onChange={e => onChange({ field: c.field, [e.target.value]: c.greaterThan ?? c.lessThan ?? 0 })}>
              <option value="greaterThan">greater than</option>
              <option value="lessThan">less than</option>
            </select>
          </label>
          <label>Value<input type="number" step="any" value={(kind === 'lessThan' ? c.lessThan : c.greaterThan) ?? ''}
            onChange={e => onChange({ field: c.field, [kind]: Number(e.target.value) })} /></label>
        </div>
      )}
    </div>
  )
}
