import { useEffect, useState } from 'react'
import { Link, useNavigate } from 'react-router'
import { api, formatDateTime, type WorklistRow } from '../api'

export default function Worklist() {
  const [rows, setRows] = useState<WorklistRow[] | null>(null)
  const [status, setStatus] = useState('Draft')
  const [q, setQ] = useState('')
  const [error, setError] = useState<string | null>(null)
  const navigate = useNavigate()

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
        <h1>Worklist</h1>
        <Link to="/reports/new" className="button primary">New report</Link>
      </div>

      <div className="toolbar">
        <div className="segmented" role="group" aria-label="Status">
          {[['Draft', 'In progress'], ['Final', 'Signed'], ['', 'All']].map(([value, label]) => (
            <button key={value} className={status === value ? 'on' : ''} onClick={() => setStatus(value)}>{label}</button>
          ))}
        </div>
        <input type="search" placeholder="Search last name, first name or MRN" value={q} onChange={e => setQ(e.target.value)} aria-label="Search" />
      </div>

      {error && <p className="error">{error}</p>}
      {rows === null ? <p className="muted">Loading…</p> : rows.length === 0 ? (
        <div className="empty card">
          <p>No reports here.</p>
          <Link to="/reports/new" className="button">Start a report</Link>
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
