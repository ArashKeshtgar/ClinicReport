using ClinicReport.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace ClinicReport.Api.Auth;

/// <summary>Hosted mode: a clinic whose payment stayed failed past the grace period becomes read-only.</summary>
public sealed class Dunning(CatalogDbContext catalog, TenantDirectory directory, TimeProvider clock, IMailSender mail, ILogger<Dunning> logger)
{
    public async Task RunAsync(CancellationToken ct = default)
    {
        await WarnPastDueAsync(ct);
        await SuspendOverdueAsync(ct);
    }

    /// <summary>
    /// Tells each past-due clinic's owner once: someone who doesn't sign in that week would never see the banner.
    /// A mail that fails is tried again by the next pass. Returns how many went out.
    /// </summary>
    public async Task<int> WarnPastDueAsync(CancellationToken ct = default)
    {
        var due = await catalog.Tenants.Where(t => t.Status == TenantStatus.PastDue && t.PastDueNoticeAt == null && t.OwnerEmail != null).ToListAsync(ct);
        var sent = 0;
        foreach (var tenant in due)
        {
            if (!await TellOwnerAsync(tenant, $"Payment failed for {tenant.Name}",
                $"""
                The last payment for {tenant.Name} failed.

                Update the card on the clinic's Billing page by {tenant.DeadlineAt:yyyy-MM-dd} (UTC). After that the clinic
                becomes read-only: reports can still be read and exported, but not written or changed.
                """, ct))
                continue;
            tenant.PastDueNoticeAt = clock.GetUtcNow().UtcDateTime;
            await catalog.SaveChangesAsync(ct);
            sent++;
        }
        return sent;
    }

    private async Task<bool> TellOwnerAsync(Tenant tenant, string subject, string body, CancellationToken ct)
    {
        try
        {
            await mail.SendAsync(new OutgoingMail(tenant.OwnerEmail!, subject, body), ct);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning("Mail to the owner of {Tenant} not sent: {Error}", tenant.Slug, ex.GetType().Name);
            return false;
        }
    }

    /// <summary>Returns how many clinics it suspended.</summary>
    public async Task<int> SuspendOverdueAsync(CancellationToken ct = default)
    {
        var deadline = clock.GetUtcNow().UtcDateTime.AddDays(-Tenant.GraceDays);
        var overdue = catalog.Tenants.Where(t => t.Status == TenantStatus.PastDue && t.PastDueSince <= deadline);
        var slugs = await overdue.Select(t => t.Slug).ToListAsync(ct);
        var suspended = 0;
        foreach (var slug in slugs)
        {
            // The condition again, in the update itself: the webhook may just have recorded a payment.
            if (await overdue.Where(t => t.Slug == slug).ExecuteUpdateAsync(s => s.SetProperty(t => t.Status, TenantStatus.Suspended), ct) == 0)
                continue;
            directory.Forget(slug);
            suspended++;
            logger.LogInformation("Suspended {Tenant}: payment overdue", slug);
            if (await catalog.Tenants.AsNoTracking().SingleAsync(t => t.Slug == slug, ct) is { OwnerEmail: not null } tenant)
                await TellOwnerAsync(tenant, $"{tenant.Name} is now read-only",
                    $"The payment for {tenant.Name} stayed overdue, so the clinic is read-only. Update the card on its Billing page to restore it at once.", ct);
        }
        return suspended;
    }
}

/// <summary>Runs <see cref="Dunning"/> at start-up and every hour, so a restart doesn't put a suspension off.</summary>
public sealed class DunningService(IServiceScopeFactory scopes, TimeProvider clock, ILogger<DunningService> logger)
    : PeriodicJobService(scopes, clock, logger, TimeSpan.FromHours(1))
{
    protected override Task RunAsync(IServiceProvider services, CancellationToken ct) => services.GetRequiredService<Dunning>().RunAsync(ct);
}
