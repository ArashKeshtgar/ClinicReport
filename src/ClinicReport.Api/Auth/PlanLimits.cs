using ClinicReport.Api.Data;
using ClinicReport.Api.Reports;
using Microsoft.EntityFrameworkCore;

namespace ClinicReport.Api.Auth;

public record PlanUsage(int ActiveUsers, int Services, int SignedThisMonth);

/// <summary>
/// Hosted mode: what the clinic's plan lets it do next. The one place plan limits are decided; a full limit is a 402.
/// Limits stop growth only: after a downgrade, everyone and everything the clinic already has keeps working.
/// Outside Hosted mode there is no tenant and nothing is limited.
/// </summary>
public sealed class PlanLimits(ClinicReportDbContext db, TimeProvider clock, ITenantContext? current = null, SeatChanges? seatChanges = null, UsageMeter? usage = null)
{
    public const int FreeUsers = 3;
    public const int FreeServices = 2;
    public const int FreeSignedReportsPerMonth = 50;

    private bool Free => current?.Tenant?.EffectivePlan(clock.GetUtcNow().UtcDateTime) == Plans.Free;

    /// <summary>The active users of a clinic: its limit on Free, and on Pro the seats it pays for.</summary>
    public static Task<int> CountSeatsAsync(ClinicReportDbContext db, CancellationToken ct = default) => db.Users.CountAsync(u => u.IsActive, ct);

    private Task<int> ActiveUsersAsync(CancellationToken ct) => CountSeatsAsync(db, ct);

    /// <summary>After the clinic's active users may have changed; a subscribed clinic's seats then follow.</summary>
    public void NoteSeatChange()
    {
        if (current?.Tenant is { StripeSubscriptionId: not null } tenant)
            seatChanges?.Note(tenant.Slug);
    }

    /// <summary>After a report was signed: the usage, counted where the limit is.</summary>
    public Task NoteReportSignedAsync(CancellationToken ct = default) => usage?.RecordAsync(UsageKinds.ReportSigned, ct: ct) ?? Task.CompletedTask;

    private Task<int> SignedThisMonthAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var monthStart = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        return db.Reports.CountAsync(r => r.SignedAt >= monthStart, ct);
    }

    /// <summary>The services of the templates in use, apart from one template (none: every template).</summary>
    private async Task<List<string>> ServicesInUseAsync(int? exceptTemplateId, CancellationToken ct) => (await db.Templates
        .Where(t => t.Id != exceptTemplateId && t.IsActive && t.Versions.Any(v => v.Status == TemplateStatus.Published))
        .Select(t => t.Service).ToListAsync(ct)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>What the clinic uses now, counted the way the limits count it.</summary>
    public async Task<PlanUsage> UsageAsync(CancellationToken ct = default) =>
        new(await ActiveUsersAsync(ct), (await ServicesInUseAsync(null, ct)).Count, await SignedThisMonthAsync(ct));

    /// <summary>Before adding a user, or bringing back a deactivated one: both add to the active users.</summary>
    public async Task EnsureCanAddUser(CancellationToken ct = default)
    {
        if (Free && await ActiveUsersAsync(ct) >= FreeUsers)
            throw new ReportRuleException($"The Free plan has {FreeUsers} active users. Upgrade, or deactivate someone first.", StatusCodes.Status402PaymentRequired);
    }

    /// <summary>Before signing. Only reports signed this calendar month (UTC) count; addenda are not new reports.</summary>
    public async Task EnsureCanSignReport(CancellationToken ct = default)
    {
        if (Free && await SignedThisMonthAsync(ct) >= FreeSignedReportsPerMonth)
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
        var others = await ServicesInUseAsync(templateId, ct);
        if (others.Count >= FreeServices && !others.Contains(service, StringComparer.OrdinalIgnoreCase))
            throw new ReportRuleException($"The Free plan covers {FreeServices} services. Upgrade to report on another.", StatusCodes.Status402PaymentRequired);
    }
}
