using System.Globalization;
using System.Text.Json;
using ClinicReport.Api.Data;
using ClinicReport.Api.Domain;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace ClinicReport.Api.Reports;

public record PatientDto(int Id, string Mrn, string FirstName, string LastName, DateOnly DateOfBirth, string Sex);

public record HeaderDto(DateOnly ExamDate, string? Indication, string? ReferringPhysician, double? HeightCm, double? WeightKg);

public record AddendumDto(int Id, string Text, string By, DateTime CreatedAt);

public record ReportDto(
    int Id, string Status, int Revision, PatientDto Patient,
    int TemplateId, int TemplateVersionId, int TemplateVersion, string TemplateName, string Service,
    JsonElement Definition, HeaderDto Header, JsonElement Values, string? Conclusion,
    RenderedReport Rendered, string CreatedBy, DateTime CreatedAt, DateTime UpdatedAt,
    string? SignedBy, string? SignedByCredentials, DateTime? SignedAt, IReadOnlyList<AddendumDto> Addenda);

/// <summary>Raised for requests that are well-formed but not allowed in the report's current state.</summary>
public class ReportRuleException(string message, int status = StatusCodes.Status409Conflict) : Exception(message)
{
    public int Status { get; } = status;
}

/// <summary>
/// Every change to a report goes through here: it checks the answers against the template, records who
/// changed what, saves with optimistic concurrency (two people typing at once never lose each other's
/// fields), and pushes the new state to everyone who has the report open.
/// </summary>
public class ReportService(ClinicReportDbContext db, IHubContext<ReportHub> hub, TimeProvider clock)
{
    private const int MaxTextLength = 4000;
    private const int MaxSaveAttempts = 5;

    public async Task<Report?> LoadAsync(int id, CancellationToken ct) =>
        await db.Reports
            .Include(r => r.Patient)
            .Include(r => r.TemplateVersion).ThenInclude(v => v.Template)
            .Include(r => r.CreatedBy)
            .Include(r => r.SignedBy)
            .Include(r => r.Addenda.OrderBy(a => a.CreatedAt)).ThenInclude(a => a.User)
            .AsSplitQuery()
            .SingleOrDefaultAsync(r => r.Id == id, ct);

    public static RenderContext ContextFor(Report r)
    {
        var dob = r.Patient.DateOfBirth;
        var age = r.ExamDate.Year - dob.Year - (r.ExamDate < dob.AddYears(r.ExamDate.Year - dob.Year) ? 1 : 0);
        return new RenderContext(r.Patient.Sex, r.HeightCm, r.WeightKg, age);
    }

    public static ReportDto ToDto(Report r)
    {
        var def = TemplateDefinition.Parse(r.TemplateVersion.DefinitionJson);
        var values = ReportValues.FromJson(r.ValuesJson);
        var rendered = r.SignedSnapshotJson is not null
            ? JsonSerializer.Deserialize<RenderedReport>(r.SignedSnapshotJson, TemplateDefinition.JsonOptions)!
            : ReportRenderer.Render(def, values, ContextFor(r));

        return new ReportDto(
            r.Id, r.Status, r.Revision,
            new PatientDto(r.Patient.Id, r.Patient.Mrn, r.Patient.FirstName, r.Patient.LastName, r.Patient.DateOfBirth, r.Patient.Sex),
            r.TemplateVersion.TemplateId, r.TemplateVersionId, r.TemplateVersion.Version, r.TemplateVersion.Template.Name, r.TemplateVersion.Template.Service,
            JsonDocument.Parse(r.TemplateVersion.DefinitionJson).RootElement.Clone(),
            new HeaderDto(r.ExamDate, r.Indication, r.ReferringPhysician, r.HeightCm, r.WeightKg),
            JsonDocument.Parse(r.ValuesJson).RootElement.Clone(),
            r.Conclusion, rendered, r.CreatedBy.DisplayName, r.CreatedAt, r.UpdatedAt,
            r.SignedBy?.DisplayName, r.SignedBy?.Credentials, r.SignedAt,
            r.Addenda.Select(a => new AddendumDto(a.Id, a.Text, a.User.DisplayName, a.CreatedAt)).ToList());
    }

    /// <summary>Applies answers (null clears). Returns the saved report.</summary>
    public Task<ReportDto> SetValuesAsync(int id, IReadOnlyDictionary<string, JsonElement?> changes, int userId, string? clientId, CancellationToken ct) =>
        MutateAsync(id, userId, clientId, ct, (r, log) =>
        {
            var def = TemplateDefinition.Parse(r.TemplateVersion.DefinitionJson);
            var fields = def.AllFields().ToDictionary(f => f.Id);
            var values = ReportValues.FromJson(r.ValuesJson);
            foreach (var (fieldId, value) in changes)
            {
                if (!fields.TryGetValue(fieldId, out var field))
                    throw new ReportRuleException($"Unknown field \"{fieldId}\".", StatusCodes.Status400BadRequest);
                var normalized = Normalize(field, value);
                values.Set(fieldId, normalized);
                log(fieldId, normalized);
            }
            r.ValuesJson = values.ToJson();
        });

