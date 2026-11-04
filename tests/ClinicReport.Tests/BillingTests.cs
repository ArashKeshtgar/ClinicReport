using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using ClinicReport.Api.Auth;
using ClinicReport.Api.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ClinicReport.Tests;

/// <summary>Stands in for Stripe and keeps what the app asked of it.</summary>
public sealed class RecordingBillingGateway : IBillingGateway
{
    public List<CheckoutRequest> Checkouts { get; } = [];
    public List<(string Customer, string ReturnUrl)> Portals { get; } = [];

    public Task<string> CreateCheckoutUrlAsync(CheckoutRequest request, CancellationToken ct = default)
    {
        lock (Checkouts)
            Checkouts.Add(request);
        return Task.FromResult($"https://stripe.test/checkout/{request.Slug}");
    }

    public Task<string> CreatePortalUrlAsync(string customerId, string returnUrl, CancellationToken ct = default)
    {
        lock (Portals)
            Portals.Add((customerId, returnUrl));
        return Task.FromResult($"https://stripe.test/portal/{customerId}");
    }
}

public class BillingFactory : HostedFactory
{
    public RecordingBillingGateway Billing { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("Stripe:SecretKey", "sk_test_not_a_real_key");
        builder.UseSetting("Stripe:ProPriceId", "price_test_pro");
        builder.ConfigureTestServices(s => s.AddSingleton<IBillingGateway>(Billing));
    }
}

