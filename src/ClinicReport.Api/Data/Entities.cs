namespace ClinicReport.Api.Data;

public static class ReportStatus
{
    public const string Draft = "Draft";
    public const string Final = "Final";
}

public static class TemplateStatus
{
    public const string Draft = "Draft";
    public const string Published = "Published";
}

public class AppUser
{
    public int Id { get; set; }
    public string UserName { get; set; } = "";
    public string DisplayName { get; set; } = "";

    /// <summary>Printed under the signature, e.g. "MD, FRCPC".</summary>
    public string? Credentials { get; set; }

    /// <summary>
    /// "clinichub:{account id}" for a person who signs in through ClinicHub (connected mode); null for a local
    /// account. Such a user has no password here: ClinicHub holds it, and their roles.
    /// </summary>
    public string? ExternalId { get; set; }

    public string PasswordHash { get; set; } = "";
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; }
    public DateTime? LastLoginAt { get; set; }

    public List<UserRole> Roles { get; set; } = [];

    /// <summary>
    /// Services (template "service" names, e.g. "Echocardiography") this user may work on, one per line.
    /// Empty means every service.
    /// </summary>
    public string? Services { get; set; }

    /// <summary>Changes whenever access should end for existing sessions (password, roles, deactivation, "sign out everywhere").</summary>
    public string SecurityStamp { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>Set by an admin-created or admin-reset password: the user must choose their own before doing anything else.</summary>
    public bool MustChangePassword { get; set; }

    public int FailedLoginCount { get; set; }
    public DateTime? LockoutEndUtc { get; set; }

    /// <summary>TOTP secret, encrypted with ASP.NET Data Protection. Null until set up.</summary>
    public string? TwoFactorSecret { get; set; }
    public bool TwoFactorEnabled { get; set; }

    /// <summary>Last accepted TOTP time step, so a code cannot be replayed.</summary>
    public long TwoFactorLastStep { get; set; }

    public IReadOnlyList<string> ServiceList() =>
        (Services ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}

/// <summary>A named set of permissions, e.g. "Echo typist". Users can hold several.</summary>
public class Role
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string? Description { get; set; }

    /// <summary>Space-separated permission keys (see Auth.Permissions).</summary>
    public string Permissions { get; set; } = "";

    /// <summary>Members must sign in with an authenticator code.</summary>
    public bool RequireTwoFactor { get; set; }

    /// <summary>Created by the installation; cannot be deleted. Administrator also keeps every permission.</summary>
    public bool IsSystem { get; set; }

    public List<UserRole> Users { get; set; } = [];
}

public class UserRole
{
    public int UserId { get; set; }
    public AppUser User { get; set; } = null!;
    public int RoleId { get; set; }
    public Role Role { get; set; } = null!;
}

public static class SecurityEventTypes
{
    public const string LoginSucceeded = "LoginSucceeded";
    public const string LoginFailed = "LoginFailed";
    public const string LockedOut = "LockedOut";
    public const string Logout = "Logout";
    public const string PasswordChanged = "PasswordChanged";
    public const string PasswordReset = "PasswordReset";
    public const string TwoFactorEnabled = "TwoFactorEnabled";
    public const string TwoFactorDisabled = "TwoFactorDisabled";
    public const string TwoFactorReset = "TwoFactorReset";
    public const string SessionsRevoked = "SessionsRevoked";
    public const string UserCreated = "UserCreated";
    public const string UserUpdated = "UserUpdated";
    public const string UserUnlocked = "UserUnlocked";
    public const string RoleCreated = "RoleCreated";
    public const string RoleUpdated = "RoleUpdated";
    public const string RoleDeleted = "RoleDeleted";
    public const string ReportViewed = "ReportViewed";
    public const string ReportSigned = "ReportSigned";
}

/// <summary>Security and privacy audit trail: sign-ins, account changes, and who opened which patient's report.</summary>
public class SecurityEvent
{
    public long Id { get; set; }
    public DateTime At { get; set; }
    public string Type { get; set; } = "";

    /// <summary>Who did it (null for a failed sign-in with an unknown user name).</summary>
    public int? ActorUserId { get; set; }

    /// <summary>The account it was done to, when that is someone else (e.g. an admin resetting a password).</summary>
    public int? TargetUserId { get; set; }

    public int? ReportId { get; set; }
    public int? PatientId { get; set; }
    public string? Detail { get; set; }
    public string? IpAddress { get; set; }
}

public class Patient
{
    public int Id { get; set; }
    public string Mrn { get; set; } = "";
    public string FirstName { get; set; } = "";
    public string LastName { get; set; } = "";
    public DateOnly DateOfBirth { get; set; }

    /// <summary>M, F, X or U. Picks sex-specific normal ranges; X and U use the general range only.</summary>
    public string Sex { get; set; } = "U";

    public DateTime CreatedAt { get; set; }
}

/// <summary>A service's report form, e.g. "Transthoracic Echocardiogram". The content lives in its versions.</summary>
public class ReportTemplate
{
    public int Id { get; set; }
    public string Key { get; set; } = "";
    public string Name { get; set; } = "";
    public string Service { get; set; } = "";
    public bool IsActive { get; set; } = true;
    public List<TemplateVersion> Versions { get; set; } = [];
}

/// <summary>
/// Published versions never change, so a report always renders with the exact form it was typed on.
/// At most one Draft version per template is being edited at a time.
/// </summary>
public class TemplateVersion
{
    public int Id { get; set; }
    public int TemplateId { get; set; }
    public ReportTemplate Template { get; set; } = null!;
    public int Version { get; set; }
    public string Status { get; set; } = TemplateStatus.Draft;
    public string DefinitionJson { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public DateTime? PublishedAt { get; set; }
    public int? PublishedByUserId { get; set; }
}

public class Report
{
    public int Id { get; set; }
    public int PatientId { get; set; }
    public Patient Patient { get; set; } = null!;
    public int TemplateVersionId { get; set; }
    public TemplateVersion TemplateVersion { get; set; } = null!;
    public string Status { get; set; } = ReportStatus.Draft;

    public DateOnly ExamDate { get; set; }
    public string? Indication { get; set; }
    public string? ReferringPhysician { get; set; }
    public double? HeightCm { get; set; }
    public double? WeightKg { get; set; }

    public string ValuesJson { get; set; } = "{}";
    public string? Conclusion { get; set; }

    /// <summary>Bumped on every change; also the optimistic concurrency token.</summary>
    public int Revision { get; set; }

    public int CreatedByUserId { get; set; }
    public AppUser CreatedBy { get; set; } = null!;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public int? SignedByUserId { get; set; }
    public AppUser? SignedBy { get; set; }
    public DateTime? SignedAt { get; set; }

    /// <summary>The rendered report frozen at signing, so the signed text never changes if code changes.</summary>
    public string? SignedSnapshotJson { get; set; }

    public List<ReportAddendum> Addenda { get; set; } = [];
}

public class ReportAddendum
{
    public int Id { get; set; }
    public int ReportId { get; set; }
    public string Text { get; set; } = "";
    public int UserId { get; set; }
    public AppUser User { get; set; } = null!;
    public DateTime CreatedAt { get; set; }
}

/// <summary>Who changed what and when, one row per field change. Never updated or deleted.</summary>
public class ReportChange
{
    public long Id { get; set; }
    public int ReportId { get; set; }

    /// <summary>A template field id, or one of the header names prefixed with "$" (e.g. "$conclusion", "$status").</summary>
    public string Field { get; set; } = "";

    public string? NewValueJson { get; set; }
    public int UserId { get; set; }
    public DateTime At { get; set; }
}
