using System.IO.Compression;
using System.Text.Json;
using ClinicReport.Api.Data;
using ClinicReport.Api.Domain;
using Microsoft.EntityFrameworkCore;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace ClinicReport.Api.Reports;

/// <summary>
/// Everything a clinic has in ClinicReport as one zip, so it can leave: JSON for its patients, reports, addenda, users
/// and audit trail, its templates, and a PDF of every signed report. File names carry Ids, never a patient's name.
/// Passwords and two-factor secrets stay behind.
/// </summary>
public static class TenantExport
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static async Task WriteAsync(ClinicReportDbContext db, ClinicOptions clinic, Stream output, DateTime now, CancellationToken ct)
    {
        using var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
        var patients = await db.Patients.AsNoTracking().OrderBy(p => p.Id).ToListAsync(ct);
        var users = await db.Users.AsNoTracking().OrderBy(u => u.Id)
            .Select(u => new { u.Id, u.UserName, u.DisplayName, u.Credentials, u.IsActive, u.CreatedAt }).ToListAsync(ct);
        var reports = await db.Reports.AsNoTracking().Include(r => r.Addenda).OrderBy(r => r.Id).ToListAsync(ct);
        var versions = await db.TemplateVersions.AsNoTracking().Include(v => v.Template).OrderBy(v => v.Id).ToListAsync(ct);

        Add(zip, "patients.json", patients);
        Add(zip, "users.json", users);
        Add(zip, "reports.json", reports.Select(r => new
        {
            r.Id, r.PatientId, r.TemplateVersionId, r.Status, r.ExamDate, r.Indication, r.ReferringPhysician, r.HeightCm, r.WeightKg,
            Values = JsonSerializer.Deserialize<JsonElement>(r.ValuesJson), r.Conclusion, r.CreatedByUserId, r.CreatedAt, r.UpdatedAt,
            r.SignedByUserId, r.SignedAt, Addenda = r.Addenda.Select(a => new { a.Id, a.Text, a.UserId, a.CreatedAt }),
        }));
        Add(zip, "audit.json", new
        {
            SecurityEvents = await db.SecurityEvents.AsNoTracking().OrderBy(e => e.Id).ToListAsync(ct),
            ReportChanges = await db.Changes.AsNoTracking().OrderBy(c => c.Id).ToListAsync(ct),
        });
        foreach (var v in versions)
            AddText(zip, $"templates/{v.Template.Key}.v{v.Version}.json", v.DefinitionJson);

        var names = users.ToDictionary(u => u.Id, u => (u.DisplayName, u.Credentials));
        var signed = reports.Where(r => r.SignedSnapshotJson is not null).ToList();
        foreach (var report in signed)
        {
            ct.ThrowIfCancellationRequested();
            var rendered = JsonSerializer.Deserialize<RenderedReport>(report.SignedSnapshotJson!, TemplateDefinition.JsonOptions)!;
            var patient = patients.Single(p => p.Id == report.PatientId);
            var signer = report.SignedByUserId is { } id ? names.GetValueOrDefault(id) : default;
            var version = versions.Single(v => v.Id == report.TemplateVersionId);
            var pdf = ReportPdf.Render(report, patient, version.Template.Name, rendered, signer.DisplayName, signer.Credentials, clinic);
            await using var entry = zip.CreateEntry($"reports-pdf/report-{report.Id}.pdf").Open();
            await entry.WriteAsync(pdf, ct);
        }

        Add(zip, "manifest.json", new
        {
            Clinic = clinic.Name, ExportedAt = now, Patients = patients.Count, Reports = reports.Count, SignedReports = signed.Count, Templates = versions.Count,
        });
    }

    private static void Add<T>(ZipArchive zip, string name, T value) => AddText(zip, name, JsonSerializer.Serialize(value, Json));

    private static void AddText(ZipArchive zip, string name, string text)
    {
        using var writer = new StreamWriter(zip.CreateEntry(name).Open());
        writer.Write(text);
    }
}

/// <summary>A signed report as a PDF, from the snapshot frozen at signing: what the physician signed, not today's template.</summary>
public static class ReportPdf
{
    static ReportPdf() => QuestPDF.Settings.License = LicenseType.Community;

    public static byte[] Render(Report report, Patient patient, string title, RenderedReport rendered, string? signedBy, string? credentials, ClinicOptions clinic) =>
        Document.Create(document => document.Page(page =>
        {
            page.Size(PageSizes.Letter);
            page.Margin(40);
            page.DefaultTextStyle(s => s.FontSize(10));
            page.Header().Column(header =>
            {
                header.Item().Text(clinic.Name).FontSize(14).Bold();
                if (clinic.Address is { Length: > 0 } address)
                    header.Item().Text(address);
                if (clinic.Phone is { Length: > 0 } phone)
                    header.Item().Text(phone);
            });
            page.Content().PaddingVertical(12).Column(content =>
            {
                content.Spacing(6);
                content.Item().Text(title).FontSize(12).Bold();
                content.Item().Text($"{patient.LastName}, {patient.FirstName}   MRN {patient.Mrn}   Born {patient.DateOfBirth:yyyy-MM-dd}   Sex {patient.Sex}");
                content.Item().Text($"Exam {report.ExamDate:yyyy-MM-dd}" + (report.ReferringPhysician is { Length: > 0 } referrer ? $"   Referred by {referrer}" : ""));
                if (report.Indication is { Length: > 0 } indication)
                    content.Item().Text($"Indication: {indication}");
                foreach (var section in rendered.Sections.Where(s => s.Sentences.Count > 0))
                {
                    content.Item().PaddingTop(6).Text(section.Title).Bold();
                    content.Item().Text(string.Join(" ", section.Sentences));
                }
                if (rendered.Measurements.Count > 0)
                {
                    content.Item().PaddingTop(6).Text("Measurements").Bold();
                    foreach (var m in rendered.Measurements)
                        content.Item().Text($"{m.Label}: {m.Display} {m.Unit} {(m.Range is null ? "" : $"({m.Range})")} {m.Flag}".TrimEnd());
                }
                content.Item().PaddingTop(6).Text("Conclusion").Bold();
                content.Item().Text(report.Conclusion ?? "");
                content.Item().PaddingTop(6).Text($"Signed by {signedBy}{(credentials is { Length: > 0 } ? $", {credentials}" : "")} on {report.SignedAt:yyyy-MM-dd HH:mm} UTC");
                foreach (var addendum in report.Addenda.OrderBy(a => a.CreatedAt))
                    content.Item().PaddingTop(6).Text($"Addendum {addendum.CreatedAt:yyyy-MM-dd HH:mm} UTC: {addendum.Text}");
            });
            page.Footer().Column(footer =>
            {
                if (clinic.ReportFooter is { Length: > 0 } text)
                    footer.Item().Text(text).FontSize(8);
                footer.Item().AlignRight().Text(t => { t.CurrentPageNumber(); t.Span(" / "); t.TotalPages(); });
            });
        })).GeneratePdf();
}
