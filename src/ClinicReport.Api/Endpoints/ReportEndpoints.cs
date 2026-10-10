using System.Security.Claims;
using System.Text.Json;
using ClinicReport.Api.Auth;
using ClinicReport.Api.Data;
using ClinicReport.Api.Reports;
using Microsoft.EntityFrameworkCore;

namespace ClinicReport.Api.Endpoints;

public static class ReportEndpoints
{
    /// <summary>Each browser tab sends a random id so it can ignore the broadcast of its own change.</summary>
    public const string ClientIdHeader = "X-Client-Id";

    public record WorklistRow(
        int Id, string Status, DateOnly ExamDate, string PatientName, string Mrn, string TemplateName, string Service,
        string CreatedBy, DateTime UpdatedAt, string? SignedBy, DateTime? SignedAt);

    public record CreateReport(int PatientId, int TemplateId, DateOnly ExamDate, string? Indication, string? ReferringPhysician, double? HeightCm, double? WeightKg);
    public record ValuesBody(Dictionary<string, JsonElement?> Values);
    public record ConclusionBody(string? Conclusion);
    public record AddendumBody(string Text);
    public record ChangeRow(DateTime At, string User, string Field, string? Value);

    public static void MapReportEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/reports").WithTags("Reports");

        api.MapGet("/", async (string? status, DateOnly? from, DateOnly? to, string? q, ClinicReportDbContext db) =>
        {
            var query = db.Reports.AsNoTracking();
            if (!string.IsNullOrEmpty(status))
                query = query.Where(r => r.Status == status);
            if (from is not null)
                query = query.Where(r => r.ExamDate >= from);
            if (to is not null)
                query = query.Where(r => r.ExamDate <= to);
            if (!string.IsNullOrWhiteSpace(q))
            {
                var term = q.Trim();
                query = query.Where(r => r.Patient.Mrn == term || r.Patient.LastName.StartsWith(term) || r.Patient.FirstName.StartsWith(term));
            }
            return await query
                .OrderBy(r => r.Status == ReportStatus.Draft ? 0 : 1).ThenByDescending(r => r.ExamDate).ThenByDescending(r => r.Id)
                .Take(200)
                .Select(r => new WorklistRow(
                    r.Id, r.Status, r.ExamDate, r.Patient.LastName + ", " + r.Patient.FirstName, r.Patient.Mrn,
                    r.TemplateVersion.Template.Name, r.TemplateVersion.Template.Service,
                    r.CreatedBy.DisplayName, r.UpdatedAt, r.SignedBy != null ? r.SignedBy.DisplayName : null, r.SignedAt))
                .ToListAsync();
        });

        api.MapPost("/", async (CreateReport req, ClaimsPrincipal user, ClinicReportDbContext db, ReportService reports, TimeProvider clock, CancellationToken ct) =>
        {
            var errors = new Dictionary<string, string[]>();
            if (!await db.Patients.AnyAsync(p => p.Id == req.PatientId, ct))
                errors["patientId"] = ["Pick a patient."];
            var version = await db.TemplateVersions
                .Where(v => v.TemplateId == req.TemplateId && v.Status == TemplateStatus.Published && v.Template.IsActive)
                .OrderByDescending(v => v.Version).FirstOrDefaultAsync(ct);
            if (version is null)
                errors["templateId"] = ["Pick an active, published template."];
            if (errors.Count > 0)
                return Results.ValidationProblem(errors);

            var now = clock.GetUtcNow().UtcDateTime;
            var report = new Report
            {
                PatientId = req.PatientId, TemplateVersionId = version!.Id, ExamDate = req.ExamDate,
                CreatedByUserId = user.RequiredUserId(), CreatedAt = now, UpdatedAt = now,
            };
            db.Reports.Add(report);
            await db.SaveChangesAsync(ct);

            // Header fields go through the service so they get the same checks and audit as later edits.
            var dto = await reports.SetHeaderAsync(report.Id,
                new HeaderDto(req.ExamDate, req.Indication, req.ReferringPhysician, req.HeightCm, req.WeightKg), user.RequiredUserId(), null, ct);
            return Results.Created($"/api/reports/{report.Id}", dto);
        });

        api.MapGet("/{id:int}", async (int id, ReportService reports, CancellationToken ct) =>
            await reports.LoadAsync(id, ct) is { } r ? Results.Ok(ReportService.ToDto(r)) : Results.NotFound());

        api.MapPatch("/{id:int}/values", (int id, ValuesBody body, ClaimsPrincipal user, HttpRequest http, ReportService reports, CancellationToken ct) =>
            reports.SetValuesAsync(id, body.Values ?? [], user.RequiredUserId(), ClientId(http), ct));

        api.MapPost("/{id:int}/presets/{sectionId}", (int id, string sectionId, ClaimsPrincipal user, HttpRequest http, ReportService reports, CancellationToken ct) =>
            reports.ApplyPresetAsync(id, sectionId, user.RequiredUserId(), ClientId(http), ct));

        api.MapPut("/{id:int}/header", (int id, HeaderDto body, ClaimsPrincipal user, HttpRequest http, ReportService reports, CancellationToken ct) =>
            reports.SetHeaderAsync(id, body, user.RequiredUserId(), ClientId(http), ct));

        api.MapPut("/{id:int}/conclusion", (int id, ConclusionBody body, ClaimsPrincipal user, HttpRequest http, ReportService reports, CancellationToken ct) =>
            reports.SetConclusionAsync(id, body.Conclusion, user.RequiredUserId(), ClientId(http), ct));

        api.MapPost("/{id:int}/sign", (int id, ClaimsPrincipal user, HttpRequest http, ReportService reports, CancellationToken ct) =>
            reports.SignAsync(id, user.RequiredUserId(), ClientId(http), ct))
            .RequireAuthorization(Policies.Sign);

        api.MapPost("/{id:int}/addenda", (int id, AddendumBody body, ClaimsPrincipal user, ReportService reports, CancellationToken ct) =>
            reports.AddAddendumAsync(id, body.Text, user.RequiredUserId(), ct))
            .RequireAuthorization(Policies.Sign);

        api.MapGet("/{id:int}/history", async (int id, ClinicReportDbContext db, CancellationToken ct) =>
            await db.Changes.AsNoTracking().Where(c => c.ReportId == id).OrderByDescending(c => c.Id).Take(500)
                .Join(db.Users, c => c.UserId, u => u.Id, (c, u) => new ChangeRow(c.At, u.DisplayName, c.Field, c.NewValueJson))
                .ToListAsync(ct));
    }

    private static string? ClientId(HttpRequest http) =>
        http.Headers[ClientIdHeader].FirstOrDefault() is { Length: > 0 and <= 64 } id ? id : null;
}