/// <summary>Hosted mode: a clinic's plan, its usage, and the way to Stripe's pages.</summary>
public class BillingTests : IAsyncLifetime
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

    private async Task<HttpClient> AdminOfAsync(string slug, string status = TenantStatus.Active)
    {
        await _factory.AddTenantAsync(slug, status);
        return (await _factory.SignInAtAsync(slug)).Client;
    }

    private static async Task<JsonNode> Json(HttpResponseMessage res)
    {
        var body = await res.Content.ReadAsStringAsync();
        Assert.True(res.IsSuccessStatusCode, $"{(int)res.StatusCode}: {body}");
        return JsonNode.Parse(body)!;
    }

    [Fact]
    public async Task Checkout_is_for_the_clinic_of_the_host_with_a_seat_for_each_active_user()
    {
        var north = await AdminOfAsync("north");
        var south = await AdminOfAsync("south");
        await ChangeTenantAsync("south", t => t.OwnerEmail = "owner@south.example.com");
        await using (var db = _factory.Services.GetRequiredService<ClinicReportDatabases>().Create("north"))
            await db.Users.Where(u => u.UserName == "demo.echo").ExecuteUpdateAsync(s => s.SetProperty(u => u.IsActive, false));

        var url = await Json(await north.PostAsync("/api/billing/checkout", null));
        await Json(await south.PostAsync("/api/billing/checkout", null));

        Assert.Equal("https://stripe.test/checkout/north", (string)url["url"]!);
        var sent = _factory.Billing.Checkouts;
        Assert.Equal("north", sent[0].Slug);
        Assert.Equal(3, sent[0].Seats);
        Assert.Equal($"http://north.{HostedFactory.BaseDomain}/billing?checkout=success", sent[0].SuccessUrl);
        Assert.Equal($"http://north.{HostedFactory.BaseDomain}/billing?checkout=cancelled", sent[0].CancelUrl);
        Assert.Equal("south", sent[1].Slug);
        Assert.Equal(4, sent[1].Seats);
        Assert.Equal("owner@south.example.com", sent[1].Email);
    }

    [Fact]
    public async Task Coming_back_from_checkout_changes_nothing_by_itself()
    {
        var admin = await AdminOfAsync("north");
        await ChangeTenantAsync("north", t => t.Plan = Plans.Free);

        await Json(await admin.PostAsync("/api/billing/checkout", null));

        Assert.Equal(Plans.Free, (string)(await Json(await admin.GetAsync("/api/billing")))["plan"]!);
    }

    [Fact]
    public async Task A_clinic_with_a_subscription_cannot_start_a_second_one()
    {
        var admin = await AdminOfAsync("north");
        await ChangeTenantAsync("north", t => (t.StripeCustomerId, t.StripeSubscriptionId) = ("cus_1", "sub_1"));

        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsync("/api/billing/checkout", null)).StatusCode);
        Assert.Empty(_factory.Billing.Checkouts);
    }

    [Fact]
    public async Task A_former_subscriber_comes_back_as_the_same_stripe_customer()
    {
        var admin = await AdminOfAsync("north");
        await ChangeTenantAsync("north", t => t.StripeCustomerId = "cus_1");

        await Json(await admin.PostAsync("/api/billing/checkout", null));

        Assert.Equal("cus_1", _factory.Billing.Checkouts.Single().CustomerId);
    }

    [Fact]
    public async Task The_portal_opens_for_the_clinics_own_customer_and_returns_to_its_own_page()
    {
        var north = await AdminOfAsync("north");
        var south = await AdminOfAsync("south");
        await ChangeTenantAsync("north", t => t.StripeCustomerId = "cus_north");

        Assert.Equal("https://stripe.test/portal/cus_north", (string)(await Json(await north.PostAsync("/api/billing/portal", null)))["url"]!);
        Assert.Equal(("cus_north", $"http://north.{HostedFactory.BaseDomain}/billing"), _factory.Billing.Portals.Single());

        // South has the same Ids and no customer: it gets nothing of north's.
        Assert.Equal(HttpStatusCode.Conflict, (await south.PostAsync("/api/billing/portal", null)).StatusCode);
        Assert.Single(_factory.Billing.Portals);
    }

    [Fact]
    public async Task Billing_is_for_administrators()
    {
        await _factory.AddTenantAsync("north");
        var typist = (await _factory.SignInAtAsync("north", "demo.typist")).Client;

        Assert.Equal(HttpStatusCode.Forbidden, (await typist.GetAsync("/api/billing")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await typist.PostAsync("/api/billing/checkout", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await typist.PostAsync("/api/billing/portal", null)).StatusCode);
    }

    [Fact]
    public async Task A_suspended_clinic_can_still_pay_but_change_nothing_else()
    {
        var admin = await AdminOfAsync("north", TenantStatus.Suspended);
        await ChangeTenantAsync("north", t => t.StripeCustomerId = "cus_1");

        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync("/api/billing/portal", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.PutAsJsonAsync("/api/clinic", new { name = "Renamed" })).StatusCode);
    }

    [Fact]
    public async Task The_billing_page_shows_usage_with_limits_on_free_and_without_on_pro()
    {
        var free = await AdminOfAsync("north");
        var pro = await AdminOfAsync("south");
        await ChangeTenantAsync("north", t => t.Plan = Plans.Free);
        await ChangeTenantAsync("south", t => t.Plan = Plans.Pro);

        var north = await Json(await free.GetAsync("/api/billing"));
        var south = await Json(await pro.GetAsync("/api/billing"));

        Assert.Equal(4, (int)north["users"]!["used"]!);
        Assert.Equal(PlanLimits.FreeUsers, (int)north["users"]!["limit"]!);
        Assert.Equal(2, (int)north["services"]!["used"]!);
        Assert.Equal(PlanLimits.FreeSignedReportsPerMonth, (int)north["signedReports"]!["limit"]!);
        Assert.True((bool)north["canUpgrade"]!);
        Assert.False((bool)north["canManage"]!);
        Assert.Equal("Pro", (string)south["plan"]!);
        Assert.Null(south["users"]!["limit"]);
    }

    [Fact]
    public async Task A_trial_shows_as_a_trial_until_it_ends()
    {
        var admin = await AdminOfAsync("north");
        await ChangeTenantAsync("north", t => t.TrialEndsAt = DateTime.UtcNow.AddDays(3));
        Assert.True((bool)(await Json(await admin.GetAsync("/api/billing")))["onTrial"]!);

        await ChangeTenantAsync("north", t => t.TrialEndsAt = DateTime.UtcNow.AddDays(-1));
        var ended = await Json(await admin.GetAsync("/api/billing"));
        Assert.False((bool)ended["onTrial"]!);
        Assert.Equal(Plans.Free, (string)ended["plan"]!);
    }

    [Fact]
    public async Task A_server_without_stripe_says_so()
    {
        await using var factory = new HostedFactory();
        await factory.AddTenantAsync("north");
        var admin = (await factory.SignInAtAsync("north")).Client;

        Assert.False((bool)(await Json(await admin.GetAsync("/api/billing")))["configured"]!);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await admin.PostAsync("/api/billing/checkout", null)).StatusCode);
    }

    [Fact]
    public void A_live_key_stops_the_start_up()
    {
        using var factory = new LiveKeyFactory();
        var ex = Assert.ThrowsAny<Exception>(() => factory.CreateClient());
        Assert.Contains("test key", ex.Message);
    }

    private sealed class LiveKeyFactory : HostedFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseSetting("Stripe:SecretKey", "sk_live_not_a_real_key");
        }
    }
}

