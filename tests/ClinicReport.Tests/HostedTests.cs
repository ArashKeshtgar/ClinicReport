using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using ClinicReport.Api.Auth;
using ClinicReport.Api.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Data.Sqlite;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace ClinicReport.Tests;

/// <summary>Every save loses the race for a report, as if others kept saving first.</summary>
public sealed class AlwaysConflictingInterceptor : SaveChangesInterceptor
{
    public int Attempts;

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        if (eventData.Context!.ChangeTracker.Entries<ClinicReport.Api.Data.Report>().Any(e => e.State == EntityState.Modified))
        {
            Interlocked.Increment(ref Attempts);
            throw new DbUpdateConcurrencyException("Lost the race (test).");
        }
        return ValueTask.FromResult(result);
    }
}

/// <summary>Hosted (multi-tenant) mode: tenants in the catalog, each at {slug}.clinicreport.test.</summary>
public class HostedFactory : ApiFactory
{
    public const string BaseDomain = "clinicreport.test";

    private readonly string _dir = Directory.CreateTempSubdirectory("clinicreport-hosted-").FullName;

    public string DatabaseOf(string slug) => Path.Combine(_dir, $"clinicreport-{slug}.db");

    protected virtual int SignupsPerHour => 1000;

    /// <summary>Mail the app sent, instead of SMTP.</summary>
    public RecordingMailSender Mail { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("Hosting:Mode", "Hosted");
        builder.UseSetting("Hosting:BaseDomain", BaseDomain);
        builder.UseSetting("Hosting:CatalogConnection", $"Data Source={Path.Combine(_dir, "catalog.db")}");
        builder.UseSetting("Hosting:DatabasePattern", $"Data Source={Path.Combine(_dir, "clinicreport-{clinic}.db")}");
        builder.UseSetting("RateLimits:SignupsPerHour", SignupsPerHour.ToString());
        builder.ConfigureTestServices(s => s.AddSingleton<IMailSender>(Mail));
    }

    /// <summary>The service's own front page, where clinics sign up.</summary>
    public HttpClient RootClient()
    {
        var client = CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri($"http://{BaseDomain}") });
        client.DefaultRequestHeaders.Add(AuthSetup.CsrfHeader, "1");
        return client;
    }

    public async Task AddTenantAsync(string slug, string status = TenantStatus.Active)
    {
        using var scope = Services.CreateScope();
        var catalog = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
        catalog.Tenants.Add(new Tenant
        {
            Slug = slug, Name = $"Clinic {slug}", Status = status, CreatedAt = DateTime.UtcNow,
            Settings = new TenantSettings { Address = $"1 {slug} Road", ReportFooter = $"Footer of {slug}" },
        });
        await catalog.SaveChangesAsync();
    }

    public HttpClient ClientFor(string slug, bool cookies = true)
    {
        var client = CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri($"http://{slug}.{BaseDomain}"), HandleCookies = cookies,
        });
        client.DefaultRequestHeaders.Add(AuthSetup.CsrfHeader, "1");
        return client;
    }

    /// <summary>Signs in at the tenant's subdomain; returns the client and its raw session cookie.</summary>
    public async Task<(HttpClient Client, string Cookie)> SignInAtAsync(string slug, string demoUser = "demo.admin")
    {
        var client = ClientFor(slug, cookies: false);
        var res = await client.PostAsJsonAsync("/api/auth/demo-login", new { userName = demoUser });
        res.EnsureSuccessStatusCode();
        var cookie = res.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith("clinicreport.auth=")).Split(';')[0];
        client.DefaultRequestHeaders.Add("Cookie", cookie);
        return (client, cookie);
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        ApiFactory.ClearPools(Directory.GetFiles(_dir, "*.db"));
        Directory.Delete(_dir, recursive: true);
    }
}

public sealed class RecordingMailSender : IMailSender
{
    public List<OutgoingMail> Sent { get; } = [];

    /// <summary>Makes sending fail, like an SMTP server that is down.</summary>
    public bool Fail { get; set; }

    public Task SendAsync(OutgoingMail mail, CancellationToken ct = default)
    {
        if (Fail)
            throw new IOException("SMTP is down (test).");
        lock (Sent)
            Sent.Add(mail);
        return Task.CompletedTask;
    }

    /// <summary>The confirmation token in the last mail to this address.</summary>
    public string TokenFor(string to)
    {
        lock (Sent)
        {
            var body = Sent.Last(m => m.To == to).Body;
            var start = body.IndexOf("token=", StringComparison.Ordinal) + "token=".Length;
            // Split() with no separators splits on any white space: the token ends its line.
            return body[start..].Split()[0];
        }
    }
}

/// <summary>Records which database file each query ran against.</summary>
public sealed class QueryRecorder : DbCommandInterceptor
{
    public List<(string Database, string Sql)> Queries { get; } = [];

    private void Record(DbCommand command)
    {
        lock (Queries)
            Queries.Add((command.Connection!.DataSource, command.CommandText));
    }

