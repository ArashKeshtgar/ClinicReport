using System.Security.Cryptography;
using System.Text.Json;
using ClinicReport.Api.Auth;
using ClinicReport.Api.Domain;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace ClinicReport.Api.Data;

public class DemoOptions
{
    /// <summary>Seeds synthetic patients and one-click demo users. Never enable on a server with real patients.</summary>
    public bool Enabled { get; set; }

    /// <summary>When set (0–23), the demo database is wiped and re-seeded every day at this UTC hour.</summary>
    public int? ResetHourUtc { get; set; }

    /// <summary>Visitors are all admins in the demo, so by default nobody can add accounts or change passwords.</summary>
    public bool LockAccounts { get; set; } = true;
}

public class ClinicOptions
{
    public string Name { get; set; } = "ClinicReport";
    public string? Address { get; set; }
    public string? Phone { get; set; }
    public string? ReportFooter { get; set; }
}

/// <summary>Imports every bundled template whose key is not in the database yet, as published version 1.</summary>
public static class TemplateSeeder
{
    public static async Task SeedAsync(ClinicReportDbContext db, string directory, ILogger logger, DateTime now)
    {
        if (!Directory.Exists(directory))
            return;
        foreach (var path in Directory.GetFiles(directory, "*.json").Order())
        {
            var json = await File.ReadAllTextAsync(path);
            var errors = TemplateValidator.Validate(json);
            if (errors.Count > 0)
            {
                logger.LogError("Skipping template {File}: {Errors}", Path.GetFileName(path), string.Join(" | ", errors));
                continue;
            }
            var def = TemplateDefinition.Parse(json);
            if (await db.Templates.AnyAsync(t => t.Key == def.Key))
                continue;

            db.Templates.Add(new ReportTemplate
            {
                Key = def.Key, Name = def.Name, Service = def.Service,
                Versions = [new TemplateVersion
                {
                    Version = 1, Status = TemplateStatus.Published, DefinitionJson = def.ToJson(), CreatedAt = now, PublishedAt = now,
                }],
            });
            logger.LogInformation("Imported template {Key} from {File}", def.Key, Path.GetFileName(path));
        }
        await db.SaveChangesAsync();
    }
}

/// <summary>
/// Makes sure the built-in roles exist. Administrator always gets every permission (so a new permission
/// added in a later version reaches it); the other built-in roles are only created, never overwritten,
/// because a clinic may have changed them.
/// </summary>
public static class RoleSeeder
{
    public static async Task SeedAsync(ClinicReportDbContext db)
    {
        foreach (var (name, description, permissions, requireTwoFactor) in SystemRoles.Defaults)
        {
            var role = await db.Roles.SingleOrDefaultAsync(r => r.Name == name);
            if (role is null)
                db.Roles.Add(new Role
                {
                    Name = name, Description = description, Permissions = Permissions.Format(permissions),
                    RequireTwoFactor = requireTwoFactor, IsSystem = true,
                });
            else if (name == SystemRoles.Administrator)
            {
                role.IsSystem = true;
                role.Permissions = Permissions.Format(Permissions.All);
            }
        }
        await db.SaveChangesAsync();
    }

    public static Task<Role> GetAsync(ClinicReportDbContext db, string name) => db.Roles.SingleAsync(r => r.Name == name);
}

/// <summary>On a brand-new database, creates the first admin so someone can sign in and add the real users.</summary>
public static class UserBootstrap
{
    public static async Task SeedAsync(ClinicReportDbContext db, IConfiguration config, IPasswordHasher<AppUser> hasher, ILogger logger, DateTime now)
    {
        if (await db.Users.AnyAsync())
            return;

        var userName = config["Bootstrap:AdminUserName"] ?? "admin";
        var password = config["Bootstrap:AdminPassword"];
        var generated = string.IsNullOrEmpty(password);
        if (generated)
            password = RandomNumberGenerator.GetString("abcdefghijkmnpqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789", 16);

        var admin = new AppUser
        {
            UserName = userName, DisplayName = "Administrator", CreatedAt = now, MustChangePassword = true,
            Roles = [new UserRole { Role = await RoleSeeder.GetAsync(db, SystemRoles.Administrator) }],
        };
        admin.PasswordHash = hasher.HashPassword(admin, password!);
        db.Users.Add(admin);
        await db.SaveChangesAsync();

        if (generated)
            logger.LogWarning("Created the first admin user \"{User}\" with password {Password} - sign in and change it now.", userName, password);
        else
            logger.LogInformation("Created the first admin user \"{User}\" from Bootstrap:AdminPassword.", userName);
    }
}

/// <summary>Synthetic data for the public demo: no real people, every MRN starts with DEMO-.</summary>
public static class DemoSeeder
{
    public static readonly string[] DemoUserNames = ["demo.typist", "demo.physician", "demo.admin", "demo.echo"];