/// <summary>The real gateway against a stand-in for Stripe's HTTP API: what goes over the wire, and failures.</summary>
public class StripeGatewayTests
{
    private sealed class FakeStripe(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public string? Path { get; private set; }
        public string? Form { get; private set; }
        public string? Authorization { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Path = request.RequestUri!.AbsolutePath;
            Authorization = request.Headers.Authorization?.Parameter;
            Form = Uri.UnescapeDataString(await request.Content!.ReadAsStringAsync(ct));
            return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }

    private static StripeBillingGateway Gateway(HttpMessageHandler stripe) => new(
        Options.Create(new StripeOptions { SecretKey = "sk_test_not_a_real_key", ProPriceId = "price_test_pro" }),
        NullLogger<StripeBillingGateway>.Instance, stripe);

    private static readonly CheckoutRequest Request = new("north", null, "owner@north.example.com", 4,
        "http://north.clinicreport.test/billing?checkout=success", "http://north.clinicreport.test/billing?checkout=cancelled");

    [Fact]
    public async Task Checkout_asks_stripe_for_a_subscription_for_the_tenant()
    {
        var stripe = new FakeStripe(HttpStatusCode.OK, """{ "id": "cs_test_1", "object": "checkout.session", "url": "https://checkout.stripe.test/cs_test_1" }""");

        var url = await Gateway(stripe).CreateCheckoutUrlAsync(Request);

        Assert.Equal("https://checkout.stripe.test/cs_test_1", url);
        Assert.Equal("/v1/checkout/sessions", stripe.Path);
        Assert.Equal("sk_test_not_a_real_key", stripe.Authorization);
        Assert.Contains("mode=subscription", stripe.Form);
        Assert.Contains("client_reference_id=north", stripe.Form);
        Assert.Contains("customer_email=owner@north.example.com", stripe.Form);
        Assert.Contains("line_items[0][price]=price_test_pro", stripe.Form);
        Assert.Contains("line_items[0][quantity]=4", stripe.Form);
        Assert.Contains("subscription_data[metadata][tenant]=north", stripe.Form);
    }

    [Fact]
    public async Task A_known_customer_is_not_sent_an_email_as_well()
    {
        var stripe = new FakeStripe(HttpStatusCode.OK, """{ "id": "cs_test_1", "object": "checkout.session", "url": "https://checkout.stripe.test/cs_test_1" }""");

        await Gateway(stripe).CreateCheckoutUrlAsync(Request with { CustomerId = "cus_1" });

        Assert.Contains("customer=cus_1", stripe.Form);
        Assert.DoesNotContain("customer_email", stripe.Form);
    }

    [Fact]
    public async Task The_portal_asks_stripe_for_the_customers_page()
    {
        var stripe = new FakeStripe(HttpStatusCode.OK, """{ "id": "bps_1", "object": "billing_portal.session", "url": "https://billing.stripe.test/bps_1" }""");

        var url = await Gateway(stripe).CreatePortalUrlAsync("cus_1", "http://north.clinicreport.test/billing");

        Assert.Equal("https://billing.stripe.test/bps_1", url);
        Assert.Equal("/v1/billing_portal/sessions", stripe.Path);
        Assert.Contains("customer=cus_1", stripe.Form);
    }

    [Fact]
    public async Task A_refusal_from_stripe_is_a_business_error_not_a_crash()
    {
        var stripe = new FakeStripe(HttpStatusCode.BadRequest, """{ "error": { "type": "invalid_request_error", "code": "resource_missing", "message": "No such price" } }""");

        var ex = await Assert.ThrowsAsync<ClinicReport.Api.Reports.ReportRuleException>(() => Gateway(stripe).CreateCheckoutUrlAsync(Request));

        Assert.Equal(502, ex.Status);
    }
}
