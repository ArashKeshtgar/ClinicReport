using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using ClinicReport.Api.Auth;
using ClinicReport.Api.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClinicReport.Tests;

/// <summary>Hosted mode: from a failed payment to read-only, and back.</summary>
public class DunningTests : IAsyncLifetime
{
    private readonly BillingFactory _factory = new();

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    private async Task ChangeTenantAsync(string slug, Action<Tenant> change)
    {
        using var scope = _factory.Services.CreateScope();
        var catalog = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
        change(await catalog.Tenants.SingleAsync(t => t.Slug == slug));
        await catalog.SaveChangesAsync();
        scope.ServiceProvider.GetRequiredService<TenantDirectory>().Forget(slug);
    }

    private async Task PastDueAsync(string slug, TimeSpan ago)
    {
        await _factory.AddTenantAsync(slug);
        await ChangeTenantAsync(slug, t => (t.Status, t.PastDueSince) = (TenantStatus.PastDue, DateTime.UtcNow - ago));
    }

    private async Task<string> StatusOfAsync(string slug)
    {
        using var scope = _factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<CatalogDbContext>().Tenants.Where(t => t.Slug == slug).Select(t => t.Status).SingleAsync();
    }

    private async Task<int> RunDunningAsync()
    {
        using var scope = _factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<Dunning>().SuspendOverdueAsync();
    }

    private static readonly TimeSpan PastGrace = TimeSpan.FromDays(Tenant.GraceDays) + TimeSpan.FromMinutes(1);
    private static readonly TimeSpan WithinGrace = TimeSpan.FromDays(Tenant.GraceDays) - TimeSpan.FromMinutes(1);

    [Fact]
    public async Task Seven_days_past_due_suspends_that_clinic_only_and_only_once()
    {
        await PastDueAsync("overdue", PastGrace);
        await PastDueAsync("waiting", WithinGrace);
        await _factory.AddTenantAsync("healthy");

        Assert.Equal(1, await RunDunningAsync());

        Assert.Equal(TenantStatus.Suspended, await StatusOfAsync("overdue"));
        Assert.Equal(TenantStatus.PastDue, await StatusOfAsync("waiting"));
        Assert.Equal(TenantStatus.Active, await StatusOfAsync("healthy"));
        Assert.Equal(0, await RunDunningAsync());
    }

    [Fact]
    public async Task A_suspended_clinic_notices_at_once_and_can_still_read()
    {
        await PastDueAsync("north", PastGrace);
        var admin = (await _factory.SignInAtAsync("north")).Client;
        // Read, not written: saving the clinic would drop the cached tenant by itself.
        Assert.Equal(TenantStatus.PastDue, (string)(await admin.GetFromJsonAsync<JsonObject>("/api/billing/notice"))!["status"]!);

        await RunDunningAsync();

        Assert.Equal(HttpStatusCode.Forbidden, (await admin.PutAsJsonAsync("/api/clinic", new { name = "After" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/api/reports")).StatusCode);
    }

    [Fact]
    public async Task The_background_service_runs_the_pass_when_it_starts()
    {
        await PastDueAsync("north", PastGrace);
        var service = new DunningService(_factory.Services.GetRequiredService<IServiceScopeFactory>(), TimeProvider.System, NullLogger<DunningService>.Instance);

        await service.StartAsync(default);
        for (var i = 0; i < 100 && await StatusOfAsync("north") != TenantStatus.Suspended; i++)
            await Task.Delay(20);
        await service.StopAsync(default);

        Assert.Equal(TenantStatus.Suspended, await StatusOfAsync("north"));
    }

    private sealed class BrokenScopes : IServiceScopeFactory
    {
        public int Calls;
        public IServiceScope CreateScope()
        {
            Calls++;
            throw new InvalidOperationException("Catalog is down (test).");
        }
    }

    [Fact]
    public async Task A_failed_pass_does_not_stop_the_service()
    {
        var scopes = new BrokenScopes();
        var service = new DunningService(scopes, TimeProvider.System, NullLogger<DunningService>.Instance);

        await service.StartAsync(default);
        for (var i = 0; i < 100 && scopes.Calls == 0; i++)
            await Task.Delay(20);
        await Task.Delay(20);

        Assert.Equal(1, scopes.Calls);
        Assert.False(service.ExecuteTask!.IsCompleted);
        await service.StopAsync(default);
    }

    [Fact]
    public void Dunning_runs_in_hosted_mode_only()
    {
        Assert.Contains(_factory.Services.GetServices<IHostedService>(), s => s is DunningService);
        using var standalone = new ApiFactory();
        Assert.DoesNotContain(standalone.Services.GetServices<IHostedService>(), s => s is DunningService);
    }

    [Fact]
    public async Task Everyone_at_the_clinic_sees_the_notice_but_only_at_that_clinic()
    {
        await PastDueAsync("north", TimeSpan.FromDays(2));
        await _factory.AddTenantAsync("south");
        var typist = (await _factory.SignInAtAsync("north", "demo.typist")).Client;
        var south = (await _factory.SignInAtAsync("south")).Client;

        var notice = (await typist.GetFromJsonAsync<JsonObject>("/api/billing/notice"))!;

        Assert.Equal(TenantStatus.PastDue, (string)notice["status"]!);
        var until = (DateTime)notice["until"]!;
        Assert.InRange(until, DateTime.UtcNow.AddDays(4.9), DateTime.UtcNow.AddDays(5.1));
        Assert.Equal(HttpStatusCode.NoContent, (await south.GetAsync("/api/billing/notice")).StatusCode);
    }

    [Fact]
    public async Task The_notice_is_not_for_anyone_signed_out()
    {
        await PastDueAsync("north", TimeSpan.FromDays(2));

        Assert.Equal(HttpStatusCode.Unauthorized, (await _factory.ClientFor("north").GetAsync("/api/billing/notice")).StatusCode);
        var settings = await _factory.ClientFor("north").GetStringAsync("/api/settings");
        Assert.DoesNotContain("PastDue", settings);
    }

    [Fact]
    public async Task A_cancelled_clinic_is_told_how_long_it_can_export()
    {
        await _factory.AddTenantAsync("north");
        var admin = (await _factory.SignInAtAsync("north")).Client;
        await ChangeTenantAsync("north", t => (t.Status, t.CancelledAt) = (TenantStatus.Cancelled, DateTime.UtcNow.AddDays(-10)));

        var notice = (await admin.GetFromJsonAsync<JsonObject>("/api/billing/notice"))!;

        Assert.Equal(TenantStatus.Cancelled, (string)notice["status"]!);
        Assert.InRange((DateTime)notice["until"]!, DateTime.UtcNow.AddDays(19.9), DateTime.UtcNow.AddDays(20.1));
    }
}
