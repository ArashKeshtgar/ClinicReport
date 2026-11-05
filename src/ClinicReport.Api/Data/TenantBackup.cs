using ClinicReport.Api.Auth;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace ClinicReport.Api.Data;

/// <summary>
/// Hosted: consistent copies of the catalog and every clinic's database, taken with SQLite's backup API, so a copy made
/// while the service runs is never half a transaction. One clinic can be restored without touching the others.
/// </summary>
public static class TenantBackup
{
    public const string BackupCommand = "--backup-all";
    public const string RestoreCommand = "--restore-tenant";
    public const string CatalogFile = "catalog.db";

    public static string FileName(string slug) => $"clinicreport-{slug}.db";

    /// <summary>Writes catalog.db and one file per clinic into <paramref name="folder"/>. Returns how many clinics failed.</summary>
    public static async Task<int> BackupAllAsync(IServiceProvider services, string folder, CancellationToken ct = default)
    {
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("ClinicReport.Backup");
        Directory.CreateDirectory(folder);
        List<string> slugs;
        using (var scope = services.CreateScope())
        {
            var catalog = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
            Copy(catalog.Database.GetConnectionString()!, Target(folder, CatalogFile));
            slugs = await catalog.Tenants.AsNoTracking().Where(t => t.DeletedAt == null).OrderBy(t => t.Id).Select(t => t.Slug).ToListAsync(ct);
        }

        var databases = services.GetRequiredService<ClinicReportDatabases>();
        var failed = 0;
        foreach (var slug in slugs)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                // A clinic that signed up and never came back has no database yet.
                if (databases.Exists(slug))
                    Copy(databases.ConnectionStringOf(slug), Target(folder, FileName(slug)));
            }
            catch (SqliteException ex)
            {
                failed++;
                logger.LogError("Could not back up {Clinic}: {Error}", slug, ex.SqliteErrorCode);
            }
        }
        logger.LogInformation("Backed up {Done} of {Count} tenants", slugs.Count - failed, slugs.Count);
        return failed;
    }

    /// <summary>
    /// Puts one clinic's database back from a copy. The copy is checked first, and the swap is a single transaction, so
    /// a bad file changes nothing. Whatever the clinic wrote since the copy is lost: suspend it first.
    /// </summary>
    public static async Task RestoreAsync(IServiceProvider services, string slug, string file, CancellationToken ct = default)
    {
        using (var scope = services.CreateScope())
            if (!await scope.ServiceProvider.GetRequiredService<CatalogDbContext>().Tenants.AnyAsync(t => t.Slug == slug && t.DeletedAt == null, ct))
                throw new InvalidOperationException($"No clinic \"{slug}\" to restore.");
        if (!File.Exists(file))
            throw new FileNotFoundException("No such backup file.", file);
        var source = new SqliteConnectionStringBuilder { DataSource = file, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString();
        await using (var check = new SqliteConnection(source))
        {
            await check.OpenAsync(ct);
            await using var command = check.CreateCommand();
            command.CommandText = "PRAGMA integrity_check";
            if (await command.ExecuteScalarAsync(ct) as string != "ok")
                throw new InvalidOperationException("The backup file is damaged.");
            command.CommandText = "SELECT count(*) FROM sqlite_master WHERE type = 'table' AND name = 'Reports'";
            if (Convert.ToInt32(await command.ExecuteScalarAsync(ct)) == 0)
                throw new InvalidOperationException("That is not a clinic's database.");
        }
        Copy(source, services.GetRequiredService<ClinicReportDatabases>().ConnectionStringOf(slug));
    }

    private static string Target(string folder, string name) =>
        new SqliteConnectionStringBuilder { DataSource = Path.Combine(folder, name), Pooling = false }.ToString();

    private static void Copy(string from, string to)
    {
        using var source = new SqliteConnection(from);
        using var target = new SqliteConnection(to);
        source.Open();
        target.Open();
        source.BackupDatabase(target);
    }
}
