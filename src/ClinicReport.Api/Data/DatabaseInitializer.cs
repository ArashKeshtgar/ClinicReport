using ClinicReport.Api.Auth;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ClinicReport.Api.Data;

public static class DatabaseInitializer
{
    /// <summary>Migrates and seeds. With <paramref name="wipe"/> the database is deleted first (demo reset only).</summary>
    public static async Task RunAsync(IServiceProvider services, bool wipe, CancellationToken ct = default)
    {
        var db = services.GetRequiredService<ClinicReportDbContext>();
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("Startup");
        var now = services.GetRequiredService<TimeProvider>().GetUtcNow().UtcDateTime;
        var hasher = services.GetRequiredService<IPasswordHasher<AppUser>>();
        var config = services.GetRequiredService<IConfiguration>();

        if (wipe)
        {
            await db.Database.EnsureDeletedAsync(ct);
            SqliteConnection.ClearAllPools();
        }
        await db.Database.MigrateAsync(ct);
        await RoleSeeder.SeedAsync(db);
        await TemplateSeeder.SeedAsync(db, Path.Combine(AppContext.BaseDirectory, "templates"), logger, now);
        if (services.GetRequiredService<IOptions<DemoOptions>>().Value.Enabled)
            await DemoSeeder.SeedAsync(db, hasher, now);
        await UserBootstrap.SeedAsync(db, config, hasher, logger, now);
    }
}

/// <summary>
/// Public demo only: puts the demo back to its starting state once a day, so whatever visitors typed
/// (including anything they should not have) does not stay.
/// </summary>
public sealed class DemoResetService(IServiceProvider services, IOptions<DemoOptions> demo, TimeProvider clock, ILogger<DemoResetService> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Connected installations have a database per clinic and no demo reset.
        if (!demo.Value.Enabled || services.IsConnected() || demo.Value.ResetHourUtc is not { } hour || hour is < 0 or > 23)
            return;

        while (!stoppingToken.IsCancellationRequested)
        {
            var now = clock.GetUtcNow();
            var next = new DateTimeOffset(now.Year, now.Month, now.Day, hour, 0, 0, TimeSpan.Zero);
            if (next <= now)
                next = next.AddDays(1);
            await Task.Delay(next - now, clock, stoppingToken);

            try
            {
                using var scope = services.CreateScope();
                await DatabaseInitializer.RunAsync(scope.ServiceProvider, wipe: true, stoppingToken);
                logger.LogInformation("Demo data reset.");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Demo reset failed.");
            }
        }
    }
}
