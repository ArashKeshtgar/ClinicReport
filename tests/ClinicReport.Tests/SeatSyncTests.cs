using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using ClinicReport.Api.Auth;
using ClinicReport.Api.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ClinicReport.Tests;

/// <summary>Hosted mode: a subscription's quantity follows the clinic's active users. Demo tenants start with 4.</summary>
public class SeatSyncTests : IAsyncLifetime
{
    private const string Password = "correct-horse-battery";

    private readonly BillingFactory _factory = new();

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    private async Task<HttpClient> SubscribedAsync(string slug, string subscription)
    {
        await _factory.AddTenantAsync(slug);
        var admin = (await _factory.SignInAtAsync(slug)).Client;
        using var scope = _factory.Services.CreateScope();
        var catalog = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
        var tenant = await catalog.Tenants.SingleAsync(t => t.Slug == slug);
        (tenant.Plan, tenant.StripeCustomerId, tenant.StripeSubscriptionId, tenant.BilledSeats) = (Plans.Pro, $"cus_{slug}", subscription, 4);
        await catalog.SaveChangesAsync();
        scope.ServiceProvider.GetRequiredService<TenantDirectory>().Forget(slug);
        return admin;
    }

    private static async Task<HttpResponseMessage> AddUserAsync(HttpClient admin, string userName)
    {
        var roles = (await admin.GetFromJsonAsync<JsonArray>("/api/roles"))!;
        var role = roles.Single(r => (string)r!["name"]! == SystemRoles.Typist)!["id"]!.GetValue<int>();
        return await admin.PostAsJsonAsync("/api/users", new { userName, displayName = userName, roleIds = new[] { role }, password = Password });
    }

    private async Task<int> PassAsync()
    {
        using var scope = _factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<SeatSync>().SyncAsync();
    }

    private async Task<int?> BilledSeatsAsync(string slug)
    {
        using var scope = _factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<CatalogDbContext>().Tenants.Where(t => t.Slug == slug).Select(t => t.BilledSeats).SingleAsync();
    }

    private async Task WaitForSeatsAsync(string slug, int seats)
    {
        for (var i = 0; i < 150 && await BilledSeatsAsync(slug) != seats; i++)
            await Task.Delay(20);
        Assert.Equal(seats, await BilledSeatsAsync(slug));
    }

    [Fact]
    public async Task A_new_user_adds_a_seat_to_that_clinics_subscription_only()
    {
        var north = await SubscribedAsync("north", "sub_north");
        await SubscribedAsync("south", "sub_south");

        Assert.Equal(HttpStatusCode.Created, (await AddUserAsync(north, "fifth.person")).StatusCode);

        await WaitForSeatsAsync("north", 5);
        Assert.Equal(("sub_north", 5), _factory.Billing.SeatUpdates.Single());
        Assert.Equal(4, await BilledSeatsAsync("south"));
    }

    [Fact]
    public async Task Deactivating_a_user_takes_the_seat_away()
    {
        var admin = await SubscribedAsync("north", "sub_north");
        var user = (await admin.GetFromJsonAsync<JsonArray>("/api/users"))!.Single(u => (string)u!["userName"]! == "demo.echo")!;

        (await admin.PutAsJsonAsync($"/api/users/{user["id"]}", new
        {
            displayName = (string)user["displayName"]!,
            roleIds = user["roleIds"]!.AsArray().Select(r => r!.GetValue<int>()).ToArray(),
            isActive = false,
        })).EnsureSuccessStatusCode();

        await WaitForSeatsAsync("north", 3);
    }

    [Fact]
    public async Task A_clinic_with_no_subscription_has_nothing_to_update()
    {
        await _factory.AddTenantAsync("north");
        var admin = (await _factory.SignInAtAsync("north")).Client;

        await AddUserAsync(admin, "fifth.person");

        Assert.Equal(0, await PassAsync());
        Assert.Empty(_factory.Billing.SeatUpdates);
    }

    [Fact]
    public async Task Stripe_being_down_does_not_stop_a_user_being_added_and_the_next_pass_catches_up()
    {
        var admin = await SubscribedAsync("north", "sub_north");
        _factory.Billing.FailSeats = true;

        Assert.Equal(HttpStatusCode.Created, (await AddUserAsync(admin, "fifth.person")).StatusCode);
        Assert.Equal(0, await PassAsync());
        Assert.Equal(4, await BilledSeatsAsync("north"));

        _factory.Billing.FailSeats = false;
        await PassAsync();
        Assert.Equal(5, await BilledSeatsAsync("north"));
    }

