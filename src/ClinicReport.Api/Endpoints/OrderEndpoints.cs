using System.Security.Claims;
using System.Text.Json.Nodes;
using ClinicReport.Api.Auth;
using ClinicReport.Api.Data;
using ClinicReport.Api.Reports;
using Microsoft.EntityFrameworkCore;

namespace ClinicReport.Api.Endpoints;

/// <summary>
/// Orders from ClinicHub (connected mode). ClinicHub posts a FHIR R4 transaction Bundle (the patient, the
/// requesting physician and one ServiceRequest per exam) to /fhir; each exam waits on the worklist until someone
/// starts its report. The clinic withdraws an exam by sending its ServiceRequest again with status "revoked";
/// once the report has been started, that is refused and the clinic is told to speak to the reader.
/// </summary>
public static class OrderEndpoints
{
    public const string TemplateSystem = "urn:clinicreport:template";

    public record OrderRow(int Id, string Patient, string Mrn, string Template, string Service, string? Indication, string? ReferringPhysician,
        string Priority, DateTime ReceivedAt, string Status, int? ReportId);

    public static void MapOrderEndpoints(this IEndpointRouteBuilder app)
    {
        if (!app.ServiceProvider.IsConnected())
            return;

        app.MapPost("/fhir", async (HttpRequest request, ClaimsPrincipal hub, ClinicReportDbContext db, TimeProvider clock) =>
        {
            JsonNode? bundle;
            try { bundle = await JsonNode.ParseAsync(request.Body); }
            catch (System.Text.Json.JsonException) { return Outcome("The body is not JSON."); }
            if ((string?)bundle?["resourceType"] != "Bundle" || (string?)bundle["type"] != "transaction")
                return Outcome("Expected a FHIR transaction Bundle.");
            var entries = bundle["entry"]?.AsArray().Select(e => e?["resource"]).ToList() ?? [];
            var revoked = entries.Where(r => (string?)r?["resourceType"] == "ServiceRequest" && (string?)r?["status"] == "revoked").ToList();
            if (revoked.Count > 0)
                return revoked.Count == entries.Count
                    ? await CancelAsync(db, hub.FindFirstValue("clinic_slug")!, revoked!, clock.GetUtcNow().UtcDateTime)
                    : Outcome("A Bundle that withdraws orders carries only the revoked ServiceRequests.");
            var patient = entries.FirstOrDefault(r => (string?)r?["resourceType"] == "Patient");
            var practitioner = entries.FirstOrDefault(r => (string?)r?["resourceType"] == "Practitioner");
            var orders = entries.Where(r => (string?)r?["resourceType"] == "ServiceRequest").ToList();
            if (patient is null || orders.Count == 0)
                return Outcome("The Bundle needs a Patient and at least one ServiceRequest.");

            var mrn = patient["identifier"]?.AsArray().Select(i => i!).FirstOrDefault(i => ((string?)i["system"])?.EndsWith(":mrn") == true)?["value"]?.ToString();
            var family = (string?)patient["name"]?[0]?["family"];
            var given = (string?)patient["name"]?[0]?["given"]?[0];
            if (string.IsNullOrWhiteSpace(mrn) || string.IsNullOrWhiteSpace(family) || !DateOnly.TryParse((string?)patient["birthDate"], out var dob))
                return Outcome("The Patient needs an MRN identifier, a family name and a birth date.");

            var keys = orders.Select(o => o?["code"]?["coding"]?.AsArray().FirstOrDefault(c => (string?)c?["system"] == TemplateSystem)?["code"]?.ToString()).ToList();
            var templates = await db.Templates.Where(t => t.IsActive).ToDictionaryAsync(t => t.Key);
            var unknown = keys.Where(k => k is null || !templates.ContainsKey(k)).ToList();
            if (unknown.Count > 0)
                return Outcome($"ClinicReport has no active template '{unknown[0] ?? "(no code)"}'. Codes use {TemplateSystem}.");

            var slug = hub.FindFirstValue("clinic_slug")!;
            var now = clock.GetUtcNow().UtcDateTime;
            var local = await db.Patients.SingleOrDefaultAsync(p => p.Mrn == mrn)
                ?? db.Patients.Add(new Patient { Mrn = mrn, CreatedAt = now }).Entity;
            // ClinicHub's register is the source of truth for the demographics of its patients.
            local.LastName = family;
            local.FirstName = given ?? "";
            local.DateOfBirth = dob;
            local.Sex = (string?)patient["gender"] switch { "male" => "M", "female" => "F", "other" => "X", _ => "U" };

            var referring = practitioner is null ? null : string.Join(" ",
                new[] { (string?)practitioner["name"]?[0]?["prefix"]?[0], (string?)practitioner["name"]?[0]?["given"]?[0], (string?)practitioner["name"]?[0]?["family"] }
                    .Where(x => !string.IsNullOrWhiteSpace(x)))
                + (practitioner["identifier"]?.AsArray().FirstOrDefault(i => ((string?)i?["system"])?.Contains("billing") == true)?["value"] is { } billing ? $" (OHIP {billing})" : "");
            var requisition = (string?)bundle["identifier"]?["value"];

            var saved = new List<ReportOrder>();
            for (var i = 0; i < orders.Count; i++)
            {
                var o = orders[i]!;
                var externalId = $"{slug}:{(string?)o["identifier"]?[0]?["value"]}";
                var existing = await db.ReportOrders.SingleOrDefaultAsync(x => x.ExternalId == externalId);
                if (existing is not null)
                {
                    saved.Add(existing);   // the same order sent again (a retry): accepted once
                    continue;
                }
                var order = new ReportOrder
                {
                    ExternalId = externalId, Requisition = requisition, Patient = local, Template = templates[keys[i]!],
                    Indication = string.Join("; ", o["note"]?.AsArray().Select(n => (string?)n?["text"]).Where(t => !string.IsNullOrWhiteSpace(t)) ?? []) is { Length: > 0 } note ? note : null,
                    ReferringPhysician = string.IsNullOrWhiteSpace(referring) ? null : referring,
                    Priority = (string?)o["priority"] == "stat" ? "Stat" : "Routine",
                    ReceivedAt = now, OrderedAt = DateTime.TryParse((string?)o["authoredOn"], out var at) ? at.ToUniversalTime() : null,
                };
                db.ReportOrders.Add(order);
                saved.Add(order);
            }
            await db.SaveChangesAsync();

            // transaction-response: one entry per request entry, in the same order.
            var response = new JsonArray();
            foreach (var r in entries)
                response.Add(new JsonObject
                {
                    ["response"] = (string?)r?["resourceType"] switch
                    {
                        "Patient" => Created($"Patient/{local.Id}"),
                        "ServiceRequest" => Created($"ServiceRequest/{saved[orders.IndexOf(r)].Id}"),
                        _ => new JsonObject { ["status"] = "200 OK" },
                    },
                });
            return Results.Text(new JsonObject { ["resourceType"] = "Bundle", ["type"] = "transaction-response", ["entry"] = response }.ToJsonString(),
                "application/fhir+json");
        }).RequireAuthorization(ClinicHubConnection.OrdersPolicy).ExcludeFromDescription();

        var api = app.MapGroup("/api/orders").WithTags("Orders");

        // Exams waiting to be done (and the ones started today), for the worklist.
        api.MapGet("/", async (ClaimsPrincipal user, ClinicReportDbContext db, TimeProvider clock) =>
        {
            var since = clock.GetUtcNow().UtcDateTime.Date;
            var services = user.ServiceScope();
            return await db.ReportOrders.AsNoTracking()
                .Where(o => o.Status == ReportOrderStatus.Waiting || (o.Status == ReportOrderStatus.Started && o.ReceivedAt >= since))
                .Where(o => services == null || services.Contains(o.Template.Service))
                .OrderByDescending(o => o.Priority == "Stat").ThenBy(o => o.ReceivedAt)
                .Select(o => new OrderRow(o.Id, o.Patient.LastName + ", " + o.Patient.FirstName, o.Patient.Mrn, o.Template.Name, o.Template.Service,
                    o.Indication, o.ReferringPhysician, o.Priority, o.ReceivedAt, o.Status, o.ReportId))
                .ToListAsync();
        }).RequireAuthorization(Permissions.ReportsView);

        // Starting an ordered exam: a draft report on the template, with the order's indication and referring physician.
        api.MapPost("/{id:int}/start", async (int id, ClaimsPrincipal user, ClinicReportDbContext db, ReportService reports, TimeProvider clock, CancellationToken ct) =>
        {
            var order = await db.ReportOrders.Include(o => o.Template).SingleOrDefaultAsync(o => o.Id == id, ct);
            if (order is null || !user.CanAccessService(order.Template.Service))
                return Results.NotFound();
            if (order.Status != ReportOrderStatus.Waiting)
                return Results.Problem("This order has already been started.", statusCode: StatusCodes.Status409Conflict);
            var version = await db.TemplateVersions.Where(v => v.TemplateId == order.TemplateId && v.Status == TemplateStatus.Published)
                .OrderByDescending(v => v.Version).FirstOrDefaultAsync(ct);
            if (version is null)
                return Results.Problem("The template has no published version.", statusCode: StatusCodes.Status409Conflict);

            var now = clock.GetUtcNow().UtcDateTime;
            var today = DateOnly.FromDateTime(now);
            var report = new Report
            {
                PatientId = order.PatientId, TemplateVersionId = version.Id, ExamDate = today,
                CreatedByUserId = user.RequiredUserId(), CreatedAt = now, UpdatedAt = now,
            };
            db.Reports.Add(report);
            await db.SaveChangesAsync(ct);
            order.Status = ReportOrderStatus.Started;
            order.ReportId = report.Id;
            await db.SaveChangesAsync(ct);
            await reports.SetHeaderAsync(report.Id, new HeaderDto(today, order.Indication, order.ReferringPhysician, null, null), user.RequiredUserId(), null, ct);
            return Results.Ok(new { reportId = report.Id });
        }).RequireAuthorization(Permissions.ReportsCreate);
    }

