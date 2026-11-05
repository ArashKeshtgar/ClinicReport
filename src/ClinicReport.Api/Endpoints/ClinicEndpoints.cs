using System.Security.Claims;
using ClinicReport.Api.Auth;
using ClinicReport.Api.Data;
using ClinicReport.Api.Reports;
using Microsoft.EntityFrameworkCore;

namespace ClinicReport.Api.Endpoints;

/// <summary>
/// Hosted mode: a clinic's own letterhead (kept in the catalog, as standalone keeps it in the "Clinic" settings)
/// and the onboarding checklist its administrators see until the clinic is set up.
/// </summary>
public static class ClinicEndpoints
{
    public record ClinicBody(string? Name, string? Address, string? Phone, string? ReportFooter);
    public record OnboardingStep(string Key, string Title, bool Done);
    public record Onboarding(OnboardingStep[] Steps, bool Complete);

    public static void MapClinicEndpoints(this IEndpointRouteBuilder app)
    {
        if (!app.ServiceProvider.IsHosted())
            return;
        var admin = app.MapGroup("/api/clinic").WithTags("Clinic").RequireAuthorization(Permissions.UsersManage);

        admin.MapPut("/", async (ClinicBody body, ITenantContext current, CatalogDbContext catalog, TenantDirectory directory, CancellationToken ct) =>
        {
            var name = (body.Name ?? "").Trim();
            if (name.Length is < 2 or > 200)
                throw new ReportRuleException("Enter the clinic's name (2 to 200 characters).", StatusCodes.Status400BadRequest);
            if (body.Address?.Length > 500 || body.Phone?.Length > 50 || body.ReportFooter?.Length > 1000)
                throw new ReportRuleException("Address, phone or footer is too long.", StatusCodes.Status400BadRequest);

            var tenant = await catalog.Tenants.Include(t => t.Settings).SingleAsync(t => t.Id == current.Tenant!.Id, ct);
            tenant.Name = name;
            tenant.Settings ??= new TenantSettings();
            tenant.Settings.Address = Blank(body.Address);
            tenant.Settings.Phone = Blank(body.Phone);
            tenant.Settings.ReportFooter = Blank(body.ReportFooter);
            await catalog.SaveChangesAsync(ct);
            // The letterhead comes from the cached tenant; the next request reads the new one.
            directory.Forget(tenant.Slug);
            return Results.NoContent();
        });

        // A GET, so it also works for a suspended or cancelled clinic, and logged in its own audit trail.
        admin.MapGet("/export", async (HttpContext http, ClaimsPrincipal me, ClinicReportDbContext db, SecurityLog log, TimeProvider clock, CancellationToken ct) =>
        {
            var now = clock.GetUtcNow().UtcDateTime;
            log.Add(SecurityEventTypes.DataExported, me.RequiredUserId());
            await db.SaveChangesAsync(ct);
            // On disk, as it can be large; gone when the response has been sent.
            var file = new FileStream(Path.GetTempFileName(), FileMode.Create, FileAccess.ReadWrite, FileShare.None, 81920, FileOptions.DeleteOnClose);
            await TenantExport.WriteAsync(db, http.CurrentClinic(), file, now, ct);
            file.Position = 0;
            return Results.File(file, "application/zip", $"clinicreport-export-{now:yyyy-MM-dd}.zip");
        });

        admin.MapGet("/onboarding", async (ITenantContext current, ClinicReportDbContext db, CancellationToken ct) =>
        {
            var tenant = current.Tenant!;
            var settings = tenant.Settings;
            OnboardingStep[] steps =
            [
                new("email", "Confirm the clinic's email address", !tenant.AwaitingEmailVerification),
                new("letterhead", "Add the clinic's address and report footer",
                    !string.IsNullOrWhiteSpace(settings?.Address) || !string.IsNullOrWhiteSpace(settings?.ReportFooter)),
                // The bundled templates are published by nobody; the clinic's own have a publisher.
                new("template", "Publish a template of your own", await db.TemplateVersions.AnyAsync(v => v.PublishedByUserId != null, ct)),
                new("invite", "Add a colleague", await db.Users.CountAsync(u => u.IsActive, ct) > 1),
                new("report", "Start the first report", await db.Reports.AnyAsync(ct)),
            ];
            return new Onboarding(steps, steps.All(s => s.Done));
        });
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
