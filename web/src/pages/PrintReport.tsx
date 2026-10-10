import { useEffect, useState } from 'react'
import { Link, useParams } from 'react-router'
import { ageOn, api, formatDateTime, type Report } from '../api'
import { useSession } from '../App'
import { ReportView } from '../components/ReportView'

/** Paper layout. The browser's "Save as PDF" produces the PDF. */
export default function PrintReport() {
  const id = Number(useParams().id)
  const { settings } = useSession()
  const [report, setReport] = useState<Report | null>(null)

  useEffect(() => { api.report(id).then(setReport) }, [id])
  if (!report) return <p className="muted pad">Loading…</p>

  const p = report.patient
  const h = report.header
  return (
    <div className="print-page">
      <div className="print-actions no-print">
        <Link to={`/reports/${id}`}>← Back to report</Link>
        <button className="primary" onClick={() => window.print()}>Print / Save as PDF</button>
      </div>

      <article className="paper">
        {report.status !== 'Final' && <div className="watermark">DRAFT – NOT SIGNED</div>}
        <header className="paper-head">
          <div>
            <div className="clinic-name">{settings.name}</div>
            {settings.address && <div>{settings.address}</div>}
            {settings.phone && <div>{settings.phone}</div>}
          </div>
          <h1>{report.templateName}</h1>
        </header>

        <table className="paper-meta">
          <tbody>
            <tr>
              <th>Patient</th><td>{p.lastName}, {p.firstName}</td>
              <th>MRN</th><td>{p.mrn}</td>
            </tr>
            <tr>
              <th>Date of birth</th><td>{p.dateOfBirth} ({ageOn(p.dateOfBirth, h.examDate)} y, {p.sex})</td>
              <th>Exam date</th><td>{h.examDate}</td>
            </tr>
            <tr>
              <th>Referring</th><td>{h.referringPhysician ?? '—'}</td>
              <th>Height / weight</th><td>{h.heightCm ? `${h.heightCm} cm` : '—'} / {h.weightKg ? `${h.weightKg} kg` : '—'}</td>
            </tr>
            {h.indication && <tr><th>Indication</th><td colSpan={3}>{h.indication}</td></tr>}
          </tbody>
        </table>

        <ReportView rendered={report.rendered} conclusion={report.conclusion} />

        <footer className="paper-sign">
          {report.status === 'Final' ? (
            <>
              <div>Electronically signed by <strong>{report.signedBy}</strong>{report.signedByCredentials ? `, ${report.signedByCredentials}` : ''}</div>
              <div className="muted">{formatDateTime(report.signedAt)}</div>
            </>
          ) : <div className="muted">Not signed.</div>}
        </footer>

        {report.addenda.length > 0 && (
          <section className="paper-addenda">
            <h3>Addenda</h3>
            {report.addenda.map(a => (
              <div key={a.id}><p>{a.text}</p><div className="muted small">{a.by} · {formatDateTime(a.createdAt)}</div></div>
            ))}
          </section>
        )}

        {settings.reportFooter && <div className="paper-footer">{settings.reportFooter}</div>}
      </article>
    </div>
  )
}
