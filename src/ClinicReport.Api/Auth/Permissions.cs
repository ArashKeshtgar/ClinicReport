namespace ClinicReport.Api.Auth;

/// <summary>
/// Everything a role can allow. Roles are made of these; endpoints check these, never role names, so a
/// clinic can shape its own roles ("Echo typist", "Reading physician", "Front desk") without code changes.
/// </summary>
public static class Permissions
{
    public const string ReportsView = "reports.view";
    public const string ReportsCreate = "reports.create";
    public const string ReportsEdit = "reports.edit";
    public const string ReportsSign = "reports.sign";
    public const string PatientsManage = "patients.manage";
    public const string TemplatesDesign = "templates.design";
    public const string UsersManage = "users.manage";
    public const string AuditView = "audit.view";

    public record Info(string Key, string Group, string Label, string Description);

    public static readonly Info[] Catalog =
    [
        new(ReportsView, "Reports", "View reports", "Open the worklist and read reports, including history."),
        new(ReportsCreate, "Reports", "Start reports", "Start a new report for a patient."),
        new(ReportsEdit, "Reports", "Fill reports", "Answer fields, edit the exam header and conclusion of unsigned reports."),
        new(ReportsSign, "Reports", "Sign reports", "Sign reports and add addenda to signed ones. Printed as the signing physician."),
        new(PatientsManage, "Patients", "Manage patients", "Register patients and correct their details."),
        new(TemplatesDesign, "Templates", "Design templates", "Create, edit, publish, activate and deactivate templates."),
        new(UsersManage, "Administration", "Manage users and roles", "Add users, assign roles and services, reset passwords and two-factor, unlock accounts."),
        new(AuditView, "Administration", "View audit log", "See sign-ins, account changes and who opened which report."),
    ];

    public static readonly string[] All = Catalog.Select(p => p.Key).ToArray();

    public static string[] Parse(string? stored) =>
        (stored ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(All.Contains).Distinct().ToArray();

    public static string Format(IEnumerable<string> permissions) =>
        string.Join(' ', All.Where(permissions.Contains));
}

/// <summary>The roles a new installation starts with. Administrator always has every permission.</summary>
public static class SystemRoles
{
    public const string Administrator = "Administrator";
    public const string Physician = "Physician";
    public const string Typist = "Typist";

    public static readonly (string Name, string Description, string[] Permissions, bool RequireTwoFactor)[] Defaults =
    [
        (Administrator, "Full access, including users, roles and the audit log.", Permissions.All, false),
        (Physician, "Reads, edits and signs reports.",
            [Permissions.ReportsView, Permissions.ReportsCreate, Permissions.ReportsEdit, Permissions.ReportsSign, Permissions.PatientsManage], false),
        (Typist, "Starts and fills reports during the exam.",
            [Permissions.ReportsView, Permissions.ReportsCreate, Permissions.ReportsEdit, Permissions.PatientsManage], false),
    ];
}
