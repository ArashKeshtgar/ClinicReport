using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClinicReport.Api.Data;
using ClinicReport.Api.Endpoints;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace ClinicReport.Tests;

/// <summary>Fails saving the administrator of a sign-up, after the tenant's database has been created.</summary>
public sealed class FailingAdminInterceptor : SaveChangesInterceptor
{
    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        if (eventData.Context!.ChangeTracker.Entries<AppUser>().Any(e => e.State == EntityState.Added && e.Entity.UserName.StartsWith("fail@")))
            throw new IOException("Disk full (test).");
        return ValueTask.FromResult(result);
    }
}

public class TightSignupFactory : HostedFactory
{
    protected override int SignupsPerHour => 2;
}

/// <summary>Hosted mode: a clinic signs itself up on the root domain.</summary>
public class SignupTests : IAsyncLifetime
{
    private readonly HostedFactory _factory = new();

    public async Task InitializeAsync() => await _factory.AddTenantAsync("north");

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    private static object Request(string slug, string email = "owner@example.com", string password = "a long passphrase 42") =>
        new { clinicName = "Harbour Cardiology", slug, adminName = "Test Owner", email, password };

    private async Task<HttpResponseMessage> SignUpAsync(string slug, string email = "owner@example.com", HttpClient? client = null) =>
        await (client ?? _factory.RootClient()).PostAsJsonAsync("/api/signup", Request(slug, email));

    private async Task<Tenant?> TenantAsync(string slug)
    {
        using var scope = _factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<CatalogDbContext>().Tenants.AsNoTracking().SingleOrDefaultAsync(t => t.Slug == slug);
    }

