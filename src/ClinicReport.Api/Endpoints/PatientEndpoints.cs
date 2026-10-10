using ClinicReport.Api.Data;
using ClinicReport.Api.Reports;
using Microsoft.EntityFrameworkCore;

namespace ClinicReport.Api.Endpoints;

/// <summary>
/// A minimal patient list so the app works on its own. A clinic with a HIS/EMR would fill this from
/// there (HL7 ADT or FHIR Patient) instead of typing patients in.
/// </summary>
public static class PatientEndpoints
{
    public record SavePatient(string Mrn, string FirstName, string LastName, DateOnly DateOfBirth, string Sex);

    private static readonly string[] Sexes = ["M", "F", "X", "U"];

    public static void MapPatientEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/patients").WithTags("Patients");

        api.MapGet("/", async (string? q, ClinicReportDbContext db) =>
        {
            var query = db.Patients.AsNoTracking();
            if (!string.IsNullOrWhiteSpace(q))
            {
                var term = q.Trim();
                query = query.Where(p => p.Mrn == term || p.LastName.StartsWith(term) || p.FirstName.StartsWith(term));
            }
            return await query.OrderBy(p => p.LastName).ThenBy(p => p.FirstName).Take(50)
                .Select(p => new PatientDto(p.Id, p.Mrn, p.FirstName, p.LastName, p.DateOfBirth, p.Sex))
                .ToListAsync();
        });

        api.MapGet("/{id:int}", async (int id, ClinicReportDbContext db) =>
            await db.Patients.AsNoTracking().Where(p => p.Id == id)
                .Select(p => new PatientDto(p.Id, p.Mrn, p.FirstName, p.LastName, p.DateOfBirth, p.Sex))
                .SingleOrDefaultAsync() is { } p ? Results.Ok(p) : Results.NotFound());

        api.MapPost("/", async (SavePatient req, ClinicReportDbContext db, TimeProvider clock) =>
        {
            if (await Validate(req, null, db, clock) is { } problem)
                return problem;
            var p = new Patient { CreatedAt = clock.GetUtcNow().UtcDateTime };
            Apply(p, req);
            db.Patients.Add(p);
            await db.SaveChangesAsync();
            return Results.Created($"/api/patients/{p.Id}", new PatientDto(p.Id, p.Mrn, p.FirstName, p.LastName, p.DateOfBirth, p.Sex));
        });

        api.MapPut("/{id:int}", async (int id, SavePatient req, ClinicReportDbContext db, TimeProvider clock) =>
        {
            var p = await db.Patients.FindAsync(id);
            if (p is null)
                return Results.NotFound();
            if (await Validate(req, id, db, clock) is { } problem)
                return problem;
            Apply(p, req);
            await db.SaveChangesAsync();
            return Results.Ok(new PatientDto(p.Id, p.Mrn, p.FirstName, p.LastName, p.DateOfBirth, p.Sex));
        });
    }

    private static void Apply(Patient p, SavePatient req)
    {
        p.Mrn = req.Mrn.Trim();
        p.FirstName = req.FirstName.Trim();
        p.LastName = req.LastName.Trim();
        p.DateOfBirth = req.DateOfBirth;
        p.Sex = req.Sex;
    }

    private static async Task<IResult?> Validate(SavePatient req, int? id, ClinicReportDbContext db, TimeProvider clock)
    {
        var errors = new Dictionary<string, string[]>();
        var mrn = req.Mrn?.Trim() ?? "";
        if (mrn.Length is 0 or > 32)
            errors["mrn"] = ["Required, at most 32 characters."];
        else if (await db.Patients.AnyAsync(p => p.Mrn == mrn && p.Id != id))
            errors["mrn"] = ["Another patient has this MRN."];
        if (string.IsNullOrWhiteSpace(req.FirstName) || req.FirstName.Trim().Length > 64)
            errors["firstName"] = ["Required, at most 64 characters."];
        if (string.IsNullOrWhiteSpace(req.LastName) || req.LastName.Trim().Length > 64)
            errors["lastName"] = ["Required, at most 64 characters."];
        var today = DateOnly.FromDateTime(clock.GetLocalNow().DateTime);
        if (req.DateOfBirth > today || req.DateOfBirth < today.AddYears(-125))
            errors["dateOfBirth"] = ["Not a plausible date of birth."];
        if (!Sexes.Contains(req.Sex))
            errors["sex"] = ["One of M, F, X, U."];
        return errors.Count > 0 ? Results.ValidationProblem(errors) : null;
    }
}
