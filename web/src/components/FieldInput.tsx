import { boundsFor, flag, rangeText, type FieldDef, type Value, type Values } from '../template'

export type FieldContext = {
  values: Values
  visible: Set<string>
  computed: Record<string, number | null>
  sex: string | undefined
  readOnly: boolean
  /** Field id → names of other people currently in that field. */
  othersIn: Record<string, string[]>
  errors: Record<string, string>
  onChange: (field: FieldDef, value: Value | null) => void
  onFocus: (fieldId: string | null) => void
}

const NUMBER = /^-?(\d+\.?\d*|\.\d+)$/

export function isNumberText(s: string) {
  return NUMBER.test(s.trim())
}

/** One template field and its visible children. */
export function FieldInput({ field, ctx }: { field: FieldDef; ctx: FieldContext }) {
  if (!ctx.visible.has(field.id)) return null
  const value = ctx.values[field.id]
  const others = ctx.othersIn[field.id]
  const error = ctx.errors[field.id]
  const inline = field.type === 'number' || field.type === 'computed'
  const children = (field.children ?? []).filter(c => ctx.visible.has(c.id))

  return (
    <div className={`field ${inline ? 'inline' : ''} ${others ? 'busy' : ''}`} data-field={field.id}>
      <div className="field-label" id={`lbl-${field.id}`}>
        <span>{field.label}{field.required && <span className="req" title="Required to sign"> *</span>}</span>
        {field.hint && <span className="hint" title={field.hint}>?</span>}
        {others && <span className="presence-pill" title={`${others.join(', ')} editing`}>{others.join(', ')}</span>}
      </div>
      <div className="field-input">
        <Input field={field} value={value} ctx={ctx} />
        {error && <div className="error small">{error}</div>}
      </div>
      {children.length > 0 && (
        <div className="children">
          {children.map(c => <FieldInput key={c.id} field={c} ctx={ctx} />)}
        </div>
      )}
    </div>
  )
}

function Input({ field, value, ctx }: { field: FieldDef; value: Value | undefined; ctx: FieldContext }) {
  const focus = () => ctx.onFocus(field.id)
  const blur = () => ctx.onFocus(null)
  const labelled = { 'aria-labelledby': `lbl-${field.id}` }

  switch (field.type) {
    case 'choice':
      return (
        <div className="chips" role="radiogroup" {...labelled}>
          {(field.options ?? []).map(o => {
            const on = value === o.value
            return (
              <button key={o.value} type="button" role="radio" aria-checked={on} disabled={ctx.readOnly}
                className={`chip ${on ? 'on' : ''} ${o.abnormal ? 'abn' : ''}`}
                onFocus={focus} onBlur={blur}
                onClick={() => ctx.onChange(field, on ? null : o.value)}>
                {o.label}
              </button>
            )
          })}
        </div>
      )

    case 'multi': {
      const selected = Array.isArray(value) ? value : []
      return (
        <div className="chips" role="group" {...labelled}>
          {(field.options ?? []).map(o => {
            const on = selected.includes(o.value)
            return (
              <button key={o.value} type="button" role="checkbox" aria-checked={on} disabled={ctx.readOnly}
                className={`chip multi ${on ? 'on' : ''} ${o.abnormal ? 'abn' : ''}`}
                onFocus={focus} onBlur={blur}
                onClick={() => {
                  const next = on ? selected.filter(v => v !== o.value) : [...selected, o.value]
                  ctx.onChange(field, next.length ? next : null)
                }}>
                {o.label}
              </button>
            )
          })}
        </div>
      )
    }

    case 'number': {
      const text = value == null ? '' : String(value)
      const n = isNumberText(text) ? Number(text) : null
      const b = boundsFor(field.normal, ctx.sex)
      const f = flag(b, n)
      return (
        <div className="number">
          <input type="text" inputMode="decimal" value={text} readOnly={ctx.readOnly} {...labelled}
            className={f ? `flag-${f}` : ''} onFocus={focus} onBlur={blur}
            onChange={e => ctx.onChange(field, e.target.value === '' ? null : e.target.value)} />
          {field.unit && <span className="unit">{field.unit}</span>}
          <Range text={rangeText(b)} flagValue={f} />
        </div>
      )
    }

    case 'computed': {
      const n = ctx.computed[field.id]
      const b = boundsFor(field.normal, ctx.sex)
      const f = flag(b, n)
      return (
        <div className="number computed" title={`= ${field.formula}`}>
          <output className={f ? `flag-${f}` : ''} {...labelled}>{n == null ? '—' : n.toFixed(field.decimals ?? 1)}</output>
          {field.unit && <span className="unit">{field.unit}</span>}
          <Range text={rangeText(b)} flagValue={f} />
        </div>
      )
    }

    case 'text':
      return <input type="text" value={typeof value === 'string' ? value : ''} readOnly={ctx.readOnly} maxLength={4000} {...labelled}
        onFocus={focus} onBlur={blur} onChange={e => ctx.onChange(field, e.target.value || null)} />

    case 'textarea':
      return <textarea rows={3} value={typeof value === 'string' ? value : ''} readOnly={ctx.readOnly} maxLength={4000} {...labelled}
        onFocus={focus} onBlur={blur} onChange={e => ctx.onChange(field, e.target.value || null)} />
  }
}

function Range({ text, flagValue }: { text: string | null; flagValue: 'H' | 'L' | null }) {
  if (!text && !flagValue) return null
  return (
    <span className="range">
      {text && <span className="muted">({text})</span>}
      {flagValue && <span className={`flag ${flagValue}`} title={flagValue === 'H' ? 'Above normal' : 'Below normal'}>{flagValue}</span>}
    </span>
  )
}
