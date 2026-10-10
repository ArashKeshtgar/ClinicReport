using ClinicReport.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace ClinicReport.Api.Auth;

/// <summary>Writes the security and privacy audit trail. Rows are only ever added.</summary>
public class SecurityLog(ClinicReportDbContext db, IHttpContextAccessor http, TimeProvider clock)
{
    /// <summary>Opening the same report again within this window is not logged again.</summary>
    public static readonly TimeSpan ViewDedupWindow = TimeSpan.FromMinutes(10);

    /// <summary>Adds an event to the context; it is saved with the caller's next SaveChanges.</summary>
    public void Add(string type, int? actor, int? target = null, string? detail = null, int? reportId = null, int? patientId = null) =>
        db.SecurityEvents.Add(new SecurityEvent
        {
            At = clock.GetUtcNow().UtcDateTime,
            Type = type,
            ActorUserId = actor,
            TargetUserId = target,
            ReportId = reportId,
            PatientId = patientId,
            Detail = detail is { Length: > 500 } ? detail[..500] : detail,
            IpAddress = http.HttpContext?.Connection.RemoteIpAddress?.ToString(),
        });

    public async Task WriteAsync(string type, int? actor, int? target = null, string? detail = null, int? reportId = null, int? patientId = null)
    {
        Add(type, actor, target, detail, reportId, patientId);
        await db.SaveChangesAsync();
    }

    /// <summary>Records that a user opened a patient's report (PHIPA/HIPAA access logging).</summary>
    public async Task ReportViewedAsync(int userId, int reportId, int patientId, CancellationToken ct)
    {
        var since = clock.GetUtcNow().UtcDateTime - ViewDedupWindow;
        if (await db.SecurityEvents.AnyAsync(e => e.Type == SecurityEventTypes.ReportViewed
                && e.ActorUserId == userId && e.ReportId == reportId && e.At >= since, ct))
            return;
        Add(SecurityEventTypes.ReportViewed, userId, reportId: reportId, patientId: patientId);
        await db.SaveChangesAsync(ct);
    }
}
