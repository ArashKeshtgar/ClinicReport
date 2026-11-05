using System.Threading.Channels;
using ClinicReport.Api.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ClinicReport.Api.Auth;

/// <summary>Clinics whose active users may have changed. A request only leaves a note here: it never waits for Stripe.</summary>
public sealed class SeatChanges
{
    private readonly Channel<string> _slugs = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });

    public void Note(string slug) => _slugs.Writer.TryWrite(slug);

    public ValueTask<string> NextAsync(CancellationToken ct) => _slugs.Reader.ReadAsync(ct);
}

/// <summary>Hosted mode: a Pro clinic pays per active user, so its subscription's quantity follows them.</summary>
public sealed class SeatSync(CatalogDbContext catalog, ClinicReportDatabases databases, IBillingGateway gateway, IOptions<StripeOptions> stripe, ILogger<SeatSync> logger)
{
    /// <summary>
    /// One clinic, or every subscribed clinic when <paramref name="slug"/> is null. What failed stays unbilled and the
    /// next pass tries again; one clinic's failure doesn't stop the others. Returns how many subscriptions it changed.
    /// </summary>
    public async Task<int> SyncAsync(string? slug = null, CancellationToken ct = default)
    {
        if (!stripe.Value.Configured)
            return 0;
        var tenants = await catalog.Tenants.AsNoTracking()
            .Where(t => t.StripeSubscriptionId != null && (slug == null || t.Slug == slug))
            .OrderBy(t => t.Id).Select(t => new { t.Slug, t.StripeSubscriptionId, t.BilledSeats }).ToListAsync(ct);
        var changed = 0;
        foreach (var tenant in tenants)
        {
            try
            {
                await using var db = await databases.OpenAsync(tenant.Slug);
                var seats = await PlanLimits.CountSeatsAsync(db, ct);
                if (seats == tenant.BilledSeats)
                    continue;
                await gateway.UpdateSeatsAsync(tenant.StripeSubscriptionId!, seats, ct);
                // Only for this subscription: the webhook may have replaced it meanwhile.
                await catalog.Tenants.Where(t => t.Slug == tenant.Slug && t.StripeSubscriptionId == tenant.StripeSubscriptionId)
                    .ExecuteUpdateAsync(s => s.SetProperty(t => t.BilledSeats, seats), ct);
                changed++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning("Seats of {Tenant} not updated: {Error}", tenant.Slug, ex.GetType().Name);
            }
        }
        return changed;
    }
}

/// <summary>Works through <see cref="SeatChanges"/>; every hour, and at start-up, checks every clinic for what was missed.</summary>
public sealed class SeatSyncService(IServiceScopeFactory scopes, SeatChanges changes, ILogger<SeatSyncService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        string? slug = null;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<SeatSync>().SyncAsync(slug, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Seat pass failed");
            }

            using var wait = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            wait.CancelAfter(Interval);
            try
            {
                slug = await changes.NextAsync(wait.Token);
            }
            catch (OperationCanceledException)
            {
                // The hour is up: a pass over every clinic. Or the service is stopping, which the loop sees.
                slug = null;
            }
        }
    }
}
