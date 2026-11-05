using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using ClinicReport.Api.Auth;
using ClinicReport.Api.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ClinicReport.Tests;

/// <summary>Hosted mode: a clinic leaving with its data, being deleted, and being backed up and restored on its own.</summary>
public class TenantLifecycleTests : IAsyncLifetime
{
    private const string HealthToken = "health-token-for-tests";

    private sealed class Factory : HostedFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseSetting("Health:Token", HealthToken);
        }
    }

    private readonly Factory _factory = new();
    private readonly string _folder = Directory.CreateTempSubdirectory("clinicreport-backup-").FullName;

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        await _factory.DisposeAsync();
        ApiFactory.ClearPools(Directory.GetFiles(_folder, "*.db"));
        Directory.Delete(_folder, recursive: true);
    }

    private static async Task<JsonNode> Json(HttpResponseMessage res)
    {
        var body = await res.Content.ReadAsStringAsync();
        Assert.True(res.IsSuccessStatusCode, $"{(int)res.StatusCode}: {body}");
        return JsonNode.Parse(body)!;
    }

    private async Task SignAReportAsync(string slug)
    {
        var typist = (await _factory.SignInAtAsync(slug, "demo.typist")).Client;
        var physician = (await _factory.SignInAtAsync(slug, "demo.physician")).Client;
        var echo = (await typist.GetFromJsonAsync<JsonArray>("/api/templates"))!.Single(t => (string)t!["key"]! == "echo-tte")!;
        var patient = (await typist.GetFromJsonAsync<JsonArray>("/api/patients?q=DEMO-0003"))![0]!;
        var id = (await Json(await typist.PostAsJsonAsync("/api/reports", new
        {
            patientId = patient["id"]!.GetValue<int>(), templateId = echo["id"]!.GetValue<int>(), examDate = "2026-10-09",
            heightCm = 165, weightKg = 60, indication = "Palpitations",
        })))["id"]!.GetValue<int>();
        foreach (var section in new[] { "study", "lv", "rv", "mitral", "aortic", "tricuspid", "pericardium" })
            (await typist.PostAsync($"/api/reports/{id}/presets/{section}", null)).EnsureSuccessStatusCode();
        (await physician.PutAsJsonAsync($"/api/reports/{id}/conclusion", new { conclusion = "Normal study." })).EnsureSuccessStatusCode();
        await Json(await physician.PostAsync($"/api/reports/{id}/sign", null));
    }

    private async Task<HttpClient> ConsoleAsync()
    {
        var (password, secret) = await OperatorSetup.AddAsync(_factory.Services, "ops");
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri($"http://admin.{HostedFactory.BaseDomain}"), HandleCookies = true });
        client.DefaultRequestHeaders.Add(AuthSetup.CsrfHeader, "1");
        (await client.PostAsJsonAsync("/api/operator/login", new { userName = "ops", password, code = Totp.Code(secret, Totp.StepAt(DateTimeOffset.UtcNow)) })).EnsureSuccessStatusCode();
        return client;
    }

    private async Task ChangeTenantAsync(string slug, Action<Tenant> change)
    {
        using var scope = _factory.Services.CreateScope();
        var catalog = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
        change(await catalog.Tenants.Include(t => t.Settings).SingleAsync(t => t.Slug == slug));
        await catalog.SaveChangesAsync();
        scope.ServiceProvider.GetRequiredService<TenantDirectory>().Forget(slug);
    }

    private static async Task<ZipArchive> ExportAsync(HttpClient admin)
    {
        var res = await admin.GetAsync("/api/clinic/export");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("application/zip", res.Content.Headers.ContentType!.MediaType);
        return new ZipArchive(new MemoryStream(await res.Content.ReadAsByteArrayAsync()));
    }

    private static string Text(ZipArchive zip, string name)
    {
        using var reader = new StreamReader(zip.GetEntry(name)!.Open());
        return reader.ReadToEnd();
    }

    [Fact]
    public async Task An_export_holds_the_clinics_data_and_a_pdf_of_each_signed_report()
    {
        await _factory.AddTenantAsync("north");
        await SignAReportAsync("north");
        var admin = (await _factory.SignInAtAsync("north")).Client;

        using var zip = await ExportAsync(admin);

        Assert.Contains("DEMO-0001", Text(zip, "patients.json"));
        var reports = JsonNode.Parse(Text(zip, "reports.json"))!.AsArray();
        var signed = reports.Single(r => r!["signedAt"] is not null)!;
        Assert.Equal("Normal study.", (string)signed["conclusion"]!);
        Assert.NotNull(zip.GetEntry("templates/echo-tte.v1.json"));
        Assert.Contains("ReportSigned", Text(zip, "audit.json"));
        Assert.Equal(1, (int)JsonNode.Parse(Text(zip, "manifest.json"))!["signedReports"]!);

        await using var pdf = zip.GetEntry($"reports-pdf/report-{signed["id"]}.pdf")!.Open();
        var head = new byte[5];
        await pdf.ReadExactlyAsync(head);
        Assert.Equal("%PDF-", System.Text.Encoding.ASCII.GetString(head));
    }

    [Fact]
    public async Task An_export_carries_no_password_or_secret()
    {
        await _factory.AddTenantAsync("north");
        var admin = (await _factory.SignInAtAsync("north")).Client;

        using var zip = await ExportAsync(admin);

        var users = Text(zip, "users.json");
        Assert.DoesNotContain("password", users, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret", users, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("stamp", users, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task An_export_is_one_clinics_only_and_is_in_the_audit_trail()
    {
        await _factory.AddTenantAsync("north");
        await _factory.AddTenantAsync("south");
        await SignAReportAsync("south");
        var north = (await _factory.SignInAtAsync("north")).Client;

        using (var zip = await ExportAsync(north))
            Assert.Equal(0, (int)JsonNode.Parse(Text(zip, "manifest.json"))!["signedReports"]!);

        await using var db = _factory.Services.GetRequiredService<ClinicReportDatabases>().Create("north");
        Assert.True(await db.SecurityEvents.AnyAsync(e => e.Type == SecurityEventTypes.DataExported));
    }

    [Fact]
    public async Task Only_an_administrator_can_export()
    {
        await _factory.AddTenantAsync("north");
        var physician = (await _factory.SignInAtAsync("north", "demo.physician")).Client;

        Assert.Equal(HttpStatusCode.Forbidden, (await physician.GetAsync("/api/clinic/export")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _factory.ClientFor("north").GetAsync("/api/clinic/export")).StatusCode);
    }

    [Theory]
    [InlineData(TenantStatus.Suspended)]
    [InlineData(TenantStatus.Cancelled)]
    public async Task A_clinic_on_its_way_out_can_still_export(string status)
    {
        await _factory.AddTenantAsync("north");
        var admin = (await _factory.SignInAtAsync("north")).Client;
        await ChangeTenantAsync("north", t => (t.Status, t.CancelledAt) = (status, DateTime.UtcNow));

        using var zip = await ExportAsync(admin);

        Assert.NotNull(zip.GetEntry("manifest.json"));
    }

    private async Task<(HttpClient Console, int Id)> GoneTenantAsync(string slug, int daysAgo = Tenant.ExportDays + 1)
    {
        await _factory.AddTenantAsync(slug);
        await (await _factory.SignInAtAsync(slug)).Client.GetAsync("/api/settings");
        await ChangeTenantAsync(slug, t =>
        {
            (t.Status, t.CancelledAt, t.OwnerEmail) = (TenantStatus.Cancelled, DateTime.UtcNow.AddDays(-daysAgo), $"owner@{slug}.example.com");
        });
        var console = await ConsoleAsync();
        var id = (await console.GetFromJsonAsync<JsonArray>("/api/operator/tenants"))!.Single(t => (string)t!["slug"]! == slug)!["id"]!.GetValue<int>();
        return (console, id);
    }

    [Fact]
    public async Task Deleting_removes_the_database_keeps_the_record_and_logs_it()
    {
        var (console, id) = await GoneTenantAsync("north");
        Assert.True(File.Exists(_factory.DatabaseOf("north")));

        Assert.Equal(HttpStatusCode.NoContent, (await console.PostAsJsonAsync($"/api/operator/tenants/{id}/delete", new { confirmSlug = "north" })).StatusCode);

        Assert.False(File.Exists(_factory.DatabaseOf("north")));
        var row = (await console.GetFromJsonAsync<JsonArray>("/api/operator/tenants"))!.Single()!;
        Assert.NotNull(row["deletedAt"]);
        Assert.False((bool)row["canDelete"]!);
        var events = (await console.GetFromJsonAsync<JsonArray>("/api/operator/events"))!;
        Assert.Contains(events, e => (string)e!["type"]! == OperatorEventTypes.TenantDeleted && (string)e["tenant"]! == "north");
        using var scope = _factory.Services.CreateScope();
        Assert.Null(await scope.ServiceProvider.GetRequiredService<CatalogDbContext>().Tenants.Select(t => t.OwnerEmail).SingleAsync());
    }

    [Fact]
    public async Task A_clinic_still_in_its_time_to_export_cannot_be_deleted()
    {
        var (console, id) = await GoneTenantAsync("north", daysAgo: 5);

        Assert.Equal(HttpStatusCode.Conflict, (await console.PostAsJsonAsync($"/api/operator/tenants/{id}/delete", new { confirmSlug = "north" })).StatusCode);

        Assert.True(File.Exists(_factory.DatabaseOf("north")));
    }

    [Fact]
    public async Task Deleting_needs_the_slug_typed_out()
    {
        var (console, id) = await GoneTenantAsync("north");

        Assert.Equal(HttpStatusCode.BadRequest, (await console.PostAsJsonAsync($"/api/operator/tenants/{id}/delete", new { confirmSlug = "south" })).StatusCode);

        Assert.True(File.Exists(_factory.DatabaseOf("north")));
    }

    [Fact]
    public async Task A_deleted_clinic_stays_deleted()
    {
        var (console, id) = await GoneTenantAsync("north");
        await console.PostAsJsonAsync($"/api/operator/tenants/{id}/delete", new { confirmSlug = "north" });

        Assert.Equal(HttpStatusCode.NotFound, (await _factory.ClientFor("north").GetAsync("/api/settings")).StatusCode);
        Assert.Equal(0, await TenantMigration.MigrateAllAsync(_factory.Services));
        Assert.False(File.Exists(_factory.DatabaseOf("north")));
        Assert.NotEqual(HttpStatusCode.Created, (await _factory.RootClient().PostAsJsonAsync("/api/signup",
            new { clinicName = "New North", slug = "north", adminName = "New Owner", email = "new@north.example.com", password = "a long passphrase 42" })).StatusCode);
    }

    [Fact]
    public async Task A_clinic_is_restored_on_its_own_from_last_nights_copy()
    {
        await _factory.AddTenantAsync("north");
        await _factory.AddTenantAsync("south");
        var north = (await _factory.SignInAtAsync("north")).Client;
        var south = (await _factory.SignInAtAsync("south")).Client;
        Assert.Equal(0, await TenantBackup.BackupAllAsync(_factory.Services, _folder));
        Assert.True(File.Exists(Path.Combine(_folder, TenantBackup.CatalogFile)));

        var added = new { mrn = "AFTER-0001", firstName = "After", lastName = "Backup", dateOfBirth = "1980-01-01", sex = "F" };
        (await north.PostAsJsonAsync("/api/patients", added)).EnsureSuccessStatusCode();
        (await south.PostAsJsonAsync("/api/patients", added)).EnsureSuccessStatusCode();

        await TenantBackup.RestoreAsync(_factory.Services, "north", Path.Combine(_folder, TenantBackup.FileName("north")));

        Assert.Empty((await north.GetFromJsonAsync<JsonArray>("/api/patients?q=AFTER-0001"))!);
        Assert.Single((await south.GetFromJsonAsync<JsonArray>("/api/patients?q=AFTER-0001"))!);
        Assert.NotEmpty((await north.GetFromJsonAsync<JsonArray>("/api/patients?q=DEMO-0001"))!);
    }

    [Fact]
    public async Task A_damaged_backup_changes_nothing()
    {
        await _factory.AddTenantAsync("north");
        var north = (await _factory.SignInAtAsync("north")).Client;
        var bad = Path.Combine(_folder, "bad.db");
        await File.WriteAllTextAsync(bad, "not a database at all, but long enough to be read as one");

        await Assert.ThrowsAnyAsync<Exception>(() => TenantBackup.RestoreAsync(_factory.Services, "north", bad));

        Assert.NotEmpty((await north.GetFromJsonAsync<JsonArray>("/api/patients?q=DEMO-0001"))!);
    }

    [Fact]
    public async Task Restoring_the_catalog_file_over_a_clinic_is_refused()
    {
        await _factory.AddTenantAsync("north");
        await (await _factory.SignInAtAsync("north")).Client.GetAsync("/api/settings");
        await TenantBackup.BackupAllAsync(_factory.Services, _folder);

        await Assert.ThrowsAsync<InvalidOperationException>(() => TenantBackup.RestoreAsync(_factory.Services, "north", Path.Combine(_folder, TenantBackup.CatalogFile)));
    }

    [Fact]
    public async Task The_health_of_the_tenants_takes_a_token_and_gives_only_counts()
    {
        await _factory.AddTenantAsync("north");
        await _factory.AddTenantAsync("south", TenantStatus.Suspended);
        await ChangeTenantAsync("south", t => t.DatabaseFailed = true);
        var client = _factory.CreateClient();

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/healthz/tenants")).StatusCode);

        client.DefaultRequestHeaders.Add("X-Health-Token", HealthToken);
        var body = await client.GetStringAsync("/healthz/tenants");
        var health = JsonNode.Parse(body)!;
        Assert.Equal(2, (int)health["tenants"]!);
        Assert.Equal(1, (int)health["databaseFailed"]!);
        Assert.Equal(1, (int)health["byStatus"]![TenantStatus.Suspended]!);
        Assert.DoesNotContain("north", body);
    }

    [Fact]
    public async Task Migrating_marks_a_clinic_that_fails_and_clears_it_once_it_works()
    {
        await _factory.AddTenantAsync("north");
        await ChangeTenantAsync("north", t => t.DatabaseFailed = true);

        Assert.Equal(0, await TenantMigration.MigrateAllAsync(_factory.Services));

        using var scope = _factory.Services.CreateScope();
        Assert.False(await scope.ServiceProvider.GetRequiredService<CatalogDbContext>().Tenants.Select(t => t.DatabaseFailed).SingleAsync());
    }
}
