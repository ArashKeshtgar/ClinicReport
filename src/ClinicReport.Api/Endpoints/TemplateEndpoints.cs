using System.Security.Claims;
using System.Text;
using System.Text.Json;
using ClinicReport.Api.Auth;
using ClinicReport.Api.Data;
using ClinicReport.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace ClinicReport.Api.Endpoints;

/// <summary>
/// Template library and designer. Editing happens on a Draft version; publishing freezes it and makes it
/// the version new reports use. Reports already typed keep the version they were started on.
/// </summary>
public static class TemplateEndpoints
{
    public record TemplateSummary(
        int Id, string Key, string Name, string Service, bool IsActive,
        int? PublishedVersionId, int? PublishedVersion, int? DraftVersionId);

    public record VersionInfo(int Id, int Version, string Status, DateTime CreatedAt, DateTime? PublishedAt);

    public record TemplateDetail(
        int Id, string Key, string Name, string Service, bool IsActive, IReadOnlyList<VersionInfo> Versions,
        int? PublishedVersionId, JsonElement? Published, int? DraftVersionId, string? DraftJson, IReadOnlyList<string> DraftErrors);

    public record DefinitionBody(string DefinitionJson);
    public record SaveResult(TemplateDetail Template, IReadOnlyList<string> Errors);
    public record PreviewRequest(string DefinitionJson, Dictionary<string, JsonElement>? Values, string? Sex, double? HeightCm, double? WeightKg, double? AgeYears);
    public record PreviewResult(IReadOnlyList<string> Errors, RenderedReport? Rendered);
    public record ActiveBody(bool IsActive);

    public static void MapTemplateEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/templates").WithTags("Templates");
        var admin = api.MapGroup("").RequireAuthorization(Policies.Admin);

        api.MapGet("/", async (ClaimsPrincipal user, ClinicReportDbContext db) =>
        {
            var list = await db.Templates.AsNoTracking().Include(t => t.Versions).OrderBy(t => t.Service).ThenBy(t => t.Name).ToListAsync();
            var all = user.IsInRole(Roles.Admin);
            return list.Select(Summarize).Where(t => all || (t.IsActive && t.PublishedVersionId is not null));
        });

        api.MapGet("/{id:int}", async (int id, ClinicReportDbContext db) =>
            await LoadDetail(db, id) is { } d ? Results.Ok(d) : Results.NotFound());

        api.MapGet("/{id:int}/export", async (int id, ClinicReportDbContext db) =>
        {
            var v = await db.TemplateVersions.AsNoTracking().Include(v => v.Template)
                .Where(v => v.TemplateId == id && v.Status == TemplateStatus.Published)
                .OrderByDescending(v => v.Version).FirstOrDefaultAsync();
            return v is null
                ? Results.NotFound()
                : Results.File(Encoding.UTF8.GetBytes(v.DefinitionJson), "application/json", $"{v.Template.Key}.v{v.Version}.json");
        });

        admin.MapPost("/", async (DefinitionBody body, ClinicReportDbContext db, TimeProvider clock) =>
        {
            if (Parse(body.DefinitionJson, out var def) is { } bad)
                return bad;
            if (await db.Templates.AnyAsync(t => t.Key == def!.Key))
                return Results.Problem($"A template with key \"{def!.Key}\" exists. Open it and edit its draft instead.", statusCode: StatusCodes.Status409Conflict);

            var t = new ReportTemplate { Key = def!.Key, Name = def.Name, Service = def.Service };
            t.Versions.Add(new TemplateVersion { Version = 1, Status = TemplateStatus.Draft, DefinitionJson = def.ToJson(), CreatedAt = clock.GetUtcNow().UtcDateTime });
            db.Templates.Add(t);
            await db.SaveChangesAsync();
            return Results.Created($"/api/templates/{t.Id}", new SaveResult((await LoadDetail(db, t.Id))!, TemplateValidator.Validate(def)));
        });

        // Saves work in progress. It may still have problems; they come back in Errors and block publishing.
        admin.MapPut("/{id:int}/draft", async (int id, DefinitionBody body, ClinicReportDbContext db, TimeProvider clock) =>
        {
            var t = await db.Templates.Include(t => t.Versions).SingleOrDefaultAsync(t => t.Id == id);
            if (t is null)
                return Results.NotFound();
            if (Parse(body.DefinitionJson, out var def) is { } bad)
                return bad;
            if (def!.Key != t.Key)
                return Results.Problem($"The key must stay \"{t.Key}\"; reports and exports refer to it.", statusCode: StatusCodes.Status400BadRequest);

            var draft = t.Versions.FirstOrDefault(v => v.Status == TemplateStatus.Draft);
            if (draft is null)
            {
                draft = new TemplateVersion
                {
                    Version = t.Versions.Count == 0 ? 1 : t.Versions.Max(v => v.Version) + 1,
                    Status = TemplateStatus.Draft, CreatedAt = clock.GetUtcNow().UtcDateTime,
                };
                t.Versions.Add(draft);
            }
            draft.DefinitionJson = def.ToJson();
            await db.SaveChangesAsync();
            return Results.Ok(new SaveResult((await LoadDetail(db, id))!, TemplateValidator.Validate(def)));
        });

