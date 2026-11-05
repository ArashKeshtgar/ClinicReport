using ClinicReport.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace ClinicReport.Api.Auth;

/// <summary>Hosted mode: a clinic whose payment stayed failed past the grace period becomes read-only.</summary>
public sealed class Dunning(CatalogDbContext catalog, TenantDirectory directory, TimeProvider clock, ILogger<Dunning> logger)
{
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
        }
        return suspended;
    }
}

/// <summary>Runs <see cref="Dunning"/> at start-up and every hour, so a restart doesn't put a suspension off.</summary>
public sealed class DunningService(IServiceScopeFactory scopes, TimeProvider clock, ILogger<DunningService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval, clock);
        do
        {
            try
            {
                using var scope = scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<Dunning>().SuspendOverdueAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A failure here must not stop the service; the next hour tries again.
                logger.LogError(ex, "Dunning pass failed");
            }
        }
        while (await WaitAsync(timer, stoppingToken));
    }

    private static async Task<bool> WaitAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try
        {
            return await timer.WaitForNextTickAsync(ct);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
