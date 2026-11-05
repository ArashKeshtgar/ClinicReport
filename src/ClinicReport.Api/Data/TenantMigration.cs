using ClinicReport.Api.Auth;
using Microsoft.EntityFrameworkCore;

namespace ClinicReport.Api.Data;

/// <summary>
/// "--migrate-all" (Hosted): brings every tenant's database in the catalog up to date, one after another, before a new
/// version takes requests. A tenant that fails is logged and skipped; the others are still migrated.
/// </summary>
public static class TenantMigration
{
    public const string Command = "--migrate-all";

    /// <returns>How many tenants could not be migrated.</returns>
    public static async Task<int> MigrateAllAsync(IServiceProvider services, CancellationToken ct = default)
    {
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("ClinicReport.Migration");
        // Every status: a cancelled tenant can still export its data for a while, so its database has to open.
        List<string> slugs;
        using (var scope = services.CreateScope())
            slugs = await scope.ServiceProvider.GetRequiredService<CatalogDbContext>().Tenants.AsNoTracking()
                .Where(t => t.DeletedAt == null).OrderBy(t => t.Id).Select(t => t.Slug).ToListAsync(ct);

        var databases = services.GetRequiredService<ClinicReportDatabases>();
        var failed = 0;
        foreach (var slug in slugs)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await (await databases.OpenAsync(slug)).DisposeAsync();
                await MarkAsync(services, slug, failed: false, ct);
                logger.LogInformation("Migrated {Clinic}", slug);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failed++;
                // The message only, not the exception and its data: nothing from the tenant's rows goes in the log.
                logger.LogError("Could not migrate {Clinic}: {Error}", slug, ex.Message);
                await MarkAsync(services, slug, failed: true, ct);
            }
        }
        logger.LogInformation("Migrated {Done} of {Count} tenants", slugs.Count - failed, slugs.Count);
        return failed;
    }

    /// <summary>For the operator's console and /healthz/tenants.</summary>
    private static async Task MarkAsync(IServiceProvider services, string slug, bool failed, CancellationToken ct)
    {
        using var scope = services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<CatalogDbContext>().Tenants.Where(t => t.Slug == slug)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.DatabaseFailed, failed), ct);
    }
}
