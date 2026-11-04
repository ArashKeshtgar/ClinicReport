using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using ClinicReport.Api.Auth;
using ClinicReport.Api.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ClinicReport.Tests;

/// <summary>Hosted mode: what a clinic's plan lets it add, sign and publish. Demo tenants share their Ids and start with 4 users.</summary>
public class PlanLimitsTests : IAsyncLifetime
{
    private const string Password = "correct-horse-battery";

    private readonly HostedFactory _factory = new();

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    private async Task SetPlanAsync(string slug, string plan, DateTime? trialEndsAt = null)
    {
        using var scope = _factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<CatalogDbContext>().Tenants.Where(t => t.Slug == slug)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.Plan, plan).SetProperty(t => t.TrialEndsAt, trialEndsAt));
        scope.ServiceProvider.GetRequiredService<TenantDirectory>().Forget(slug);
    }

    private async Task<HttpClient> AdminOfAsync(string slug, string plan)
    {
        await _factory.AddTenantAsync(slug);
        await SetPlanAsync(slug, plan);
        return (await _factory.SignInAtAsync(slug)).Client;
    }

    private static async Task<JsonNode> Json(HttpResponseMessage res)
    {
        var body = await res.Content.ReadAsStringAsync();
        Assert.True(res.IsSuccessStatusCode, $"{(int)res.StatusCode}: {body}");
        return JsonNode.Parse(body)!;
    }

    private static async Task AssertPaymentRequiredAsync(HttpResponseMessage res)
    {
        Assert.Equal(HttpStatusCode.PaymentRequired, res.StatusCode);
        Assert.Contains("Free plan", (string)JsonNode.Parse(await res.Content.ReadAsStringAsync())!["title"]!);
    }

    private static async Task<HttpResponseMessage> AddUserAsync(HttpClient admin, string userName)
    {
        var roles = (await admin.GetFromJsonAsync<JsonArray>("/api/roles"))!;
        var role = roles.Single(r => (string)r!["name"]! == SystemRoles.Typist)!["id"]!.GetValue<int>();
        return await admin.PostAsJsonAsync("/api/users", new { userName, displayName = userName, roleIds = new[] { role }, password = Password });
    }

    private static async Task<HttpResponseMessage> SetActiveAsync(HttpClient admin, string userName, bool isActive)
    {
        var user = (await admin.GetFromJsonAsync<JsonArray>("/api/users"))!.Single(u => (string)u!["userName"]! == userName)!;
        return await admin.PutAsJsonAsync($"/api/users/{user["id"]}", new
        {
            displayName = (string)user["displayName"]!,
            roleIds = user["roleIds"]!.AsArray().Select(r => r!.GetValue<int>()).ToArray(),
            isActive,
        });
    }

    private static string Template(string key, string service) => $$"""
        { "key": "{{key}}", "name": "Test {{key}}", "service": "{{service}}",
          "sections": [ { "id": "summary", "title": "Summary", "fields": [
            { "id": "rhythm", "label": "Predominant rhythm", "type": "choice",
              "options": [ { "value": "sinus", "label": "Sinus", "phrase": "Predominant rhythm is sinus." } ] } ] } ] }
        """;

    private static async Task<int> DraftTemplateAsync(HttpClient admin, string key, string service) =>
        (await Json(await admin.PostAsJsonAsync("/api/templates", new { definitionJson = Template(key, service) })))["template"]!["id"]!.GetValue<int>();

    /// <summary>An echo report with everything filled in, so that only the plan can stand in the way of signing.</summary>
    private async Task<(HttpClient Physician, int Id)> ReportReadyToSignAsync(string slug)
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
        return (physician, id);
    }

    /// <summary>Tops the clinic up to the Free plan's reports for this month.</summary>
    private async Task SignUpToTheLimitAsync(string slug)
    {
        await using var db = _factory.Services.GetRequiredService<ClinicReportDatabases>().Create(slug);
        var now = DateTime.UtcNow;
        var monthStart = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        var signed = await db.Reports.CountAsync(r => r.SignedAt >= monthStart);
        var patient = await db.Patients.FirstAsync();
        var version = await db.TemplateVersions.FirstAsync(v => v.Status == TemplateStatus.Published);
        var physician = await db.Users.SingleAsync(u => u.UserName == "demo.physician");
        for (var i = signed; i < PlanLimits.FreeSignedReportsPerMonth; i++)
            db.Reports.Add(new Report
            {
                PatientId = patient.Id, TemplateVersionId = version.Id, Status = ReportStatus.Final, ExamDate = DateOnly.FromDateTime(now),
                CreatedByUserId = physician.Id, CreatedAt = now, UpdatedAt = now, SignedByUserId = physician.Id, SignedAt = now,
            });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task A_free_clinic_stops_at_three_active_users_and_a_comeback_counts_as_one()
    {
        var admin = await AdminOfAsync("north", Plans.Free);

        Assert.Equal(HttpStatusCode.OK, (await SetActiveAsync(admin, "demo.echo", false)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SetActiveAsync(admin, "demo.typist", false)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await AddUserAsync(admin, "third.person")).StatusCode);

        await AssertPaymentRequiredAsync(await AddUserAsync(admin, "fourth.person"));
        await AssertPaymentRequiredAsync(await SetActiveAsync(admin, "demo.typist", true));

        await SetPlanAsync("north", Plans.Pro);
        Assert.Equal(HttpStatusCode.Created, (await AddUserAsync(admin, "fourth.person")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SetActiveAsync(admin, "demo.typist", true)).StatusCode);
    }

    [Fact]
    public async Task A_downgrade_locks_nobody_out()
    {
        var admin = await AdminOfAsync("north", Plans.Pro);
        var (physician, id) = await ReportReadyToSignAsync("north");

        await SetPlanAsync("north", Plans.Free);

        // Over the limit with 4 users: no fifth, but the four carry on, new sign-ins included.
        await AssertPaymentRequiredAsync(await AddUserAsync(admin, "fifth.person"));
        Assert.Equal(HttpStatusCode.OK, (await SetActiveAsync(admin, "demo.echo", true)).StatusCode);
        var typist = (await _factory.SignInAtAsync("north", "demo.typist")).Client;
        Assert.Equal(HttpStatusCode.OK, (await typist.GetAsync("/api/reports")).StatusCode);
        Assert.Equal("Final", (string)(await Json(await physician.PostAsync($"/api/reports/{id}/sign", null)))["status"]!);
    }

    [Fact]
    public async Task A_free_clinic_signs_fifty_reports_a_month_counted_per_clinic()
    {
        await AdminOfAsync("north", Plans.Free);
        await AdminOfAsync("south", Plans.Free);
        var (north, northId) = await ReportReadyToSignAsync("north");
        var (south, southId) = await ReportReadyToSignAsync("south");
        Assert.Equal(northId, southId);
        await SignUpToTheLimitAsync("north");

        await AssertPaymentRequiredAsync(await north.PostAsync($"/api/reports/{northId}/sign", null));
        Assert.Equal("Draft", (string)(await Json(await north.GetAsync($"/api/reports/{northId}")))["status"]!);

        // The same report Id at the other clinic, whose count is its own.
        Assert.Equal("Final", (string)(await Json(await south.PostAsync($"/api/reports/{southId}/sign", null)))["status"]!);

        // Last month's reports don't count.
        await using (var db = _factory.Services.GetRequiredService<ClinicReportDatabases>().Create("north"))
            await db.Reports.Where(r => r.SignedAt != null).ExecuteUpdateAsync(s => s.SetProperty(r => r.SignedAt, r => r.SignedAt!.Value.AddMonths(-2)));
        Assert.Equal("Final", (string)(await Json(await north.PostAsync($"/api/reports/{northId}/sign", null)))["status"]!);
    }

    [Fact]
    public async Task A_free_clinic_stays_with_two_services_but_keeps_its_templates_up_to_date()
    {
        // The bundled templates are the two services: echocardiography and ECG.
        var north = await AdminOfAsync("north", Plans.Free);
        var south = await AdminOfAsync("south", Plans.Pro);
        var northHolter = await DraftTemplateAsync(north, "holter-24h", "Holter");
        var southHolter = await DraftTemplateAsync(south, "holter-24h", "Holter");
        Assert.Equal(northHolter, southHolter);

        await AssertPaymentRequiredAsync(await north.PostAsync($"/api/templates/{northHolter}/publish", null));
        Assert.Equal(HttpStatusCode.OK, (await south.PostAsync($"/api/templates/{southHolter}/publish", null)).StatusCode);

        var ecgShort = await DraftTemplateAsync(north, "ecg-short", "electrocardiography");
        Assert.Equal(HttpStatusCode.OK, (await north.PostAsync($"/api/templates/{ecgShort}/publish", null)).StatusCode);

        // Dropping a service makes room for another; bringing it back is then a third.
        var templates = (await north.GetFromJsonAsync<JsonArray>("/api/templates"))!;
        var ecgs = templates.Where(t => ((string)t!["service"]!).Equals("Electrocardiography", StringComparison.OrdinalIgnoreCase)).Select(t => (int)t!["id"]!).ToList();
        foreach (var ecg in ecgs)
            Assert.Equal(HttpStatusCode.NoContent, (await north.PutAsJsonAsync($"/api/templates/{ecg}/active", new { isActive = false })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await north.PostAsync($"/api/templates/{northHolter}/publish", null)).StatusCode);
        await AssertPaymentRequiredAsync(await north.PutAsJsonAsync($"/api/templates/{ecgs[0]}/active", new { isActive = true }));
    }

    [Fact]
    public async Task A_trial_is_pro_until_it_ends()
    {
        var admin = await AdminOfAsync("north", Plans.Trial);
        await SetPlanAsync("north", Plans.Trial, DateTime.UtcNow.AddDays(1));
        Assert.Equal(HttpStatusCode.Created, (await AddUserAsync(admin, "during.trial")).StatusCode);

        await SetPlanAsync("north", Plans.Trial, DateTime.UtcNow.AddDays(-1));
        await AssertPaymentRequiredAsync(await AddUserAsync(admin, "after.trial"));
    }

    [Theory]
    [InlineData(Plans.Pro, null, Plans.Pro)]
    [InlineData(Plans.Pro, -1, Plans.Pro)]
    [InlineData(Plans.Free, 1, Plans.Free)]
    [InlineData(Plans.Trial, 1, Plans.Pro)]
    [InlineData(Plans.Trial, -1, Plans.Free)]
    [InlineData(Plans.Trial, null, Plans.Pro)]
    [InlineData("Unknown", null, Plans.Free)]
    public void The_plan_that_applies(string plan, int? trialDaysLeft, string effective)
    {
        var now = new DateTime(2026, 11, 4, 12, 0, 0, DateTimeKind.Utc);
        var tenant = new Tenant { Plan = plan, TrialEndsAt = trialDaysLeft is { } days ? now.AddDays(days) : null };
        Assert.Equal(effective, tenant.EffectivePlan(now));
    }

    [Fact]
    public async Task Outside_hosted_mode_nothing_is_limited()
    {
        await using var standalone = new ApiFactory();
        var admin = await standalone.SignInAsync("demo.admin");
        Assert.Equal(HttpStatusCode.Created, (await AddUserAsync(admin, "fifth.person")).StatusCode);
    }
}
