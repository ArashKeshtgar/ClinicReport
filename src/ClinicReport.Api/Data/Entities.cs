namespace ClinicReport.Api.Data;

public static class Roles
{
    public const string Admin = "Admin";         // templates and users
    public const string Physician = "Physician"; // edits and signs reports
    public const string Typist = "Typist";       // creates and fills reports

    public static readonly string[] All = [Admin, Physician, Typist];
}

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

    public string Role { get; set; } = Roles.Typist;
    public string PasswordHash { get; set; } = "";
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; }
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
