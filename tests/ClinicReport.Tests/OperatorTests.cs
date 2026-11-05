using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using ClinicReport.Api.Auth;
using ClinicReport.Api.Data;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ClinicReport.Tests;

/// <summary>Hosted mode: the operator's console at admin.{base domain}, apart from every clinic.</summary>
public class OperatorTests : IAsyncLifetime
{
    private readonly HostedFactory _factory = new();

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    private HttpClient Console()
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri($"http://admin.{HostedFactory.BaseDomain}"), HandleCookies = true });
        client.DefaultRequestHeaders.Add(AuthSetup.CsrfHeader, "1");
        return client;
    }

    private static string CodeNow(string secret) => Totp.Code(secret, Totp.StepAt(DateTimeOffset.UtcNow));

    private async Task<HttpClient> SignedInAsync()
    {
        var (password, secret) = await OperatorSetup.AddAsync(_factory.Services, "ops");
        var client = Console();
        (await client.PostAsJsonAsync("/api/operator/login", new { userName = "ops", password, code = CodeNow(secret) })).EnsureSuccessStatusCode();
        return client;
    }

    private async Task<int> IdOfAsync(HttpClient console, string slug) =>
        (await console.GetFromJsonAsync<JsonArray>("/api/operator/tenants"))!.Single(t => (string)t!["slug"]! == slug)!["id"]!.GetValue<int>();

    [Fact]
    public async Task Signing_in_takes_the_password_and_a_fresh_code()
    {
        var (password, secret) = await OperatorSetup.AddAsync(_factory.Services, "ops");
        var code = CodeNow(secret);
        var client = Console();

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/operator/login", new { userName = "ops", password })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/operator/login", new { userName = "ops", password = "wrong", code })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/operator/login", new { userName = "ops", password, code = "000000" })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/operator/tenants")).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/operator/login", new { userName = "ops", password, code })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/operator/tenants")).StatusCode);

        // A code works once, even with the right password.
        Assert.Equal(HttpStatusCode.Unauthorized, (await Console().PostAsJsonAsync("/api/operator/login", new { userName = "ops", password, code })).StatusCode);
    }

    [Fact]
    public async Task Five_failures_lock_the_account_even_for_the_right_answer()
    {
        var (password, secret) = await OperatorSetup.AddAsync(_factory.Services, "ops");
        var client = Console();
        for (var i = 0; i < 5; i++)
            await client.PostAsJsonAsync("/api/operator/login", new { userName = "ops", password = "wrong", code = "000000" });

        var res = await client.PostAsJsonAsync("/api/operator/login", new { userName = "ops", password, code = CodeNow(secret) });

        Assert.Equal(HttpStatusCode.Locked, res.StatusCode);
    }

    [Fact]
    public async Task An_account_keeps_only_a_hash_and_an_encrypted_secret()
    {
        var (password, secret) = await OperatorSetup.AddAsync(_factory.Services, "ops");

        using var scope = _factory.Services.CreateScope();
        var user = await scope.ServiceProvider.GetRequiredService<CatalogDbContext>().Operators.SingleAsync();
        Assert.DoesNotContain(password, user.PasswordHash);
        Assert.DoesNotContain(secret, user.TotpSecret);
        await Assert.ThrowsAsync<InvalidOperationException>(() => OperatorSetup.AddAsync(_factory.Services, "ops"));
    }

    [Fact]
    public async Task The_list_shows_clinics_but_nothing_from_their_databases()
    {
        await _factory.AddTenantAsync("north");
        await (await _factory.SignInAtAsync("north")).Client.GetAsync("/api/reports");
        var console = await SignedInAsync();

        var body = await console.GetStringAsync("/api/operator/tenants");

        var row = JsonNode.Parse(body)!.AsArray().Single()!;
        Assert.Equal("north", (string)row["slug"]!);
        Assert.True((long)row["databaseBytes"]! > 0);
        Assert.NotNull(row["lastActivityAt"]);
        Assert.DoesNotContain("DEMO-", body);
        Assert.DoesNotContain("Sample", body);
    }

    [Fact]
    public async Task Suspending_and_resuming_take_effect_at_once_and_are_logged()
    {
        await _factory.AddTenantAsync("north");
        await _factory.AddTenantAsync("south");
        var north = (await _factory.SignInAtAsync("north")).Client;
        var south = (await _factory.SignInAtAsync("south")).Client;
        await north.GetAsync("/api/billing/notice");
        var console = await SignedInAsync();
        var id = await IdOfAsync(console, "north");

        Assert.Equal(HttpStatusCode.NoContent, (await console.PostAsync($"/api/operator/tenants/{id}/suspend", null)).StatusCode);

        Assert.Equal(HttpStatusCode.Forbidden, (await north.PutAsJsonAsync("/api/clinic", new { name = "Changed" })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await south.PutAsJsonAsync("/api/clinic", new { name = "Changed" })).StatusCode);
        var notice = (await north.GetFromJsonAsync<JsonObject>("/api/billing/notice"))!;
        Assert.True((bool)notice["byOperator"]!);

        Assert.Equal(HttpStatusCode.NoContent, (await console.PostAsync($"/api/operator/tenants/{id}/resume", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await north.PutAsJsonAsync("/api/clinic", new { name = "Changed" })).StatusCode);

        var events = (await console.GetFromJsonAsync<JsonArray>("/api/operator/events"))!;
        Assert.Contains(events, e => (string)e!["type"]! == OperatorEventTypes.TenantSuspended && (string)e["tenant"]! == "north" && (string)e["operator"]! == "ops");
        Assert.Contains(events, e => (string)e!["type"]! == OperatorEventTypes.TenantResumed);
    }

    [Fact]
    public async Task Resuming_a_trial_leaves_it_a_trial()
    {
        await _factory.AddTenantAsync("north", TenantStatus.Trial);
        var console = await SignedInAsync();
        var id = await IdOfAsync(console, "north");

        await console.PostAsync($"/api/operator/tenants/{id}/suspend", null);
        await console.PostAsync($"/api/operator/tenants/{id}/resume", null);

        using var scope = _factory.Services.CreateScope();
        Assert.Equal(TenantStatus.Trial, await scope.ServiceProvider.GetRequiredService<CatalogDbContext>().Tenants.Select(t => t.Status).SingleAsync());
    }

    [Fact]
    public async Task A_clinic_session_is_no_way_into_the_console_and_the_console_no_way_into_a_clinic()
    {
        await _factory.AddTenantAsync("north");
        var (_, clinicCookie) = await _factory.SignInAtAsync("north");
        var (password, secret) = await OperatorSetup.AddAsync(_factory.Services, "ops");
        var login = await Console().PostAsJsonAsync("/api/operator/login", new { userName = "ops", password, code = CodeNow(secret) });
        var operatorCookie = login.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith("clinicreport.operator=")).Split(';')[0];

        var clinicAtConsole = _factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri($"http://admin.{HostedFactory.BaseDomain}"), HandleCookies = false });
        clinicAtConsole.DefaultRequestHeaders.Add("Cookie", clinicCookie);
        Assert.Equal(HttpStatusCode.Unauthorized, (await clinicAtConsole.GetAsync("/api/operator/tenants")).StatusCode);

        var operatorAtClinic = _factory.ClientFor("north", cookies: false);
        operatorAtClinic.DefaultRequestHeaders.Add("Cookie", operatorCookie);
        Assert.Equal(HttpStatusCode.Unauthorized, (await operatorAtClinic.GetAsync("/api/reports")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await operatorAtClinic.GetAsync("/api/operator/tenants")).StatusCode);
    }

    [Theory]
    [InlineData("/api/reports")]
    [InlineData("/api/billing")]
    [InlineData("/api/users")]
    public async Task The_console_host_has_no_clinic_api(string path)
    {
        var console = await SignedInAsync();

        Assert.Equal(HttpStatusCode.NotFound, (await console.GetAsync(path)).StatusCode);
    }

    [Fact]
    public async Task The_console_host_is_not_a_sign_up_page()
    {
        var settings = await Console().GetFromJsonAsync<JsonObject>("/api/settings");

        Assert.True((bool)settings!["operator"]!);
        Assert.False((bool)settings["signup"]!);
        Assert.Equal(HttpStatusCode.NotFound, (await Console().PostAsJsonAsync("/api/signup", new { })).StatusCode);
    }

    [Fact]
    public async Task A_removed_operator_is_out_at_once()
    {
        var console = await SignedInAsync();
        using (var scope = _factory.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<CatalogDbContext>().Operators.ExecuteDeleteAsync();

        Assert.Equal(HttpStatusCode.Unauthorized, (await console.GetAsync("/api/operator/tenants")).StatusCode);
    }
}