        admin.MapDelete("/{id:int}/draft", async (int id, ClinicReportDbContext db) =>
        {
            var draft = await db.TemplateVersions.SingleOrDefaultAsync(v => v.TemplateId == id && v.Status == TemplateStatus.Draft);
            if (draft is null)
                return Results.NotFound();
            if (await db.Reports.AnyAsync(r => r.TemplateVersionId == draft.Id))
                return Results.Problem("Reports use this version.", statusCode: StatusCodes.Status409Conflict);
            db.TemplateVersions.Remove(draft);
            await db.SaveChangesAsync();
            return Results.NoContent();
        });

        admin.MapPost("/{id:int}/publish", async (int id, ClaimsPrincipal user, ClinicReportDbContext db, TimeProvider clock) =>
        {
            var t = await db.Templates.Include(t => t.Versions).SingleOrDefaultAsync(t => t.Id == id);
            var draft = t?.Versions.FirstOrDefault(v => v.Status == TemplateStatus.Draft);
            if (t is null || draft is null)
                return Results.NotFound();
            var errors = TemplateValidator.Validate(draft.DefinitionJson);
            if (errors.Count > 0)
                return Results.Problem(string.Join("\n", errors), title: "Fix the template before publishing.", statusCode: StatusCodes.Status422UnprocessableEntity);

            var def = TemplateDefinition.Parse(draft.DefinitionJson);
            draft.Status = TemplateStatus.Published;
            draft.PublishedAt = clock.GetUtcNow().UtcDateTime;
            draft.PublishedByUserId = user.RequiredUserId();
            t.Name = def.Name;
            t.Service = def.Service;
            await db.SaveChangesAsync();
            return Results.Ok(await LoadDetail(db, id));
        });

        admin.MapPut("/{id:int}/active", async (int id, ActiveBody body, ClinicReportDbContext db) =>
        {
            var t = await db.Templates.FindAsync(id);
            if (t is null)
                return Results.NotFound();
            t.IsActive = body.IsActive;
            await db.SaveChangesAsync();
            return Results.NoContent();
        });

        // Live preview for the designer: validates and renders sample answers without saving anything.
        admin.MapPost("/preview", (PreviewRequest req) =>
        {
            var errors = TemplateValidator.Validate(req.DefinitionJson ?? "");
            if (errors.Count > 0)
                return new PreviewResult(errors, null);
            var def = TemplateDefinition.Parse(req.DefinitionJson!);
            var rendered = ReportRenderer.Render(def, new ReportValues(req.Values), new RenderContext(req.Sex, req.HeightCm, req.WeightKg, req.AgeYears));
            return new PreviewResult([], rendered);
        });
    }

    private static IResult? Parse(string? json, out TemplateDefinition? def)
    {
        def = null;
        if (string.IsNullOrWhiteSpace(json) || json.Length > 1_000_000)
            return Results.Problem("Template JSON is empty or too large.", statusCode: StatusCodes.Status400BadRequest);
        try
        {
            def = TemplateDefinition.Parse(json);
        }
        catch (JsonException ex)
        {
            return Results.Problem(ex.Message, title: "Not valid template JSON.", statusCode: StatusCodes.Status400BadRequest);
        }
        if (string.IsNullOrWhiteSpace(def.Key))
            return Results.Problem("The template needs a key.", statusCode: StatusCodes.Status400BadRequest);
        return null;
    }

    private static TemplateSummary Summarize(ReportTemplate t)
    {
        var published = t.Versions.Where(v => v.Status == TemplateStatus.Published).MaxBy(v => v.Version);
        var draft = t.Versions.FirstOrDefault(v => v.Status == TemplateStatus.Draft);
        return new TemplateSummary(t.Id, t.Key, t.Name, t.Service, t.IsActive, published?.Id, published?.Version, draft?.Id);
    }

    private static async Task<TemplateDetail?> LoadDetail(ClinicReportDbContext db, int id)
    {
        var t = await db.Templates.AsNoTracking().Include(t => t.Versions).SingleOrDefaultAsync(t => t.Id == id);
        if (t is null)
            return null;
        var published = t.Versions.Where(v => v.Status == TemplateStatus.Published).MaxBy(v => v.Version);
        var draft = t.Versions.FirstOrDefault(v => v.Status == TemplateStatus.Draft);
        return new TemplateDetail(
            t.Id, t.Key, t.Name, t.Service, t.IsActive,
            t.Versions.OrderByDescending(v => v.Version).Select(v => new VersionInfo(v.Id, v.Version, v.Status, v.CreatedAt, v.PublishedAt)).ToList(),
            published?.Id, published is null ? null : JsonDocument.Parse(published.DefinitionJson).RootElement.Clone(),
            draft?.Id, draft?.DefinitionJson, draft is null ? [] : TemplateValidator.Validate(draft.DefinitionJson));
    }
}