    /// <summary>Fills a section with its "All normal" answers, leaving fields it does not mention alone.</summary>
    public Task<ReportDto> ApplyPresetAsync(int id, string sectionId, int userId, string? clientId, CancellationToken ct) =>
        MutateAsync(id, userId, clientId, ct, (r, log) =>
        {
            var def = TemplateDefinition.Parse(r.TemplateVersion.DefinitionJson);
            var section = def.Sections.FirstOrDefault(s => s.Id == sectionId)
                ?? throw new ReportRuleException($"Unknown section \"{sectionId}\".", StatusCodes.Status404NotFound);
            if (section.NormalPreset is null)
                throw new ReportRuleException($"Section \"{section.Title}\" has no normal preset.", StatusCodes.Status400BadRequest);
            var values = ReportValues.FromJson(r.ValuesJson);
            foreach (var (fieldId, value) in section.NormalPreset)
            {
                values.Set(fieldId, value);
                log(fieldId, value);
            }
            r.ValuesJson = values.ToJson();
        });

    public Task<ReportDto> SetHeaderAsync(int id, HeaderDto header, int userId, string? clientId, CancellationToken ct) =>
        MutateAsync(id, userId, clientId, ct, (r, log) =>
        {
            if (header.HeightCm is < 30 or > 250)
                throw new ReportRuleException("Height must be between 30 and 250 cm.", StatusCodes.Status400BadRequest);
            if (header.WeightKg is < 1 or > 400)
                throw new ReportRuleException("Weight must be between 1 and 400 kg.", StatusCodes.Status400BadRequest);
            r.ExamDate = header.ExamDate;
            r.Indication = Trim(header.Indication, 500);
            r.ReferringPhysician = Trim(header.ReferringPhysician, 128);
            r.HeightCm = header.HeightCm;
            r.WeightKg = header.WeightKg;
            log("$header", JsonSerializer.SerializeToElement(header, TemplateDefinition.JsonOptions));
        });

    public Task<ReportDto> SetConclusionAsync(int id, string? conclusion, int userId, string? clientId, CancellationToken ct) =>
        MutateAsync(id, userId, clientId, ct, (r, log) =>
        {
            r.Conclusion = Trim(conclusion, MaxTextLength);
            log("$conclusion", r.Conclusion is null ? null : JsonSerializer.SerializeToElement(r.Conclusion));
        });

    /// <summary>Freezes the report. Required fields must be answered and a conclusion written.</summary>
    public Task<ReportDto> SignAsync(int id, int userId, string? clientId, CancellationToken ct) =>
        MutateAsync(id, userId, clientId, ct, (r, log) =>
        {
            var rendered = ReportRenderer.Render(
                TemplateDefinition.Parse(r.TemplateVersion.DefinitionJson), ReportValues.FromJson(r.ValuesJson), ContextFor(r));
            if (rendered.MissingRequired.Count > 0)
                throw new ReportRuleException("Answer the required fields first: " + string.Join("; ", rendered.MissingRequired) + ".", StatusCodes.Status422UnprocessableEntity);
            if (string.IsNullOrWhiteSpace(r.Conclusion))
                throw new ReportRuleException("Write a conclusion before signing.", StatusCodes.Status422UnprocessableEntity);

            r.Status = ReportStatus.Final;
            r.SignedByUserId = userId;
            r.SignedAt = clock.GetUtcNow().UtcDateTime;
            r.SignedSnapshotJson = JsonSerializer.Serialize(rendered, TemplateDefinition.JsonOptions);
            log("$status", JsonSerializer.SerializeToElement(ReportStatus.Final));
            db.SecurityEvents.Add(new SecurityEvent
            {
                At = r.SignedAt.Value, Type = SecurityEventTypes.ReportSigned, ActorUserId = userId, ReportId = r.Id, PatientId = r.PatientId,
            });
        });

