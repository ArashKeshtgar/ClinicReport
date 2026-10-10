// Typed client for ClinicReport.Api. Shapes mirror the C# records in src/ClinicReport.Api.
import type { TemplateDefinition, Value, Values } from './template'

export type Role = 'Admin' | 'Physician' | 'Typist'
export type Me = { id: number; userName: string; displayName: string; credentials: string | null; role: Role }

export type Settings = {
  name: string; address: string | null; phone: string | null; reportFooter: string | null
  demo: boolean; demoUsers: string[]
}

export type Sex = 'M' | 'F' | 'X' | 'U'
export type Patient = { id: number; mrn: string; firstName: string; lastName: string; dateOfBirth: string; sex: Sex }

export type TemplateSummary = {
  id: number; key: string; name: string; service: string; isActive: boolean
  publishedVersionId: number | null; publishedVersion: number | null; draftVersionId: number | null
}

export type TemplateDetail = {
  id: number; key: string; name: string; service: string; isActive: boolean
  versions: { id: number; version: number; status: string; createdAt: string; publishedAt: string | null }[]
  publishedVersionId: number | null; published: TemplateDefinition | null
  draftVersionId: number | null; draftJson: string | null; draftErrors: string[]
}

export type Measurement = {
  fieldId: string; section: string; label: string; value: number; display: string
  unit: string | null; range: string | null; flag: 'H' | 'L' | null
}

export type Rendered = {
  sections: { id: string; title: string; sentences: string[] }[]
  measurements: Measurement[]
  computed: Record<string, number | null>
  abnormalFindings: string[]
  missingRequired: string[]
}

export type Header = { examDate: string; indication: string | null; referringPhysician: string | null; heightCm: number | null; weightKg: number | null }

export type Report = {
  id: number; status: 'Draft' | 'Final'; revision: number; patient: Patient
  templateId: number; templateVersionId: number; templateVersion: number; templateName: string; service: string
  definition: TemplateDefinition; header: Header; values: Values; conclusion: string | null
  rendered: Rendered; createdBy: string; createdAt: string; updatedAt: string
  signedBy: string | null; signedByCredentials: string | null; signedAt: string | null
  addenda: { id: number; text: string; by: string; createdAt: string }[]
}

export type WorklistRow = {
  id: number; status: 'Draft' | 'Final'; examDate: string; patientName: string; mrn: string
  templateName: string; service: string; createdBy: string; updatedAt: string; signedBy: string | null; signedAt: string | null
}

export type User = { id: number; userName: string; displayName: string; credentials: string | null; role: Role; isActive: boolean }

export type Change = { at: string; user: string; field: string; value: string | null }

/** Thrown for any non-2xx response. `fieldErrors` is filled for 400 validation problems. */
export class ApiError extends Error {
  constructor(public status: number, message: string, public fieldErrors: Record<string, string[]> = {}) {
    super(message)
  }
}

/** Identifies this browser tab, so it can ignore the live broadcast of its own edits. */
export const clientId = crypto.randomUUID()

async function request<T>(method: string, url: string, body?: unknown): Promise<T> {
  const res = await fetch(url, {
    method,
    credentials: 'same-origin',
    headers: {
      'X-ClinicReport': '1',
      'X-Client-Id': clientId,
      ...(body !== undefined ? { 'Content-Type': 'application/json' } : {}),
    },
    body: body !== undefined ? JSON.stringify(body) : undefined,
  })
  if (res.status === 204) return undefined as T
  const text = await res.text()
  const data = text ? JSON.parse(text) : undefined
  if (!res.ok) {
    if (res.status === 401 && !url.startsWith('/api/auth/')) window.dispatchEvent(new Event('clinicreport:signed-out'))
    const message = data?.title && data?.detail ? `${data.title} ${data.detail}` : data?.title ?? data?.detail ?? `Request failed (${res.status})`
    throw new ApiError(res.status, message, data?.errors ?? {})
  }
  return data as T
}

const get = <T,>(url: string) => request<T>('GET', url)
const qs = (params: Record<string, string | undefined>) => {
  const p = new URLSearchParams()
  for (const [k, v] of Object.entries(params)) if (v) p.set(k, v)
  const s = p.toString()
  return s ? `?${s}` : ''
}

