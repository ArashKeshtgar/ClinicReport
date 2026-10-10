using Microsoft.EntityFrameworkCore;

namespace ClinicReport.Api.Data;

public class ClinicReportDbContext(DbContextOptions<ClinicReportDbContext> options) : DbContext(options)
{
    public DbSet<AppUser> Users => Set<AppUser>();
    public DbSet<Patient> Patients => Set<Patient>();
    public DbSet<ReportTemplate> Templates => Set<ReportTemplate>();
    public DbSet<TemplateVersion> TemplateVersions => Set<TemplateVersion>();
    public DbSet<Report> Reports => Set<Report>();
    public DbSet<ReportAddendum> Addenda => Set<ReportAddendum>();
    public DbSet<ReportChange> Changes => Set<ReportChange>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<AppUser>(e =>
        {
            e.Property(u => u.UserName).HasMaxLength(64).UseCollation("NOCASE");
            e.HasIndex(u => u.UserName).IsUnique();
            e.Property(u => u.DisplayName).HasMaxLength(128);
            e.Property(u => u.Credentials).HasMaxLength(64);
            e.Property(u => u.Role).HasMaxLength(16);
        });

        b.Entity<Patient>(e =>
        {
            e.Property(p => p.Mrn).HasMaxLength(32);
            e.HasIndex(p => p.Mrn).IsUnique();
            e.Property(p => p.FirstName).HasMaxLength(64);
            e.Property(p => p.LastName).HasMaxLength(64).UseCollation("NOCASE");
            e.HasIndex(p => p.LastName);
            e.Property(p => p.Sex).HasMaxLength(1);
        });

        b.Entity<ReportTemplate>(e =>
        {
            e.Property(t => t.Key).HasMaxLength(64);
            e.HasIndex(t => t.Key).IsUnique();
            e.Property(t => t.Name).HasMaxLength(128);
            e.Property(t => t.Service).HasMaxLength(64);
        });

        b.Entity<TemplateVersion>(e =>
        {
            e.HasIndex(v => new { v.TemplateId, v.Version }).IsUnique();
            e.Property(v => v.Status).HasMaxLength(16);
            e.HasOne(v => v.Template).WithMany(t => t.Versions).HasForeignKey(v => v.TemplateId);
        });

        b.Entity<Report>(e =>
        {
            e.Property(r => r.Status).HasMaxLength(16);
            e.Property(r => r.Indication).HasMaxLength(500);
            e.Property(r => r.ReferringPhysician).HasMaxLength(128);
            e.Property(r => r.Revision).IsConcurrencyToken();
            e.HasIndex(r => new { r.Status, r.ExamDate });
            e.HasOne(r => r.Patient).WithMany().HasForeignKey(r => r.PatientId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(r => r.TemplateVersion).WithMany().HasForeignKey(r => r.TemplateVersionId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(r => r.CreatedBy).WithMany().HasForeignKey(r => r.CreatedByUserId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(r => r.SignedBy).WithMany().HasForeignKey(r => r.SignedByUserId).OnDelete(DeleteBehavior.Restrict);
            e.HasMany(r => r.Addenda).WithOne().HasForeignKey(a => a.ReportId);
        });

        b.Entity<ReportAddendum>(e =>
        {
            e.Property(a => a.Text).HasMaxLength(4000);
            e.HasOne(a => a.User).WithMany().HasForeignKey(a => a.UserId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<ReportChange>(e =>
        {
            e.Property(c => c.Field).HasMaxLength(64);
            e.HasIndex(c => new { c.ReportId, c.At });
            e.HasOne<Report>().WithMany().HasForeignKey(c => c.ReportId);
            e.HasOne<AppUser>().WithMany().HasForeignKey(c => c.UserId).OnDelete(DeleteBehavior.Restrict);
        });
    }
}