    private async Task<(HttpClient Client, string Cookie)> LogInAsync(string slug, string email = "owner@example.com")
    {
        var client = _factory.ClientFor(slug, cookies: false);
        var res = await client.PostAsJsonAsync("/api/auth/login", new { userName = email, password = "a long passphrase 42" });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var cookie = res.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith("clinicreport.auth=")).Split(';')[0];
        client.DefaultRequestHeaders.Add("Cookie", cookie);
        return (client, cookie);
    }

    [Fact]
    public async Task A_clinic_signs_up_and_its_administrator_signs_in_at_its_address()
    {
        var res = await SignUpAsync("harbour");

        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<SignupEndpoints.SignupResult>();
        Assert.Equal($"http://harbour.{HostedFactory.BaseDomain}/", body!.Url);
        var tenant = await TenantAsync("harbour");
        Assert.Equal(TenantStatus.Trial, tenant!.Status);
        Assert.Equal(tenant.CreatedAt + SignupEndpoints.TrialLength, tenant.TrialEndsAt);
        Assert.True(File.Exists(_factory.DatabaseOf("harbour")));

        var (client, _) = await LogInAsync("harbour");
        var me = await client.GetFromJsonAsync<JsonElement>("/api/auth/me");
        Assert.Contains("Administrator", me.GetProperty("roles").EnumerateArray().Select(r => r.GetString()));
        Assert.True(me.GetProperty("permissions").GetArrayLength() > 0, "no pending action: the owner chose the password");
        var settings = await client.GetFromJsonAsync<JsonElement>("/api/settings");
        Assert.Equal("Harbour Cardiology", settings.GetProperty("name").GetString());
        // Seeded like any tenant: the bundled templates are there.
        Assert.NotEmpty(await client.GetFromJsonAsync<JsonElement[]>("/api/templates") ?? []);
    }

    [Fact]
    public async Task Two_signed_up_clinics_have_the_same_ids_and_keep_their_sessions_apart()
    {
        Assert.Equal(HttpStatusCode.Created, (await SignUpAsync("east")).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await SignUpAsync("west")).StatusCode);

        var (east, eastCookie) = await LogInAsync("east");
        var (west, _) = await LogInAsync("west");
        var eastMe = await east.GetFromJsonAsync<JsonElement>("/api/auth/me");
        var westMe = await west.GetFromJsonAsync<JsonElement>("/api/auth/me");
        Assert.Equal(eastMe.GetProperty("id").GetInt32(), westMe.GetProperty("id").GetInt32());

        var carried = _factory.ClientFor("west", cookies: false);
        carried.DefaultRequestHeaders.Add("Cookie", eastCookie);
        Assert.Equal(HttpStatusCode.Unauthorized, (await carried.GetAsync("/api/reports")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await carried.GetAsync("/api/auth/me")).StatusCode);
    }

    [Theory]
    [InlineData("north")]
    [InlineData("NORTH")]
    public async Task A_taken_address_is_refused(string slug)
    {
        Assert.Equal(HttpStatusCode.Conflict, (await SignUpAsync(slug)).StatusCode);
        var check = await _factory.RootClient().GetFromJsonAsync<SignupEndpoints.SlugCheck>("/api/signup/slug?value=north");
        Assert.False(check!.Available);
    }

    [Fact]
    public async Task A_database_file_without_a_tenant_is_never_handed_out()
    {
        await File.WriteAllTextAsync(_factory.DatabaseOf("orphan"), "somebody's data");

        Assert.Equal(HttpStatusCode.Conflict, (await SignUpAsync("orphan")).StatusCode);
        Assert.Equal("somebody's data", await File.ReadAllTextAsync(_factory.DatabaseOf("orphan")));
    }

    [Theory]
    [InlineData("admin")]
    [InlineData("www")]
    [InlineData("-north")]
    [InlineData("a--b")]
    [InlineData("x")]
    [InlineData("has space")]
    public async Task Reserved_and_malformed_addresses_are_refused(string slug)
    {
        var res = await SignUpAsync(slug);

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        var problem = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(problem.GetProperty("errors").TryGetProperty("slug", out _));
        Assert.False((await _factory.RootClient().GetFromJsonAsync<SignupEndpoints.SlugCheck>($"/api/signup/slug?value={Uri.EscapeDataString(slug)}"))!.Available);
    }

    [Fact]
    public async Task A_weak_password_or_bad_email_creates_nothing()
    {
        var root = _factory.RootClient();
        var res = await root.PostAsJsonAsync("/api/signup", new { clinicName = "Bay", slug = "bay", adminName = "Owner", email = "not-an-email", password = "short" });

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        var errors = (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors");
        Assert.True(errors.TryGetProperty("email", out _));
        Assert.True(errors.TryGetProperty("password", out _));
        Assert.Null(await TenantAsync("bay"));
        Assert.False(File.Exists(_factory.DatabaseOf("bay")));
    }

    [Fact]
    public async Task A_failure_half_way_leaves_nothing_and_the_address_can_be_used_again()
    {
        var failing = _factory.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.AddSingleton<IInterceptor>(new FailingAdminInterceptor())));
        var root = failing.CreateClient(new() { BaseAddress = new Uri($"http://{HostedFactory.BaseDomain}") });
        root.DefaultRequestHeaders.Add("X-ClinicReport", "1");

        var res = await SignUpAsync("broken", email: "fail@example.com", client: root);

        Assert.Equal(HttpStatusCode.InternalServerError, res.StatusCode);
        Assert.Null(await TenantAsync("broken"));
        Assert.False(File.Exists(_factory.DatabaseOf("broken")));
        Assert.Equal(HttpStatusCode.NotFound, (await _factory.ClientFor("broken").GetAsync("/api/settings")).StatusCode);

        Assert.Equal(HttpStatusCode.Created, (await SignUpAsync("broken")).StatusCode);
        await LogInAsync("broken");
    }

    [Fact]
    public async Task Sign_up_is_only_on_the_root_domain_and_the_root_domain_has_no_tenant_api()
    {
        Assert.Equal(HttpStatusCode.NotFound, (await SignUpAsync("harbour", client: _factory.ClientFor("north"))).StatusCode);
        Assert.Null(await TenantAsync("harbour"));

        var root = _factory.RootClient();
        var settings = await root.GetFromJsonAsync<JsonElement>("/api/settings");
        Assert.True(settings.GetProperty("signup").GetBoolean());
        Assert.Equal(HostedFactory.BaseDomain, settings.GetProperty("baseDomain").GetString());
        foreach (var path in new[] { "/api/auth/me", "/api/reports", "/api/templates", "/hubs/report/negotiate" })
            Assert.Equal(HttpStatusCode.NotFound, (await root.GetAsync(path)).StatusCode);
    }

    [Fact]
    public async Task Sign_ups_are_rate_limited()
    {
        await using var factory = new TightSignupFactory();
        var root = factory.RootClient();

        for (var i = 0; i < 2; i++)
            Assert.Equal(HttpStatusCode.BadRequest, (await root.PostAsJsonAsync("/api/signup", Request("admin"))).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await root.PostAsJsonAsync("/api/signup", Request("admin"))).StatusCode);
    }

    private async Task<HttpStatusCode> StartReportAsync(HttpClient client) =>
        (await client.PostAsJsonAsync("/api/reports", new { patientId = 0, templateId = 0, examDate = "2026-11-02" })).StatusCode;

    private async Task<bool> AwaitingAsync(string slug) =>
        (await _factory.ClientFor(slug).GetFromJsonAsync<JsonElement>("/api/settings")).GetProperty("awaitingEmailVerification").GetBoolean();

    [Fact]
    public async Task No_report_starts_until_the_owner_confirms_the_email_with_the_link()
    {
        var res = await SignUpAsync("harbour");
        Assert.True((await res.Content.ReadFromJsonAsync<SignupEndpoints.SignupResult>())!.EmailSent);
        var mail = Assert.Single(_factory.Mail.Sent);
        Assert.Equal("owner@example.com", mail.To);
        Assert.Contains($"http://harbour.{HostedFactory.BaseDomain}/verify-email?token=", mail.Body);

        var (client, _) = await LogInAsync("harbour");
        Assert.True(await AwaitingAsync("harbour"));
        var refused = await client.PostAsJsonAsync("/api/reports", new { patientId = 0, templateId = 0, examDate = "2026-11-02" });
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Equal("verify-email", (await refused.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());

        // The link works without signing in, at the clinic's address.
        var anonymous = _factory.ClientFor("harbour");
        Assert.Equal(HttpStatusCode.OK, (await anonymous.PostAsJsonAsync("/api/auth/verify-email", new { token = _factory.Mail.TokenFor("owner@example.com") })).StatusCode);

        Assert.False(await AwaitingAsync("harbour"));
        Assert.NotEqual(HttpStatusCode.Forbidden, await StartReportAsync(client));
        var tenant = await TenantAsync("harbour");
        Assert.NotNull(tenant!.EmailVerifiedAt);
        Assert.Null(tenant.EmailTokenHash);
    }

    [Fact]
    public async Task A_wrong_token_or_another_clinics_token_confirms_nothing()
    {
        await SignUpAsync("east", email: "east@example.com");
        await SignUpAsync("west", email: "west@example.com");
        var eastToken = _factory.Mail.TokenFor("east@example.com");

        foreach (var token in new[] { eastToken, "made-up", "" })
            Assert.Equal(HttpStatusCode.BadRequest,
                (await _factory.ClientFor("west").PostAsJsonAsync("/api/auth/verify-email", new { token })).StatusCode);

        Assert.True(await AwaitingAsync("west"));
        Assert.True(await AwaitingAsync("east"));
        // The tenant's token is only kept as its hash.
        Assert.DoesNotContain(eastToken, (await TenantAsync("east"))!.EmailTokenHash);
    }

    [Fact]
    public async Task When_the_mail_fails_the_clinic_is_still_created_and_the_administrator_sends_a_new_link()
    {
        _factory.Mail.Fail = true;
        var res = await SignUpAsync("harbour");
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        Assert.False((await res.Content.ReadFromJsonAsync<SignupEndpoints.SignupResult>())!.EmailSent);
        var firstHash = (await TenantAsync("harbour"))!.EmailTokenHash;

        _factory.Mail.Fail = false;
        var (client, _) = await LogInAsync("harbour");
        var resend = await client.PostAsync("/api/auth/verify-email/resend", null);
        Assert.Equal(HttpStatusCode.OK, resend.StatusCode);
        Assert.True((await resend.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("sent").GetBoolean());
        Assert.NotEqual(firstHash, (await TenantAsync("harbour"))!.EmailTokenHash);

        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/auth/verify-email",
            new { token = _factory.Mail.TokenFor("owner@example.com") })).StatusCode);
        Assert.False(await AwaitingAsync("harbour"));
    }

    [Fact]
    public async Task A_new_link_makes_the_old_one_worthless_and_only_administrators_ask_for_one()
    {
        await SignUpAsync("harbour");
        var oldToken = _factory.Mail.TokenFor("owner@example.com");
        Assert.Equal(HttpStatusCode.Unauthorized, (await _factory.ClientFor("harbour").PostAsync("/api/auth/verify-email/resend", null)).StatusCode);
        // Demo data is seeded in tests: a typist of the same clinic may not ask either.
        var (typist, _) = await _factory.SignInAtAsync("harbour", "demo.typist");
        Assert.Equal(HttpStatusCode.Forbidden, (await typist.PostAsync("/api/auth/verify-email/resend", null)).StatusCode);

        var (admin, _) = await LogInAsync("harbour");
        await admin.PostAsync("/api/auth/verify-email/resend", null);

        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/auth/verify-email", new { token = oldToken })).StatusCode);
        Assert.True(await AwaitingAsync("harbour"));
    }

    [Fact]
    public async Task Tenants_the_operator_added_have_no_email_to_confirm()
    {
        var (client, _) = await _factory.SignInAtAsync("north");
        Assert.False(await AwaitingAsync("north"));
        Assert.NotEqual(HttpStatusCode.Forbidden, await StartReportAsync(client));
    }
}