    public async Task<ReportDto> AddAddendumAsync(int id, string text, int userId, CancellationToken ct)
    {
        text = text?.Trim() ?? "";
        if (text.Length is 0 or > MaxTextLength)
            throw new ReportRuleException($"An addendum needs 1 to {MaxTextLength} characters.", StatusCodes.Status400BadRequest);

        var report = await db.Reports.SingleOrDefaultAsync(r => r.Id == id, ct)
            ?? throw new ReportRuleException("Report not found.", StatusCodes.Status404NotFound);
        if (report.Status != ReportStatus.Final)
            throw new ReportRuleException("Addenda are for signed reports; edit the draft instead.");

        var now = clock.GetUtcNow().UtcDateTime;
        db.Addenda.Add(new ReportAddendum { ReportId = id, Text = text, UserId = userId, CreatedAt = now });
        db.Changes.Add(new ReportChange { ReportId = id, Field = "$addendum", NewValueJson = JsonSerializer.Serialize(text), UserId = userId, At = now });
        report.Revision++;
        report.UpdatedAt = now;
        await db.SaveChangesAsync(ct);

        var dto = ToDto((await LoadAsync(id, ct))!);
        await BroadcastAsync(dto, null, ct);
        return dto;
    }

    private async Task<ReportDto> MutateAsync(int id, int userId, string? clientId, CancellationToken ct,
        Action<Report, Action<string, JsonElement?>> change)
    {
        for (var attempt = 1; ; attempt++)
        {
            var report = await LoadAsync(id, ct)
                ?? throw new ReportRuleException("Report not found.", StatusCodes.Status404NotFound);
            if (report.Status != ReportStatus.Draft)
                throw new ReportRuleException("This report is signed. Add an addendum instead of editing it.");

            var now = clock.GetUtcNow().UtcDateTime;
            var log = new List<ReportChange>();
            change(report, (field, value) => log.Add(new ReportChange
            {
                ReportId = id, Field = field, UserId = userId, At = now,
                NewValueJson = value is { } v ? v.GetRawText() : null,
            }));
            report.Revision++;
            report.UpdatedAt = now;
            db.Changes.AddRange(log);

            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateConcurrencyException) when (attempt < MaxSaveAttempts)
            {
                // Someone else saved between our read and write: start over from their version.
                db.ChangeTracker.Clear();
                continue;
            }

            if (report.SignedByUserId is not null && report.SignedBy is null)
                await db.Entry(report).Reference(r => r.SignedBy).LoadAsync(ct);
            var dto = ToDto(report);
            await BroadcastAsync(dto, clientId, ct);
            return dto;
        }
    }

    private Task BroadcastAsync(ReportDto dto, string? clientId, CancellationToken ct) =>
        hub.Clients.Group(ReportHub.GroupName(dto.Id)).SendAsync("reportChanged", new { clientId, report = dto }, ct);

    /// <summary>Checks a value fits its field and returns it in canonical form (numbers as numbers).</summary>
    public static JsonElement? Normalize(FieldDef field, JsonElement? value)
    {
        if (value is not { } v || ReportValues.IsBlank(v))
            return null;

        switch (field.Type)
        {
            case FieldTypes.Choice:
                if (v.ValueKind != JsonValueKind.String || field.Options.All(o => o.Value != v.GetString()))
                    throw Bad(field, "is not one of its options");
                return v;

            case FieldTypes.Multi:
                if (v.ValueKind != JsonValueKind.Array)
                    throw Bad(field, "must be a list");
                var picked = v.EnumerateArray().Select(e => e.ValueKind == JsonValueKind.String ? e.GetString() : null).ToList();
                if (picked.Any(p => p is null || field.Options.All(o => o.Value != p)))
                    throw Bad(field, "contains a value that is not one of its options");
                // Keep template order and drop duplicates so the narrative reads the same however it was clicked.
                var ordered = field.Options.Select(o => o.Value).Where(picked.Contains).ToArray();
                return JsonSerializer.SerializeToElement(ordered);

            case FieldTypes.Number:
                double number;
                if (v.ValueKind == JsonValueKind.Number)
                    number = v.GetDouble();
                else if (v.ValueKind != JsonValueKind.String
                    || !double.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out number))
                    throw Bad(field, "must be a number");
                if (double.IsNaN(number) || Math.Abs(number) > 1_000_000)
                    throw Bad(field, "is out of range");
                return JsonSerializer.SerializeToElement(number);

            case FieldTypes.Text or FieldTypes.TextArea:
                if (v.ValueKind != JsonValueKind.String)
                    throw Bad(field, "must be text");
                if (v.GetString()!.Length > MaxTextLength)
                    throw Bad(field, $"is longer than {MaxTextLength} characters");
                return v;

            default:
                throw Bad(field, "is calculated and cannot be typed");
        }
    }

    private static ReportRuleException Bad(FieldDef f, string problem) =>
        new($"\"{f.Label}\" {problem}.", StatusCodes.Status400BadRequest);

    private static string? Trim(string? s, int max)
    {
        s = s?.Trim();
        if (string.IsNullOrEmpty(s))
            return null;
        if (s.Length > max)
            throw new ReportRuleException($"Text is longer than {max} characters.", StatusCodes.Status400BadRequest);
        return s;
    }
}
