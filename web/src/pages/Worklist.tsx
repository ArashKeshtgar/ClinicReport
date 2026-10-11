import { useEffect, useState } from 'react'
import { Link, useNavigate } from 'react-router'
import { api, can, formatDateTime, type OrderRow, type WorklistRow } from '../api'
import { useSession } from '../App'

export default function Worklist() {
  const [rows, setRows] = useState<WorklistRow[] | null>(null)
  const [status, setStatus] = useState('Draft')
  const [q, setQ] = useState('')
  const [error, setError] = useState<string | null>(null)
  const navigate = useNavigate()
  const { me, settings } = useSession()

  useEffect(() => {
    const t = setTimeout(() => {
      api.worklist({ status: status || undefined, q: q || undefined })
        .then(r => { setRows(r); setError(null) })
        .catch(e => setError(e.message))
    }, 200)
    return () => clearTimeout(t)
  }, [status, q])

  return (
    <>
      <div className="page-head">
        <div>
          <h1>Worklist</h1>
          {me.services && <div className="muted small">Showing {me.services.join(', ')} only</div>}
        </div>
        {can(me, 'reports.create') && <Link to="/reports/new" className="button primary">New report</Link>}
      </div>

      <div className="toolbar">
        <div className="segmented" role="group" aria-label="Status">
          {[['Draft', 'In progress'], ['Final', 'Signed'], ['', 'All']].map(([value, label]) => (
            <button key={value} className={status === value ? 'on' : ''} onClick={() => setStatus(value)}>{label}</button>
          ))}
        </div>
        <input type="search" placeholder="Search last name, first name or MRN" value={q} onChange={e => setQ(e.target.value)} aria-label="Search" />
      </div>

      {settings.signIn === 'clinichub' && <Ordered />}

      {error && <p className="error">{error}</p>}
      {rows === null ? <p className="muted">Loading…</p> : rows.length === 0 ? (
        <div className="empty card">
          <p>No reports here.</p>
          {can(me, 'reports.create') && <Link to="/reports/new" className="button">Start a report</Link>}
        </div>
      ) : (
        <div className="table-wrap card">
          <table className="grid">
            <thead>
              <tr><th>Exam date</th><th>Patient</th><th>MRN</th><th>Study</th><th>Status</th><th>Last change</th></tr>
            </thead>
            <tbody>
              {rows.map(r => (
                <tr key={r.id} className="clickable" onClick={() => navigate(`/reports/${r.id}`)}>
                  <td>{r.examDate}</td>
                  <td><Link to={`/reports/${r.id}`} onClick={e => e.stopPropagation()}>{r.patientName}</Link></td>
                  <td className="mono">{r.mrn}</td>
                  <td>{r.templateName}</td>
                  <td>{r.status === 'Final'
                    ? <span className="badge ok" title={`Signed by ${r.signedBy}`}>Signed</span>
                    : <span className="badge">In progress</span>}</td>
                  <td className="muted">{formatDateTime(r.status === 'Final' ? r.signedAt : r.updatedAt)}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </>
  )
}

/** Exams ordered in ClinicHub and not started yet: starting one opens a draft with the order's indication and physician. */
function Ordered() {
  const { me } = useSession()
  const navigate = useNavigate()
  const [orders, setOrders] = useState<OrderRow[] | null>(null)
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    const load = () => api.orders().then(setOrders).catch(e => setError(e.message))
    load()
    const t = setInterval(load, 30_000)
    return () => clearInterval(t)
  }, [])

  const waiting = orders?.filter(o => o.status === 'Waiting') ?? []
  if (!orders || waiting.length === 0) return null
  return (
    <section className="card pad stack" style={{ marginBottom: '1rem' }}>
      <div className="section-head">
        <h2>Ordered from ClinicHub <span className="muted small">({waiting.length} waiting)</span></h2>
      </div>
      {error && <p className="error">{error}</p>}
      <div className="table-wrap">
        <table className="grid">
          <thead><tr><th>Received</th><th>Patient</th><th>MRN</th><th>Study</th><th>Indication</th><th>Ordered by</th><th></th></tr></thead>
          <tbody>
            {waiting.map(o => (
              <tr key={o.id}>
                <td className="muted nowrap">{formatDateTime(o.receivedAt)}{o.priority === 'Stat' && <span className="badge warn" style={{ marginLeft: '.4rem' }}>STAT</span>}</td>
                <td>{o.patient}</td>
                <td className="mono">{o.mrn}</td>
                <td>{o.template}</td>
                <td className="small">{o.indication ?? <span className="muted">—</span>}</td>
                <td className="small">{o.referringPhysician}</td>
                <td>{can(me, 'reports.create') && (
                  <button className="primary" onClick={async () => {
                    try { navigate(`/reports/${(await api.startOrder(o.id)).reportId}`) } catch (e) { setError(e instanceof Error ? e.message : 'Not started.') }
                  }}>Start report</button>
                )}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </section>
  )
}
