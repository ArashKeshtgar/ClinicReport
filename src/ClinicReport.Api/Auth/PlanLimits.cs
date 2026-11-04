using ClinicReport.Api.Data;
using ClinicReport.Api.Reports;
using Microsoft.EntityFrameworkCore;

namespace ClinicReport.Api.Auth;

/// <summary>
/// Hosted mode: what the clinic's plan lets it do next. The one place plan limits are decided; a full limit is a 402.
/// Limits stop growth only: after a downgrade, everyone and everything the clinic already has keeps working.
/// Outside Hosted mode there is no tenant and nothing is limited.
/// </summary>
public sealed class PlanLimits(ClinicReportDbContext db, TimeProvider clock, ITenantContext? current = null)
{
    public const int FreeUsers = 3;
    public const int FreeServices = 2;
    public const int FreeSignedReportsPerMonth = 50;

    private bool Free => current?.Tenant?.EffectivePlan(clock.GetUtcNow().UtcDateTime) == Plans.Free;

    /// <summary>Before adding a user, or bringing back a deactivated one: both add to the active users.</summary>
    public async Task EnsureCanAddUser(CancellationToken ct = default)
    {
        if (Free && await db.Users.CountAsync(u => u.IsActive, ct) >= FreeUsers)
            throw new ReportRuleException($"The Free plan has {FreeUsers} active users. Upgrade, or deactivate someone first.", StatusCodes.Status402PaymentRequired);
    }

    /// <summary>Before signing. Only reports signed this calendar month (UTC) count; addenda are not new reports.</summary>
    public async Task EnsureCanSignReport(CancellationToken ct = default)
    {
        if (!Free)
            return;
        var now = clock.GetUtcNow().UtcDateTime;
        var monthStart = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        if (await db.Reports.CountAsync(r => r.SignedAt >= monthStart, ct) >= FreeSignedReportsPerMonth)
            throw new ReportRuleException($"The Free plan signs {FreeSignedReportsPerMonth} reports a month. Upgrade to sign this one now.", StatusCodes.Status402PaymentRequired);
    }

    /// <summary>
    /// Before a template goes live, by publishing or activating. A service the clinic already reports on is always
    /// allowed, so a clinic over the limit can keep its templates up to date.
    /// </summary>
    public async Task EnsureCanPublishTemplate(int templateId, string service, CancellationToken ct = default)
    {
        if (!Free)
            return;
        var others = await db.Templates
            .Where(t => t.Id != templateId && t.IsActive && t.Versions.Any(v => v.Status == TemplateStatus.Published))
            .Select(t => t.Service).Distinct().ToListAsync(ct);
        if (others.Count >= FreeServices && !others.Contains(service, StringComparer.OrdinalIgnoreCase))
            throw new ReportRuleException($"The Free plan covers {FreeServices} services. Upgrade to report on another.", StatusCodes.Status402PaymentRequired);
    }
}