    /// <summary>Withdrawn exams: cancelled if still waiting, refused (409) once the report is started. All or nothing.</summary>
    private static async Task<IResult> CancelAsync(ClinicReportDbContext db, string slug, List<JsonNode> revoked, DateTime now)
    {
        var system = $"urn:clinichub:{slug}:service-request";
        var orders = new List<(ReportOrder Order, string? Reason)>();
        foreach (var sr in revoked)
        {
            var id = (string?)sr["identifier"]?.AsArray().FirstOrDefault(i => (string?)i?["system"] == system)?["value"];
            var order = id is null ? null : await db.ReportOrders.Include(o => o.Template).SingleOrDefaultAsync(o => o.ExternalId == $"{slug}:{id}");
            if (order is null)
                return Outcome($"No order {id ?? "(no identifier)"} from this clinic.", StatusCodes.Status404NotFound, "not-found");
            if (order.Status == ReportOrderStatus.Started)
                return Outcome($"The {order.Template.Name} has been started (report {order.ReportId}); speak to the reading physician.",
                    StatusCodes.Status409Conflict, "business-rule");
            if (order.Status == ReportOrderStatus.Waiting)
                orders.Add((order, ((string?)sr["note"]?[0]?["text"])?.Trim() is { Length: > 0 } why ? why[..Math.Min(why.Length, 200)] : null));
        }
        foreach (var (order, reason) in orders)
        {
            order.Status = ReportOrderStatus.Cancelled;
            order.CancelledAt = now;
            order.CancelReason = reason ?? "Cancelled by the ordering clinic";
        }
        await db.SaveChangesAsync();

        var response = new JsonArray();
        foreach (var _ in revoked)
            response.Add(new JsonObject { ["response"] = new JsonObject { ["status"] = "200 OK" } });
        return Results.Text(new JsonObject { ["resourceType"] = "Bundle", ["type"] = "transaction-response", ["entry"] = response }.ToJsonString(),
            "application/fhir+json");
    }

    private static JsonObject Created(string location) => new() { ["status"] = "201 Created", ["location"] = location };

    private static IResult Outcome(string diagnostics, int status = StatusCodes.Status400BadRequest, string code = "invalid") => Results.Text(new JsonObject
    {
        ["resourceType"] = "OperationOutcome",
        ["issue"] = new JsonArray(new JsonObject { ["severity"] = "error", ["code"] = code, ["diagnostics"] = diagnostics }),
    }.ToJsonString(), "application/fhir+json", statusCode: status);
}