    [Fact]
    public async Task A_pass_leaves_a_clinic_already_billed_right_alone()
    {
        await SubscribedAsync("north", "sub_north");

        Assert.Equal(0, await PassAsync());
        Assert.Empty(_factory.Billing.SeatUpdates);
    }

    [Fact]
    public async Task One_clinics_broken_database_does_not_stop_the_others()
    {
        await _factory.AddTenantAsync("broken");
        await File.WriteAllTextAsync(_factory.DatabaseOf("broken"), "not a database");
        using (var scope = _factory.Services.CreateScope())
        {
            var catalog = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
            await catalog.Tenants.Where(t => t.Slug == "broken").ExecuteUpdateAsync(s => s.SetProperty(t => t.StripeSubscriptionId, "sub_broken"));
        }
        await SubscribedAsync("north", "sub_north");
        using (var scope = _factory.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<CatalogDbContext>().Tenants.Where(t => t.Slug == "north").ExecuteUpdateAsync(s => s.SetProperty(t => t.BilledSeats, 2));

        Assert.Equal(1, await PassAsync());

        Assert.Equal(("sub_north", 4), _factory.Billing.SeatUpdates.Single());
    }

    [Fact]
    public async Task A_new_subscription_is_billed_for_the_first_time_by_the_next_pass()
    {
        await SubscribedAsync("north", "sub_north");
        using (var scope = _factory.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<CatalogDbContext>().Tenants.Where(t => t.Slug == "north").ExecuteUpdateAsync(s => s.SetProperty(t => t.BilledSeats, (int?)null));

        Assert.Equal(1, await PassAsync());
        Assert.Equal(4, await BilledSeatsAsync("north"));
    }

    [Fact]
    public async Task A_failed_pass_does_not_stop_the_service()
    {
        var service = new SeatSyncService(new BrokenScopes(), new SeatChanges(), NullLogger<SeatSyncService>.Instance);

        await service.StartAsync(default);
        await Task.Delay(50);

        Assert.False(service.ExecuteTask!.IsCompleted);
        await service.StopAsync(default);
    }

    private sealed class BrokenScopes : IServiceScopeFactory
    {
        public IServiceScope CreateScope() => throw new InvalidOperationException("Catalog is down (test).");
    }

    [Fact]
    public void The_seat_service_runs_in_hosted_mode_only()
    {
        Assert.Contains(_factory.Services.GetServices<IHostedService>(), s => s is SeatSyncService);
        using var standalone = new ApiFactory();
        Assert.DoesNotContain(standalone.Services.GetServices<IHostedService>(), s => s is SeatSyncService);
    }
}

/// <summary>The real gateway's seat update against a stand-in for Stripe's HTTP API.</summary>
public class StripeSeatGatewayTests
{
    private sealed class FakeStripe(int quantity) : HttpMessageHandler
    {
        public string? UpdateForm { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.Method == HttpMethod.Post)
                UpdateForm = Uri.UnescapeDataString(await request.Content!.ReadAsStringAsync(ct));
            var body = $$"""
                { "id": "sub_1", "object": "subscription", "status": "active",
                  "items": { "object": "list", "data": [ { "id": "si_1", "object": "subscription_item", "quantity": {{quantity}} } ] } }
                """;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }

    private static StripeBillingGateway Gateway(HttpMessageHandler stripe) => new(
        Options.Create(new StripeOptions { SecretKey = "sk_test_not_a_real_key", ProPriceId = "price_test_pro" }),
        NullLogger<StripeBillingGateway>.Instance, stripe);

    [Fact]
    public async Task It_changes_the_quantity_of_the_subscriptions_item()
    {
        var stripe = new FakeStripe(quantity: 3);

        await Gateway(stripe).UpdateSeatsAsync("sub_1", 5);

        Assert.Contains("items[0][id]=si_1", stripe.UpdateForm);
        Assert.Contains("items[0][quantity]=5", stripe.UpdateForm);
    }

    [Fact]
    public async Task A_quantity_already_right_is_left_alone()
    {
        var stripe = new FakeStripe(quantity: 5);

        await Gateway(stripe).UpdateSeatsAsync("sub_1", 5);

        Assert.Null(stripe.UpdateForm);
    }
}