    public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
    {
        Record(command);
        return result;
    }

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
        InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
    {
        Record(command);
        return ValueTask.FromResult(result);
    }
}

public class HostedTests : IAsyncLifetime
{
    private readonly HostedFactory _factory = new();

    public async Task InitializeAsync()
    {
        await _factory.AddTenantAsync("north");
        await _factory.AddTenantAsync("south");
        await _factory.AddTenantAsync("paused", TenantStatus.Suspended);
        await _factory.AddTenantAsync("gone", TenantStatus.Cancelled);
    }

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    [Fact]
    public async Task The_catalog_is_created_at_startup()
    {
        using var scope = _factory.Services.CreateScope();
        var catalog = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
        Assert.Empty(await catalog.Database.GetPendingMigrationsAsync());
        Assert.Equal(4, await catalog.Tenants.CountAsync());
    }

    [Theory]
    [InlineData("nobody")]
    [InlineData("gone")]
    public async Task Unknown_and_cancelled_tenants_are_not_found(string slug)
    {
        var client = _factory.ClientFor(slug);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/settings")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsJsonAsync("/api/auth/demo-login", new { userName = "demo.admin" })).StatusCode);
    }

    [Fact]
    public async Task A_host_outside_the_base_domain_is_not_a_tenant()
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("http://north.elsewhere.test") });
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/settings")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/healthz")).StatusCode);
    }

    [Fact]
    public void The_slug_is_one_label_of_the_base_domain()
    {
        Assert.Equal("north", Tenancy.SlugOf("North.clinicreport.test", HostedFactory.BaseDomain));
        Assert.Null(Tenancy.SlugOf("a.north.clinicreport.test", HostedFactory.BaseDomain));
        Assert.Null(Tenancy.SlugOf("clinicreport.test", HostedFactory.BaseDomain));
        Assert.Null(Tenancy.SlugOf("north.otherclinicreport.test", HostedFactory.BaseDomain));
    }

    [Fact]
    public async Task A_session_works_at_its_own_tenant()
    {
        var (north, _) = await _factory.SignInAtAsync("north");
        Assert.Equal(HttpStatusCode.OK, (await north.GetAsync("/api/patients")).StatusCode);
    }

    [Fact]
    public async Task A_session_from_one_tenant_is_refused_at_another()
    {
        // Both tenants share the same user Ids; only the session's clinic tells them apart.
        var (_, northCookie) = await _factory.SignInAtAsync("north");
        var south = _factory.ClientFor("south", cookies: false);
        south.DefaultRequestHeaders.Add("Cookie", northCookie);

        Assert.Equal(HttpStatusCode.Unauthorized, (await south.GetAsync("/api/patients")).StatusCode);
        // Not signed in there: "me" has no one to report.
        Assert.Equal(HttpStatusCode.NoContent, (await south.GetAsync("/api/auth/me")).StatusCode);
    }

    [Fact]
    public async Task The_session_cookie_is_limited_to_its_own_host()
    {
        var client = _factory.ClientFor("north", cookies: false);
        var res = await client.PostAsJsonAsync("/api/auth/demo-login", new { userName = "demo.admin" });
        var cookie = res.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith("clinicreport.auth="));
        Assert.DoesNotContain("domain=", cookie, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_suspended_tenant_can_read_but_not_write()
    {
        var (paused, _) = await _factory.SignInAtAsync("paused");
        Assert.Equal(HttpStatusCode.OK, (await paused.GetAsync("/api/patients")).StatusCode);
        var write = await paused.PostAsJsonAsync("/api/patients", NewPatient("T-SUSP-1"));
        Assert.Equal(HttpStatusCode.Forbidden, write.StatusCode);

        var (north, _) = await _factory.SignInAtAsync("north");
        Assert.Equal(HttpStatusCode.Created, (await north.PostAsJsonAsync("/api/patients", NewPatient("T-ACT-1"))).StatusCode);
    }

    [Fact]
    public async Task Settings_are_the_tenants_own()
    {
        var north = await _factory.ClientFor("north").GetFromJsonAsync<Settings>("/api/settings");
        var south = await _factory.ClientFor("south").GetFromJsonAsync<Settings>("/api/settings");
        Assert.Equal(new Settings("Clinic north", "1 north Road", "Footer of north"), north);
        Assert.Equal(new Settings("Clinic south", "1 south Road", "Footer of south"), south);
    }

    [Fact]
    public async Task Each_tenant_has_its_own_database()
    {
        var (north, _) = await _factory.SignInAtAsync("north");
        var (south, _) = await _factory.SignInAtAsync("south");
        var created = await north.PostAsJsonAsync("/api/patients", NewPatient("T-ISO-1"));
        var id = (await created.Content.ReadFromJsonAsync<Patient>())!.Id;

        Assert.True(File.Exists(_factory.DatabaseOf("north")));
        Assert.True(File.Exists(_factory.DatabaseOf("south")));
        Assert.Contains((await north.GetFromJsonAsync<Patient[]>("/api/patients?q=T-ISO-1"))!, p => p.Mrn == "T-ISO-1");
        Assert.Empty((await south.GetFromJsonAsync<Patient[]>("/api/patients?q=T-ISO-1"))!);
        // The same Id at south is south's own row, or nothing.
        var res = await south.GetAsync($"/api/patients/{id}");
        Assert.True(res.StatusCode == HttpStatusCode.NotFound || (await res.Content.ReadFromJsonAsync<Patient>())!.Mrn != "T-ISO-1");
    }

    [Fact]
    public async Task A_session_from_another_tenant_never_reads_this_tenants_users()
    {
        var recorder = new QueryRecorder();
        var factory = _factory.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.AddSingleton<IInterceptor>(recorder)));
        var signIn = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri($"http://north.{HostedFactory.BaseDomain}") });
        signIn.DefaultRequestHeaders.Add(AuthSetup.CsrfHeader, "1");
        var login = await signIn.PostAsJsonAsync("/api/auth/demo-login", new { userName = "demo.admin" });
        var northCookie = login.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith("clinicreport.auth=")).Split(';')[0];

        var south = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri($"http://south.{HostedFactory.BaseDomain}") });
        await south.GetAsync("/api/settings");   // south's database is prepared (and seeded) here
        lock (recorder.Queries) recorder.Queries.Clear();

        south.DefaultRequestHeaders.Add("Cookie", northCookie);
        Assert.Equal(HttpStatusCode.Unauthorized, (await south.GetAsync("/api/patients")).StatusCode);
        Assert.DoesNotContain(recorder.Queries, q => q.Database == _factory.DatabaseOf("south") && q.Sql.Contains("\"Users\""));
    }

    [Fact]
    public async Task An_edit_that_keeps_losing_the_race_gets_a_clear_answer_not_a_crash()
    {
        var conflicts = new AlwaysConflictingInterceptor();
        var factory = _factory.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.AddSingleton<IInterceptor>(conflicts)));
        await _factory.AddTenantAsync("conflicts");
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri($"http://conflicts.{HostedFactory.BaseDomain}"), HandleCookies = true });
        client.DefaultRequestHeaders.Add(AuthSetup.CsrfHeader, "1");
        (await client.PostAsJsonAsync("/api/auth/demo-login", new { userName = "demo.typist" })).EnsureSuccessStatusCode();
        var draft = (await client.GetFromJsonAsync<System.Text.Json.Nodes.JsonArray>("/api/reports"))!.First(r => (string)r!["status"]! == "Draft")!;

        var res = await client.PatchAsJsonAsync($"/api/reports/{draft["id"]}/values", new { values = new { heart_rate = 70 } });

        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
        Assert.Contains("Try again", await res.Content.ReadAsStringAsync());
        Assert.True(conflicts.Attempts > 1);
    }

    [Fact]
    public async Task A_tenants_report_can_be_opened_live()
    {
        var (client, cookie) = await _factory.SignInAtAsync("north", "demo.physician");
        var report = (int)(await client.GetFromJsonAsync<System.Text.Json.Nodes.JsonArray>("/api/reports"))![0]!["id"]!;

        var presence = new TaskCompletionSource<string>();
        await using var hub = LiveIsolationTests.LongPollingHub(_factory.Server, client.BaseAddress!, cookie);
        hub.On<System.Text.Json.JsonElement>("presence", p => presence.TrySetResult(p.GetRawText()));
        await hub.StartAsync();
        await hub.InvokeAsync("JoinReport", report);

        Assert.Single(System.Text.Json.Nodes.JsonNode.Parse(await presence.Task.WaitAsync(TimeSpan.FromSeconds(10)))!.AsArray());
    }

    [Fact]
    public async Task A_tenant_change_in_the_catalog_applies_once_the_tenant_is_forgotten()
    {
        var (client, _) = await _factory.SignInAtAsync("north");
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync("/api/patients", NewPatient("H-CACHE-1"))).StatusCode);

        using (var scope = _factory.Services.CreateScope())
        {
            var catalog = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
            (await catalog.Tenants.SingleAsync(t => t.Slug == "north")).Status = TenantStatus.Suspended;
            await catalog.SaveChangesAsync();
        }
        // Still the cached tenant: the catalog is not read on every request.
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync("/api/patients", NewPatient("H-CACHE-2"))).StatusCode);

        _factory.Services.GetRequiredService<TenantDirectory>().Forget("north");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/patients", NewPatient("H-CACHE-3"))).StatusCode);
    }

    private record Settings(string Name, string? Address, string? ReportFooter);

    private record Patient(int Id, string Mrn);

    private static object NewPatient(string mrn) =>
        new { mrn, firstName = "Test", lastName = "Person", dateOfBirth = "1980-01-01", sex = "U" };
}
