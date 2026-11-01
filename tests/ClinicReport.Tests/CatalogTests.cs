using ClinicReport.Api.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace ClinicReport.Tests;

/// <summary>The tenant catalog of Hosted mode, on its own migrations.</summary>
public class CatalogTests : IDisposable
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");

    public CatalogTests() => _connection.Open();

    public void Dispose() => _connection.Dispose();

    private CatalogDbContext Catalog() =>
        new(new DbContextOptionsBuilder<CatalogDbContext>().UseSqlite(_connection).Options);

    private async Task<CatalogDbContext> MigratedAsync()
    {
        var db = Catalog();
        await db.Database.MigrateAsync();
        return db;
    }

    [Fact]
    public async Task A_new_catalog_is_empty()
    {
        await using var db = await MigratedAsync();
        Assert.Empty(await db.Tenants.ToListAsync());
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
    }

    [Fact]
    public async Task Holds_two_tenants_with_their_own_settings()
    {
        await using (var db = await MigratedAsync())
        {
            db.Tenants.AddRange(
                new Tenant { Slug = "north", Name = "North Clinic", Status = TenantStatus.Active, CreatedAt = DateTime.UtcNow,
                    Settings = new TenantSettings { Address = "1 North St", ReportFooter = "North footer" } },
                new Tenant { Slug = "south", Name = "South Clinic", CreatedAt = DateTime.UtcNow, TrialEndsAt = DateTime.UtcNow.AddDays(14),
                    Settings = new TenantSettings { Address = "2 South St" } });
            await db.SaveChangesAsync();
        }

        await using var read = Catalog();
        var tenants = await read.Tenants.Include(t => t.Settings).OrderBy(t => t.Slug).ToListAsync();
        Assert.Equal(["north", "south"], tenants.Select(t => t.Slug));
        Assert.Equal(TenantStatus.Active, tenants[0].Status);
        Assert.Equal(TenantStatus.Trial, tenants[1].Status);
        Assert.Equal("North footer", tenants[0].Settings!.ReportFooter);
        Assert.Equal("2 South St", tenants[1].Settings!.Address);
    }

    [Fact]
    public async Task A_slug_belongs_to_one_tenant()
    {
        await using var db = await MigratedAsync();
        db.Tenants.Add(new Tenant { Slug = "north", Name = "North Clinic", CreatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();

        db.Tenants.Add(new Tenant { Slug = "north", Name = "Another North", CreatedAt = DateTime.UtcNow });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }
}

/// <summary>The catalog exists only in Hosted mode; the other modes must not need or create it.</summary>
public class CatalogRegistrationTests
{
    [Fact]
    public void Standalone_has_no_catalog()
    {
        using var factory = new ApiFactory();
        using var scope = factory.Services.CreateScope();
        Assert.Null(scope.ServiceProvider.GetService(typeof(CatalogDbContext)));
    }

    [Fact]
    public async Task Hosted_mode_creates_the_catalog_at_startup()
    {
        var path = Path.Combine(Path.GetTempPath(), $"clinicreport-catalog-{Guid.NewGuid():N}.db");
        try
        {
            await using (var factory = new ApiFactory().WithWebHostBuilder(b => b
                .UseSetting("Hosting:Mode", "Hosted")
                .UseSetting("Hosting:CatalogConnection", $"Data Source={path}")))
            {
                using var scope = factory.Services.CreateScope();
                var catalog = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
                Assert.Empty(await catalog.Database.GetPendingMigrationsAsync());
                Assert.Empty(await catalog.Tenants.ToListAsync());
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            File.Delete(path);
        }
    }
}
