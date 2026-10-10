// Template model. Mirrors src/ClinicReport.Api/Domain/TemplateDefinition.cs; the API is the source of truth
// for validation and the narrative, this file only decides what to show while typing.

export type FieldType = 'choice' | 'multi' | 'number' | 'computed' | 'text' | 'textarea'

export const FIELD_TYPES: { value: FieldType; label: string }[] = [
  { value: 'choice', label: 'Single choice' },
  { value: 'multi', label: 'Multiple choice' },
  { value: 'number', label: 'Number' },
  { value: 'computed', label: 'Calculated' },
  { value: 'text', label: 'Short text' },
  { value: 'textarea', label: 'Paragraph' },
]

export type Bounds = { min?: number | null; max?: number | null }
export type NormalRange = Bounds & { male?: Bounds | null; female?: Bounds | null; source?: string | null }

export type OptionDef = { value: string; label: string; phrase?: string | null; abnormal?: boolean }

export type Condition = { field: string; anyOf?: string[] | null; noneOf?: string[] | null; greaterThan?: number | null; lessThan?: number | null }

export type FieldDef = {
  id: string
  label: string
  type: FieldType
  hint?: string | null
  unit?: string | null
  decimals?: number | null
  normal?: NormalRange | null
  formula?: string | null
  options?: OptionDef[]
  phrase?: string | null
  showIf?: Condition | null
  required?: boolean
  measurement?: boolean | null
  children?: FieldDef[]
}

export type SectionDef = {
  id: string
  title: string
  narrative?: boolean
  fields: FieldDef[]
  normalPreset?: Record<string, Value> | null
}

export type TemplateDefinition = {
  key: string
  name: string
  service: string
  description?: string | null
  sections: SectionDef[]
}

export type Value = string | string[] | number
export type Values = Record<string, Value>

export function* flatten(fields: FieldDef[] | undefined): Generator<FieldDef> {
  for (const f of fields ?? []) {
    yield f
    yield* flatten(f.children)
  }
}

export function allFields(def: TemplateDefinition): FieldDef[] {
  return def.sections.flatMap(s => [...flatten(s.fields)])
}

export function boundsFor(range: NormalRange | null | undefined, sex: string | undefined): Bounds | null {
  if (!range) return null
  if (sex === 'M' && range.male) return range.male
  if (sex === 'F' && range.female) return range.female
  if (range.min != null || range.max != null) return { min: range.min, max: range.max }
  return null
}

export function rangeText(b: Bounds | null): string | null {
  if (!b) return null
  if (b.min != null && b.max != null) return `${b.min}–${b.max}`
  if (b.min != null) return `≥ ${b.min}`
  if (b.max != null) return `≤ ${b.max}`
  return null
}

export function flag(b: Bounds | null, v: number | null | undefined): 'H' | 'L' | null {
  if (!b || v == null || Number.isNaN(v)) return null
  if (b.min != null && v < b.min) return 'L'
  if (b.max != null && v > b.max) return 'H'
  return null
}

/**
 * Which fields are showing, same rules as ReportRenderer.IsVisible on the server: a field shows when its
 * parent shows and its showIf holds. Numbers for computed fields come from the server's last render.
 */
export function visibleFields(def: TemplateDefinition, values: Values, computed: Record<string, number | null>): Set<string> {
  const byId = new Map<string, FieldDef>()
  const parent = new Map<string, FieldDef | null>()
  const index = (fields: FieldDef[] | undefined, p: FieldDef | null) => {
    for (const f of fields ?? []) {
      if (!byId.has(f.id)) byId.set(f.id, f)
      if (!parent.has(f.id)) parent.set(f.id, p)
      index(f.children, f)
    }
  }
  def.sections.forEach(s => index(s.fields, null))

  const memo = new Map<string, boolean>()
  const inProgress = new Set<string>()

  const numberOf = (id: string): number | null => {
    const f = byId.get(id)
    if (!f) return null
    if (f.type === 'computed') return computed[id] ?? null
    const v = values[id]
    const n = typeof v === 'number' ? v : typeof v === 'string' ? Number(v) : NaN
    return Number.isFinite(n) ? n : null
  }

  const satisfies = (c: Condition): boolean => {
    const target = byId.get(c.field)
    if (!target || !isVisible(target.id)) return false
    if (target.type === 'number' || target.type === 'computed') {
      const n = numberOf(target.id)
      if (n == null) return false
      if (c.greaterThan != null && !(n > c.greaterThan)) return false
      if (c.lessThan != null && !(n < c.lessThan)) return false
      return true
    }
    const v = values[target.id]
    const selected = Array.isArray(v) ? v : typeof v === 'string' && v !== '' ? [v] : []
    if (selected.length === 0) return false
    if (c.anyOf && !selected.some(s => c.anyOf!.includes(s))) return false
    if (c.noneOf && selected.every(s => c.noneOf!.includes(s))) return false
    return true
  }

  const isVisible = (id: string): boolean => {
    if (memo.has(id)) return memo.get(id)!
    const f = byId.get(id)
    if (!f || inProgress.has(id)) return false
    inProgress.add(id)
    const p = parent.get(id)
    const result = (!p || isVisible(p.id)) && (!f.showIf || satisfies(f.showIf))
    inProgress.delete(id)
    memo.set(id, result)
    return result
  }

  const visible = new Set<string>()
  for (const id of byId.keys()) if (isVisible(id)) visible.add(id)
  return visible
}

export function hasValue(v: Value | undefined): boolean {
  if (v == null) return false
  if (Array.isArray(v)) return v.length > 0
  if (typeof v === 'string') return v.trim() !== ''
  return true
}
