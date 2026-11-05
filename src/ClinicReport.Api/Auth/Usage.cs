using ClinicReport.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace ClinicReport.Api.Auth;

/// <summary>
/// A job that runs at start-up and then every <paramref name="interval"/>, in a scope of its own. A failed pass is
/// logged and doesn't stop the service; the next one tries again.
/// </summary>
public abstract class PeriodicJobService(IServiceScopeFactory scopes, TimeProvider clock, ILogger logger, TimeSpan interval) : BackgroundService
{
    protected abstract Task RunAsync(IServiceProvider services, CancellationToken ct);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(interval, clock);
        do
        {
            try
            {
                using var scope = scopes.CreateScope();
                await RunAsync(scope.ServiceProvider, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "{Job} failed", GetType().Name);
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

/// <summary>Hosted mode: records what a clinic used. Counts only, in the catalog: nothing from the clinic's database.</summary>
public sealed class UsageMeter(CatalogDbContext catalog, ITenantContext current, TimeProvider clock, ILogger<UsageMeter> logger)
{
    /// <summary>After the thing happened. A failure is logged and swallowed: a signed report stays signed.</summary>
    public async Task RecordAsync(string kind, int quantity = 1, CancellationToken ct = default)
    {
        if (current.Tenant is not { } tenant)
            return;
        try
        {
            catalog.UsageEvents.Add(new UsageEvent { TenantId = tenant.Id, Kind = kind, At = clock.GetUtcNow().UtcDateTime, Quantity = quantity });
            await catalog.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning("Usage of {Tenant} not recorded: {Error}", tenant.Slug, ex.GetType().Name);
        }
    }
}

/// <summary>
/// Hosted mode, nightly: notes each working clinic's active users, then adds up the month. Clinics come from the
/// catalog (Trial, Active, PastDue), not from files. Safe to run twice: a day's snapshot is taken once and the month is
/// worked out afresh. A clinic whose database won't open is marked, for the operator and ControlPanel to see.
/// </summary>
public sealed class UsageRollup(CatalogDbContext catalog, ClinicReportDatabases databases, TimeProvider clock, ILogger<UsageRollup> logger)
{
    public async Task RunAsync(CancellationToken ct = default)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var today = now.Date;
        var tenants = await catalog.Tenants
            .Where(t => t.DeletedAt == null && (t.Status == TenantStatus.Trial || t.Status == TenantStatus.Active || t.Status == TenantStatus.PastDue))
            .OrderBy(t => t.Id).ToListAsync(ct);
        foreach (var tenant in tenants)
        {
            try
            {
                await using var db = await databases.OpenAsync(tenant.Slug);
                var seats = await PlanLimits.CountSeatsAsync(db, ct);
                if (!await catalog.UsageEvents.AnyAsync(e => e.TenantId == tenant.Id && e.Kind == UsageKinds.ActiveUser && e.At >= today, ct))
                    catalog.UsageEvents.Add(new UsageEvent { TenantId = tenant.Id, Kind = UsageKinds.ActiveUser, At = now, Quantity = seats });
                tenant.DatabaseFailed = false;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                tenant.DatabaseFailed = true;
                logger.LogError("Usage of {Tenant} not counted: {Error}", tenant.Slug, ex.GetType().Name);
            }
        }
        await catalog.SaveChangesAsync(ct);

        var monthStart = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        var month = UsageMonth.Key(now);
        var events = await catalog.UsageEvents.AsNoTracking().Where(e => e.At >= monthStart).ToListAsync(ct);
        var existing = await catalog.UsageMonths.Where(m => m.Month == month).ToListAsync(ct);
        foreach (var group in events.GroupBy(e => (e.TenantId, e.Kind)))
        {
            // Users are a level, so the month's peak; the rest are things that happened, so the total.
            var quantity = group.Key.Kind == UsageKinds.ActiveUser ? group.Max(e => e.Quantity) : group.Sum(e => e.Quantity);
            var row = existing.SingleOrDefault(m => m.TenantId == group.Key.TenantId && m.Kind == group.Key.Kind);
            if (row is null)
                catalog.UsageMonths.Add(new UsageMonth { TenantId = group.Key.TenantId, Month = month, Kind = group.Key.Kind, Quantity = quantity });
            else
                row.Quantity = quantity;
        }
        await catalog.SaveChangesAsync(ct);
    }
}

public sealed class UsageRollupService(IServiceScopeFactory scopes, TimeProvider clock, ILogger<UsageRollupService> logger)
    : PeriodicJobService(scopes, clock, logger, TimeSpan.FromDays(1))
{
    protected override Task RunAsync(IServiceProvider services, CancellationToken ct) => services.GetRequiredService<UsageRollup>().RunAsync(ct);
}
