using ClinicReport.Api.Auth;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Options;

namespace ClinicReport.Api.Data;

/// <summary>
/// How ClinicReport runs. Standalone and Connected (ClinicHub) are decided by the ClinicHub settings;
/// "Hosted" is the multi-tenant service: a catalog of tenants, each with its own database.
/// </summary>
public class HostingOptions
{
    public const string HostedMode = "Hosted";

    public string Mode { get; set; } = "Standalone";

    /// <summary>The tenant registry. Holds no health information, only who the tenants are and their plan.</summary>
    public string CatalogConnection { get; set; } = "Data Source=catalog.db";

    /// <summary>Each tenant is a subdomain of this, e.g. "clinicreport.example.com" for north.clinicreport.example.com.</summary>
    public string? BaseDomain { get; set; }

    /// <summary>Where each tenant's own database is, "{clinic}" being its slug.</summary>
    public string DatabasePattern { get; set; } = "Data Source=clinicreport-{clinic}.db";

    public bool Hosted => string.Equals(Mode, HostedMode, StringComparison.OrdinalIgnoreCase);
}

public static class TenantStatus
{
    public const string Trial = "Trial";
    public const string Active = "Active";
    public const string PastDue = "PastDue";
    public const string Suspended = "Suspended";
    public const string Cancelled = "Cancelled";
}

/// <summary>A clinic that uses the hosted service. Its data lives in its own database, named by <see cref="Slug"/>.</summary>
public class Tenant
{
    public int Id { get; set; }

    /// <summary>The subdomain and the database name: lower-case letters, digits and dashes.</summary>
    public string Slug { get; set; } = "";
    public string Name { get; set; } = "";
    public string Status { get; set; } = TenantStatus.Trial;
    public string Plan { get; set; } = "Trial";
    public DateTime? TrialEndsAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public string? StripeCustomerId { get; set; }
    public string? StripeSubscriptionId { get; set; }
    public TenantSettings? Settings { get; set; }
}

/// <summary>What a tenant prints on its reports. In standalone and connected mode this comes from the "Clinic" settings.</summary>
public class TenantSettings
{
    public int TenantId { get; set; }
    public string? Address { get; set; }
    public string? Phone { get; set; }
    public string? ReportFooter { get; set; }
    public string? LogoPath { get; set; }
}

public class CatalogDbContext(DbContextOptions<CatalogDbContext> options) : DbContext(options)
{
    public DbSet<Tenant> Tenants => Set<Tenant>();
    public DbSet<TenantSettings> TenantSettings => Set<TenantSettings>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Tenant>(e =>
        {
            e.Property(t => t.Slug).HasMaxLength(40);
            e.HasIndex(t => t.Slug).IsUnique();
            e.Property(t => t.Name).HasMaxLength(200);
            e.Property(t => t.Status).HasMaxLength(20);
            e.HasIndex(t => t.Status);
            e.Property(t => t.Plan).HasMaxLength(40);
            e.Property(t => t.StripeCustomerId).HasMaxLength(100);
            e.Property(t => t.StripeSubscriptionId).HasMaxLength(100);
            e.HasOne(t => t.Settings).WithOne().HasForeignKey<TenantSettings>(s => s.TenantId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<TenantSettings>(e =>
        {
            e.HasKey(s => s.TenantId);
            e.Property(s => s.Address).HasMaxLength(500);
            e.Property(s => s.Phone).HasMaxLength(50);
            e.Property(s => s.ReportFooter).HasMaxLength(1000);
            e.Property(s => s.LogoPath).HasMaxLength(260);
        });
    }
}

/// <summary>For "dotnet ef migrations add ... --context CatalogDbContext", which runs without Hosted mode.</summary>
public class CatalogDbContextFactory : IDesignTimeDbContextFactory<CatalogDbContext>
{
    public CatalogDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<CatalogDbContext>().UseSqlite("Data Source=catalog.db").Options);
}

public static class CatalogSetup
{
    public static bool IsHosted(this IServiceProvider services) =>
        services.GetRequiredService<IOptions<HostingOptions>>().Value.Hosted;

    /// <summary>Registers the tenant catalog. Returns false (and registers nothing) outside Hosted mode.</summary>
    public static bool AddTenantCatalog(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<HostingOptions>(config.GetSection("Hosting"));
        var hosting = config.GetSection("Hosting").Get<HostingOptions>() ?? new HostingOptions();
        if (!hosting.Hosted)
            return false;
        if (string.IsNullOrWhiteSpace(hosting.BaseDomain))
            throw new InvalidOperationException("Hosted mode needs Hosting:BaseDomain.");
        // Hosted clinics sign in here, each at its own subdomain; ClinicHub's sign-in is for the clinic group's own installation.
        if (config.GetSection("ClinicHub").Get<ClinicHubOptions>()?.Enabled == true)
            throw new InvalidOperationException("Hosted mode signs people in locally; leave ClinicHub:Authority empty.");
        services.AddDbContext<CatalogDbContext>(o => o.UseSqlite(hosting.CatalogConnection));
        services.AddTenancy();
        // The database of the request is the tenant of its host, prepared by the tenant middleware.
        services.AddSingleton<ClinicReportDatabases>();
        services.AddScoped(sp => sp.GetRequiredService<ClinicReportDatabases>().Open(
            sp.GetRequiredService<ITenantContext>().Tenant?.Slug ?? throw new InvalidOperationException("No tenant for this request.")));
        return true;
    }

    public static async Task MigrateCatalogAsync(IServiceProvider services, CancellationToken ct = default)
    {
        using var scope = services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<CatalogDbContext>().Database.MigrateAsync(ct);
    }
}
