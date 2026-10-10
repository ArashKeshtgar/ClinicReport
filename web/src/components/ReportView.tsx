import type { Rendered } from '../api'

/** The report as the referring physician will read it: findings by section, measurements, conclusion. */
export function ReportView({ rendered, conclusion, compact }: { rendered: Rendered; conclusion?: string | null; compact?: boolean }) {
  const sections = rendered.sections.filter(s => s.sentences.length > 0)
  return (
    <div className={`report-view ${compact ? 'compact' : ''}`}>
      {sections.length === 0 && rendered.measurements.length === 0 && <p className="muted">Findings appear here as the form is filled.</p>}

      {sections.length > 0 && (
        <section>
          <h3>Findings</h3>
          {sections.map(s => (
            <p key={s.id} className="finding">
              <strong>{s.title}: </strong>{s.sentences.join(' ')}
            </p>
          ))}
        </section>
      )}

      {rendered.measurements.length > 0 && (
        <section>
          <h3>Measurements</h3>
          <table className="measurements">
            <thead><tr><th>Measurement</th><th className="num">Value</th><th>Unit</th><th>Normal</th><th></th></tr></thead>
            <tbody>
              {rendered.measurements.map(m => (
                <tr key={m.fieldId} className={m.flag ? 'flagged' : ''}>
                  <td>{m.label}</td>
                  <td className="num">{m.display}</td>
                  <td>{m.unit}</td>
                  <td className="muted">{m.range}</td>
                  <td>{m.flag && <span className={`flag ${m.flag}`}>{m.flag}</span>}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </section>
      )}

      {conclusion !== undefined && (
        <section>
          <h3>Conclusion</h3>
          {conclusion ? <p className="conclusion">{conclusion}</p> : <p className="muted">No conclusion yet.</p>}
        </section>
      )}
    </div>
  )
}
