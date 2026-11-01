using System.Net;
using System.Net.Http.Json;
using ClinicReport.Api.Auth;
using ClinicReport.Api.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ClinicReport.Tests;

/// <summary>Hosted (multi-tenant) mode: tenants in the catalog, each at {slug}.clinicreport.test.</summary>
public class HostedFactory : ApiFactory
{
    public const string BaseDomain = "clinicreport.test";

    private readonly string _catalogPath = Path.Combine(Path.GetTempPath(), $"clinicreport-catalog-{Guid.NewGuid():N}.db");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("Hosting:Mode", "Hosted");
        builder.UseSetting("Hosting:BaseDomain", BaseDomain);
        builder.UseSetting("Hosting:CatalogConnection", $"Data Source={_catalogPath}");
    }

    public async Task AddTenantAsync(string slug, string status = TenantStatus.Active)
    {
        using var scope = Services.CreateScope();
        var catalog = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
        catalog.Tenants.Add(new Tenant { Slug = slug, Name = $"Clinic {slug}", Status = status, CreatedAt = DateTime.UtcNow });
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
        SqliteConnection.ClearAllPools();
        File.Delete(_catalogPath);
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
        Assert.Equal(HttpStatusCode.Unauthorized, (await south.GetAsync("/api/auth/me")).StatusCode);
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

    private static object NewPatient(string mrn) =>
        new { mrn, firstName = "Test", lastName = "Person", dateOfBirth = "1980-01-01", sex = "U" };
}