export const api = {
  settings: () => get<Settings>('/api/settings'),
  me: () => get<Me | undefined>('/api/auth/me'),
  login: (userName: string, password: string) => request<Me>('POST', '/api/auth/login', { userName, password }),
  demoLogin: (userName: string) => request<Me>('POST', '/api/auth/demo-login', { userName }),
  logout: () => request<void>('POST', '/api/auth/logout'),
  changePassword: (currentPassword: string, newPassword: string) => request<void>('POST', '/api/auth/password', { currentPassword, newPassword }),

  patients: (q: string) => get<Patient[]>(`/api/patients${qs({ q })}`),
  createPatient: (p: Omit<Patient, 'id'>) => request<Patient>('POST', '/api/patients', p),

  templates: () => get<TemplateSummary[]>('/api/templates'),
  template: (id: number) => get<TemplateDetail>(`/api/templates/${id}`),
  createTemplate: (definitionJson: string) => request<{ template: TemplateDetail; errors: string[] }>('POST', '/api/templates', { definitionJson }),
  saveDraft: (id: number, definitionJson: string) => request<{ template: TemplateDetail; errors: string[] }>('PUT', `/api/templates/${id}/draft`, { definitionJson }),
  discardDraft: (id: number) => request<void>('DELETE', `/api/templates/${id}/draft`),
  publish: (id: number) => request<TemplateDetail>('POST', `/api/templates/${id}/publish`),
  setTemplateActive: (id: number, isActive: boolean) => request<void>('PUT', `/api/templates/${id}/active`, { isActive }),
  preview: (definitionJson: string, values: Values, sex: string, heightCm: number | null, weightKg: number | null, ageYears: number | null) =>
    request<{ errors: string[]; rendered: Rendered | null }>('POST', '/api/templates/preview', { definitionJson, values, sex, heightCm, weightKg, ageYears }),

  worklist: (params: { status?: string; q?: string; from?: string; to?: string }) => get<WorklistRow[]>(`/api/reports${qs(params)}`),
  report: (id: number) => get<Report>(`/api/reports/${id}`),
  createReport: (body: { patientId: number; templateId: number } & Header) => request<Report>('POST', '/api/reports', body),
  setValues: (id: number, values: Record<string, Value | null>) => request<Report>('PATCH', `/api/reports/${id}/values`, { values }),
  applyPreset: (id: number, sectionId: string) => request<Report>('POST', `/api/reports/${id}/presets/${encodeURIComponent(sectionId)}`),
  setHeader: (id: number, header: Header) => request<Report>('PUT', `/api/reports/${id}/header`, header),
  setConclusion: (id: number, conclusion: string) => request<Report>('PUT', `/api/reports/${id}/conclusion`, { conclusion }),
  sign: (id: number) => request<Report>('POST', `/api/reports/${id}/sign`),
  addAddendum: (id: number, text: string) => request<Report>('POST', `/api/reports/${id}/addenda`, { text }),
  history: (id: number) => get<Change[]>(`/api/reports/${id}/history`),

  users: () => get<User[]>('/api/users'),
  createUser: (u: Omit<User, 'id' | 'isActive'> & { password: string }) => request<User>('POST', '/api/users', u),
  updateUser: (id: number, u: Pick<User, 'displayName' | 'credentials' | 'role' | 'isActive'>) => request<User>('PUT', `/api/users/${id}`, u),
  resetPassword: (id: number, password: string) => request<void>('POST', `/api/users/${id}/password`, { password }),
}

export function ageOn(dob: string, on: string): number {
  const d = new Date(dob), o = new Date(on)
  let age = o.getFullYear() - d.getFullYear()
  if (o.getMonth() < d.getMonth() || (o.getMonth() === d.getMonth() && o.getDate() < d.getDate())) age--
  return age
}

export function today(): string {
  const d = new Date()
  return `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}`
}

export function formatDateTime(iso: string | null | undefined): string {
  if (!iso) return ''
  return new Date(iso.endsWith('Z') ? iso : iso + 'Z').toLocaleString(undefined, { dateStyle: 'medium', timeStyle: 'short' })
}
