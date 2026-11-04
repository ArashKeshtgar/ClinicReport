using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using ClinicReport.Api.Auth;
using ClinicReport.Api.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ClinicReport.Tests;

/// <summary>Hosted mode: Stripe's webhook. Payloads are made up and signed the way Stripe signs them.</summary>
public class StripeWebhookTests : IAsyncLifetime
{
    private const string Secret = "whsec_not_a_real_secret";

    private sealed class WebhookFactory : BillingFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseSetting("Stripe:WebhookSecret", Secret);
        }
    }

    private readonly WebhookFactory _factory = new();
    private static readonly DateTimeOffset Start = DateTimeOffset.UtcNow.AddHours(-1);
    private int _nextEvent;

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    /// <summary>Stripe calls with no cookie and no CSRF header, at the root domain.</summary>
    private HttpClient Stripe() => _factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri($"http://{HostedFactory.BaseDomain}") });

    private static string Signature(string payload, string secret, DateTimeOffset at)
    {
        var timestamp = at.ToUnixTimeSeconds();
        var hash = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes($"{timestamp}.{payload}"));
        return $"t={timestamp},v1={Convert.ToHexStringLower(hash)}";
    }

    private static string Event(string type, string data, string? id = null, DateTimeOffset? created = null, bool live = false) => $$"""
        { "id": "{{id ?? "evt_" + Guid.NewGuid().ToString("N")}}", "object": "event", "api_version": "2020-08-27", "type": "{{type}}",
          "created": {{(created ?? Start).ToUnixTimeSeconds()}}, "livemode": {{(live ? "true" : "false")}}, "data": { "object": {{data}} } }
        """;

    private static string Checkout(string slug, string customer = "cus_1", string subscription = "sub_1") => $$"""
        { "id": "cs_test_1", "object": "checkout.session", "mode": "subscription", "client_reference_id": "{{slug}}",
          "customer": "{{customer}}", "subscription": "{{subscription}}" }
        """;

    private static string Subscription(string status, string? slug = null, string id = "sub_1", string customer = "cus_1") => $$"""
        { "id": "{{id}}", "object": "subscription", "status": "{{status}}", "customer": "{{customer}}",
          "metadata": { {{(slug is null ? "" : $"\"tenant\": \"{slug}\"")}} } }
        """;

    private const string FailedInvoice = """{ "id": "in_1", "object": "invoice", "customer": "cus_1" }""";

    private async Task<HttpResponseMessage> PostAsync(string payload, string? signature)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/billing/stripe") { Content = new StringContent(payload, Encoding.UTF8, "application/json") };
        if (signature is not null)
            request.Headers.Add("Stripe-Signature", signature);
        return await Stripe().SendAsync(request);
    }

    private async Task SendAsync(string payload) =>
        Assert.Equal(HttpStatusCode.OK, (await PostAsync(payload, Signature(payload, Secret, DateTimeOffset.UtcNow))).StatusCode);

    private DateTimeOffset Later() => Start.AddMinutes(++_nextEvent);

    private async Task<Tenant> TenantAsync(string slug)
    {
        using var scope = _factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<CatalogDbContext>().Tenants.AsNoTracking().SingleAsync(t => t.Slug == slug);
    }

    private async Task ChangeTenantAsync(string slug, Action<Tenant> change)
    {
        using var scope = _factory.Services.CreateScope();
        var catalog = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
        change(await catalog.Tenants.SingleAsync(t => t.Slug == slug));
        await catalog.SaveChangesAsync();
        scope.ServiceProvider.GetRequiredService<TenantDirectory>().Forget(slug);
    }

    private async Task SubscribedAsync(string slug)
    {
        await _factory.AddTenantAsync(slug);
        await SendAsync(Event("checkout.session.completed", Checkout(slug), created: Later()));
    }

    [Fact]
    public async Task A_finished_checkout_makes_the_clinic_pro_and_the_clinic_sees_it_at_once()
    {
        await _factory.AddTenantAsync("north", TenantStatus.Trial);
        await _factory.AddTenantAsync("south", TenantStatus.Trial);
        var admin = (await _factory.SignInAtAsync("north")).Client;
        await ChangeTenantAsync("north", t => t.Plan = Plans.Free);
        await admin.GetFromJsonAsync<JsonObject>("/api/billing");

        await SendAsync(Event("checkout.session.completed", Checkout("north"), created: Later()));

        var tenant = await TenantAsync("north");
        Assert.Equal((Plans.Pro, TenantStatus.Active, "cus_1", "sub_1"), (tenant.Plan, tenant.Status, tenant.StripeCustomerId, tenant.StripeSubscriptionId));
        var billing = (await admin.GetFromJsonAsync<JsonObject>("/api/billing"))!;
        Assert.Equal(Plans.Pro, (string)billing["plan"]!);
        Assert.True((bool)billing["canManage"]!);

        var south = await TenantAsync("south");
        Assert.Equal(Plans.Trial, south.Plan);
        Assert.Null(south.StripeCustomerId);
    }

    [Theory]
    [InlineData("whsec_the_wrong_secret", 0)]
    [InlineData(Secret, -3600)]
    public async Task A_payload_not_signed_by_stripe_just_now_changes_nothing(string secret, int secondsAgo)
    {
        await _factory.AddTenantAsync("north");
        var payload = Event("checkout.session.completed", Checkout("north"));

        var res = await PostAsync(payload, Signature(payload, secret, DateTimeOffset.UtcNow.AddSeconds(secondsAgo)));

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.NotEqual(Plans.Pro, (await TenantAsync("north")).Plan);
    }

    [Fact]
    public async Task A_payload_with_a_signature_that_does_not_match_it_changes_nothing()
    {
        await _factory.AddTenantAsync("north");
        var signed = Event("checkout.session.completed", Checkout("south"));

        var res = await PostAsync(Event("checkout.session.completed", Checkout("north")), Signature(signed, Secret, DateTimeOffset.UtcNow));

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task A_payload_with_no_signature_is_refused()
    {
        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(Event("checkout.session.completed", Checkout("north")), null)).StatusCode);
    }

    [Fact]
    public async Task A_repeated_event_does_its_work_once()
    {
        await SubscribedAsync("north");
        var failed = Event("invoice.payment_failed", FailedInvoice, id: "evt_failed", created: Later());
        await SendAsync(failed);
        var since = (await TenantAsync("north")).PastDueSince;
        await ChangeTenantAsync("north", t => (t.Status, t.PastDueSince) = (TenantStatus.Active, null));

        await SendAsync(failed);

        Assert.NotNull(since);
        Assert.Equal(TenantStatus.Active, (await TenantAsync("north")).Status);
        using var scope = _factory.Services.CreateScope();
        Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<CatalogDbContext>().ProcessedStripeEvents.CountAsync(e => e.Id == "evt_failed"));
    }

    [Fact]
    public async Task An_event_older_than_the_last_one_applied_changes_nothing()
    {
        await SubscribedAsync("north");
        var older = Later();
        await SendAsync(Event("customer.subscription.updated", Subscription("active", "north"), created: Later()));

        // Stripe doesn't promise the order: this past_due happened before the recovery above.
        await SendAsync(Event("customer.subscription.updated", Subscription("past_due", "north"), created: older));

        Assert.Equal(TenantStatus.Active, (await TenantAsync("north")).Status);
    }

    [Fact]
    public async Task A_subscription_update_can_arrive_before_the_checkout()
    {
        await _factory.AddTenantAsync("north", TenantStatus.Trial);

        await SendAsync(Event("customer.subscription.updated", Subscription("active", "north"), created: Later()));

        var tenant = await TenantAsync("north");
        Assert.Equal((Plans.Pro, "cus_1", "sub_1"), (tenant.Plan, tenant.StripeCustomerId, tenant.StripeSubscriptionId));
    }

    [Fact]
    public async Task A_failed_payment_starts_past_due_and_a_paid_one_ends_it()
    {
        await SubscribedAsync("north");

        await SendAsync(Event("invoice.payment_failed", FailedInvoice, created: Later()));
        var pastDue = await TenantAsync("north");
        Assert.Equal((Plans.Pro, TenantStatus.PastDue), (pastDue.Plan, pastDue.Status));
        Assert.NotNull(pastDue.PastDueSince);

        await ChangeTenantAsync("north", t => t.PastDueSince = DateTime.UtcNow.AddDays(-3));
        await SendAsync(Event("invoice.payment_failed", FailedInvoice, created: Later()));
        Assert.True((await TenantAsync("north")).PastDueSince < DateTime.UtcNow.AddDays(-2));

        await SendAsync(Event("customer.subscription.updated", Subscription("active", "north"), created: Later()));
        var recovered = await TenantAsync("north");
        Assert.Equal(TenantStatus.Active, recovered.Status);
        Assert.Null(recovered.PastDueSince);
    }

    [Fact]
    public async Task An_invoice_finds_the_clinic_by_its_customer_and_leaves_the_others()
    {
        await SubscribedAsync("north");
        await _factory.AddTenantAsync("south");
        await ChangeTenantAsync("south", t => (t.StripeCustomerId, t.StripeSubscriptionId) = ("cus_2", "sub_2"));

        await SendAsync(Event("invoice.payment_failed", FailedInvoice, created: Later()));

        Assert.Equal(TenantStatus.PastDue, (await TenantAsync("north")).Status);
        Assert.Equal(TenantStatus.Active, (await TenantAsync("south")).Status);
    }

    [Fact]
    public async Task A_cancelled_clinic_can_read_and_resubscribe_but_not_change_anything()
    {
        await SubscribedAsync("north");
        var admin = (await _factory.SignInAtAsync("north")).Client;

        await SendAsync(Event("customer.subscription.deleted", Subscription("canceled", "north"), created: Later()));

        var tenant = await TenantAsync("north");
        Assert.Equal((Plans.Free, TenantStatus.Cancelled, null), (tenant.Plan, tenant.Status, tenant.StripeSubscriptionId));
        Assert.NotNull(tenant.CancelledAt);
        Assert.Equal("cus_1", tenant.StripeCustomerId);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/api/reports")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.PutAsJsonAsync("/api/clinic", new { name = "Renamed" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync("/api/billing/checkout", null)).StatusCode);

        await SendAsync(Event("checkout.session.completed", Checkout("north", subscription: "sub_2"), created: Later()));
        var back = await TenantAsync("north");
        Assert.Equal((Plans.Pro, TenantStatus.Active, "sub_2"), (back.Plan, back.Status, back.StripeSubscriptionId));
        Assert.Null(back.CancelledAt);
    }

    [Fact]
    public async Task A_cancelled_clinic_is_gone_after_thirty_days()
    {
        await SubscribedAsync("north");
        await SendAsync(Event("customer.subscription.deleted", Subscription("canceled", "north"), created: Later()));

        await ChangeTenantAsync("north", t => t.CancelledAt = DateTime.UtcNow.AddDays(-Tenant.ExportDays - 1));

        Assert.Equal(HttpStatusCode.NotFound, (await _factory.ClientFor("north").GetAsync("/api/settings")).StatusCode);
    }

    [Fact]
    public async Task The_end_of_a_replaced_subscription_says_nothing_about_the_clinic()
    {
        await SubscribedAsync("north");
        await ChangeTenantAsync("north", t => t.StripeSubscriptionId = "sub_new");

        await SendAsync(Event("customer.subscription.deleted", Subscription("canceled", "north", id: "sub_old"), created: Later()));

        var tenant = await TenantAsync("north");
        Assert.Equal((Plans.Pro, TenantStatus.Active, "sub_new"), (tenant.Plan, tenant.Status, tenant.StripeSubscriptionId));
    }

    [Fact]
    public async Task A_live_event_is_ignored()
    {
        await _factory.AddTenantAsync("north");

        await SendAsync(Event("checkout.session.completed", Checkout("north"), live: true));

        Assert.NotEqual(Plans.Pro, (await TenantAsync("north")).Plan);
    }

    [Fact]
    public async Task An_event_for_nobody_is_acknowledged_so_stripe_stops_retrying()
    {
        await SendAsync(Event("checkout.session.completed", Checkout("nobody")));
        await SendAsync(Event("invoice.paid", FailedInvoice));
    }

    [Fact]
    public async Task The_webhook_is_only_at_the_root_domain()
    {
        await _factory.AddTenantAsync("north");
        var payload = Event("checkout.session.completed", Checkout("north"));
        var client = _factory.ClientFor("north");
        client.DefaultRequestHeaders.Add("Stripe-Signature", Signature(payload, Secret, DateTimeOffset.UtcNow));

        var res = await client.PostAsync("/api/billing/stripe", new StringContent(payload, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }

    [Fact]
    public async Task A_server_without_the_secret_says_so_and_stripe_retries_later()
    {
        await using var factory = new BillingFactory();
        var payload = Event("checkout.session.completed", Checkout("north"));
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri($"http://{HostedFactory.BaseDomain}") });
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/billing/stripe") { Content = new StringContent(payload) };
        request.Headers.Add("Stripe-Signature", Signature(payload, Secret, DateTimeOffset.UtcNow));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.SendAsync(request)).StatusCode);
    }
}