    public static async Task SeedAsync(ClinicReportDbContext db, IPasswordHasher<AppUser> hasher, DateTime now)
    {
        var roles = await db.Roles.ToDictionaryAsync(r => r.Name);
        AppUser User(string userName, string name, string role, string? credentials = null, string? services = null)
        {
            var u = new AppUser
            {
                UserName = userName, DisplayName = name, Credentials = credentials, CreatedAt = now, Services = services,
                Roles = [new UserRole { Role = roles[role] }],
            };
            // Demo users sign in with the one-click demo login; nobody knows this password.
            u.PasswordHash = hasher.HashPassword(u, RandomNumberGenerator.GetHexString(32));
            return u;
        }

        var wanted = new[]
        {
            User("demo.admin", "Demo Administrator", SystemRoles.Administrator),
            User("demo.physician", "Dr. Avery Morgan", SystemRoles.Physician, "MD, FRCPC"),
            User("demo.typist", "Riley Chen", SystemRoles.Typist),
            // Shows service scoping: this typist only sees echocardiography.
            User("demo.echo", "Sam Rivera", SystemRoles.Typist, services: "Echocardiography"),
        };
        // Add demo users a newer version introduced; leave existing ones alone.
        var existing = await db.Users.Where(u => DemoUserNames.Contains(u.UserName)).Select(u => u.UserName).ToListAsync();
        db.Users.AddRange(wanted.Where(u => !existing.Contains(u.UserName)));
        await db.SaveChangesAsync();

        if (await db.Patients.AnyAsync(p => p.Mrn.StartsWith("DEMO-")))
            return;
        var typist = await db.Users.SingleAsync(u => u.UserName == "demo.typist");

        Patient P(int n, string first, string last, int year, int month, int day, string sex) => new()
        {
            Mrn = $"DEMO-{n:0000}", FirstName = first, LastName = last, DateOfBirth = new DateOnly(year, month, day), Sex = sex, CreatedAt = now,
        };
        var patients = new[]
        {
            P(1, "Alex", "Sample", 1958, 3, 14, "M"),
            P(2, "Jordan", "Example", 1972, 11, 2, "F"),
            P(3, "Casey", "Testcase", 1990, 6, 30, "F"),
            P(4, "Morgan", "Placeholder", 1949, 1, 21, "M"),
            P(5, "Taylor", "Demo", 2001, 9, 9, "X"),
        };
        db.Patients.AddRange(patients);
        await db.SaveChangesAsync();

        var echo = await db.TemplateVersions.Include(v => v.Template)
            .Where(v => v.Template.Key == "echo-tte" && v.Status == TemplateStatus.Published).OrderByDescending(v => v.Version).FirstOrDefaultAsync();
        var ecg = await db.TemplateVersions.Include(v => v.Template)
            .Where(v => v.Template.Key == "ecg-12-lead" && v.Status == TemplateStatus.Published).OrderByDescending(v => v.Version).FirstOrDefaultAsync();
        var today = DateOnly.FromDateTime(now.ToLocalTime());

        if (echo is not null)
        {
            // A draft in progress: the typist has started, the physician still has to finish and sign.
            db.Reports.Add(new Report
            {
                PatientId = patients[0].Id, TemplateVersionId = echo.Id, ExamDate = today, Indication = "Exertional dyspnea. Hypertension.",
                ReferringPhysician = "Dr. Family Practice (demo)", HeightCm = 176, WeightKg = 88,
                ValuesJson = JsonSerializer.Serialize(new Dictionary<string, object>
                {
                    ["quality"] = "good", ["rhythm"] = "sinus", ["heart_rate"] = 72,
                    ["lv_size"] = "normal", ["lv_wall"] = "concentric_lvh", ["lv_function"] = "normal", ["lvef"] = 60, ["rwma"] = "none",
                    ["dd"] = "g1", ["ivsd"] = 13, ["lvidd"] = 48, ["lvpwd"] = 12, ["lvids"] = 30, ["la_volume"] = 70,
                    ["e_wave"] = 62, ["a_wave"] = 85, ["e_sept"] = 6, ["e_lat"] = 8,
                }),
                Revision = 1, CreatedByUserId = typist.Id, CreatedAt = now, UpdatedAt = now,
            });
            db.Reports.Add(new Report
            {
                PatientId = patients[1].Id, TemplateVersionId = echo.Id, ExamDate = today, Indication = "Murmur.",
                HeightCm = 165, WeightKg = 61, Revision = 1, CreatedByUserId = typist.Id, CreatedAt = now, UpdatedAt = now,
            });
        }
        if (ecg is not null)
        {
            db.Reports.Add(new Report
            {
                PatientId = patients[3].Id, TemplateVersionId = ecg.Id, ExamDate = today.AddDays(-1), Indication = "Pre-operative assessment.",
                ValuesJson = JsonSerializer.Serialize(new Dictionary<string, object>
                {
                    ["rhythm"] = "af", ["heart_rate"] = 88, ["qrs"] = 96, ["qt"] = 360, ["axis"] = "normal",
                    ["st_t"] = "nonspecific", ["q_waves"] = "none", ["comparison"] = "none",
                }),
                Conclusion = "Atrial fibrillation with controlled ventricular rate. Nonspecific ST-T changes.",
                Revision = 1, CreatedByUserId = typist.Id, CreatedAt = now, UpdatedAt = now,
            });
        }
        await db.SaveChangesAsync();
    }
}
