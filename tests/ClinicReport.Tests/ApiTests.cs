using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;

namespace ClinicReport.Tests;

/// <summary>A fresh SQLite database with the demo data for each test class.</summary>
public class ApiFactory : WebApplicationFactory<Program>
{
    protected virtual bool LockAccounts => false;

    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"clinicreport-test-{Guid.NewGuid():N}.db");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:ClinicReport", $"Data Source={_dbPath}");
        builder.UseSetting("Demo:Enabled", "true");
        builder.UseSetting("RateLimits:LoginsPerMinute", "1000");
        builder.UseSetting("Demo:LockAccounts", LockAccounts ? "true" : "false");
    }

    public async Task<HttpClient> SignInAsync(string demoUser)
    {
        var client = CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        client.DefaultRequestHeaders.Add("X-ClinicReport", "1");
        var res = await client.PostAsJsonAsync("/api/auth/demo-login", new { userName = demoUser });
        res.EnsureSuccessStatusCode();
        return client;
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        SqliteConnection.ClearAllPools();
        File.Delete(_dbPath);
    }
}

public class ApiTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private static async Task<JsonNode> Json(HttpResponseMessage res)
    {
        var body = await res.Content.ReadAsStringAsync();
        Assert.True(res.IsSuccessStatusCode, $"{(int)res.StatusCode}: {body}");
        return JsonNode.Parse(body)!;
    }

    private async Task<int> NewEchoReportAsync(HttpClient client)
    {
        var templates = (await client.GetFromJsonAsync<JsonArray>("/api/templates"))!;
        var echoId = templates.Single(t => (string)t!["key"]! == "echo-tte")!["id"]!.GetValue<int>();
        var patients = (await client.GetFromJsonAsync<JsonArray>("/api/patients?q=DEMO-0003"))!;
        var res = await client.PostAsJsonAsync("/api/reports", new
        {
            patientId = patients[0]!["id"]!.GetValue<int>(), templateId = echoId, examDate = "2026-10-09",
            heightCm = 165, weightKg = 60, indication = "Palpitations",
        });
        return (await Json(res))["id"]!.GetValue<int>();
    }

    private static Task<HttpResponseMessage> Patch(HttpClient client, int id, object values) =>
        client.PatchAsJsonAsync($"/api/reports/{id}/values", new { values });

    [Fact]
    public async Task Api_requires_sign_in()
    {
        var res = await factory.CreateClient().GetAsync("/api/reports");
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task Unsafe_requests_need_the_csrf_header()
    {
        var client = await factory.SignInAsync("demo.typist");
        client.DefaultRequestHeaders.Remove("X-ClinicReport");
        var res = await client.PostAsJsonAsync("/api/patients", new { });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task Typist_fills_a_report_and_sees_the_narrative()
    {
        var client = await factory.SignInAsync("demo.typist");
        var id = await NewEchoReportAsync(client);

        var report = await Json(await Patch(client, id, new { lv_size = "normal", lv_function = "mild", lvef = "47", mv_morph = "abnormal", mv_findings = new[] { "calcified", "thickened" } }));

        var lv = report["rendered"]!["sections"]!.AsArray().Single(s => (string)s!["id"]! == "lv")!["sentences"]!.AsArray().Select(s => (string)s!).ToList();
        Assert.Equal(["The left ventricle is normal in size.", "Left ventricular systolic function is mildly reduced.", "The ejection fraction is estimated at 47%."], lv);
        // Typed "47" is stored as a number; multi answers are stored in template order.
        Assert.Equal(47, report["values"]!["lvef"]!.GetValue<double>());
        Assert.Equal(["thickened", "calcified"], report["values"]!["mv_findings"]!.AsArray().Select(v => (string)v!));
        Assert.Equal(1.66, report["rendered"]!["computed"]!["bsa"]!.GetValue<double>(), 2);
    }

    [Fact]
    public async Task Answers_must_fit_the_template()
    {
        var client = await factory.SignInAsync("demo.typist");
        var id = await NewEchoReportAsync(client);

        Assert.Equal(HttpStatusCode.BadRequest, (await Patch(client, id, new { lv_size = "enormous" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Patch(client, id, new { nope = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Patch(client, id, new { lvef = "abc" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Patch(client, id, new { bsa = 2 })).StatusCode);
    }

    [Fact]
    public async Task Parallel_edits_to_different_fields_are_all_kept()
    {
        var typist = await factory.SignInAsync("demo.typist");
        var physician = await factory.SignInAsync("demo.physician");
        var id = await NewEchoReportAsync(typist);

        var fields = new[] { "ivsd", "lvidd", "lvpwd", "lvids", "la_volume", "ao_root", "rv_basal", "tapse", "e_wave", "a_wave" };
        await Task.WhenAll(fields.Select((f, i) =>
            Patch(i % 2 == 0 ? typist : physician, id, new Dictionary<string, object> { [f] = 10 + i })));

        var report = await Json(await typist.GetAsync($"/api/reports/{id}"));
        foreach (var (f, i) in fields.Select((f, i) => (f, i)))
            Assert.Equal(10 + i, report["values"]![f]!.GetValue<double>());
        Assert.Equal(1 + fields.Length, report["revision"]!.GetValue<int>());
    }

    [Fact]
    public async Task Only_a_physician_signs_and_signed_reports_are_frozen()
    {
        var typist = await factory.SignInAsync("demo.typist");
        var physician = await factory.SignInAsync("demo.physician");
        var id = await NewEchoReportAsync(typist);

        foreach (var section in new[] { "study", "lv", "rv", "mitral", "aortic", "tricuspid", "pericardium" })
            (await typist.PostAsync($"/api/reports/{id}/presets/{section}", null)).EnsureSuccessStatusCode();

        Assert.Equal(HttpStatusCode.Forbidden, (await typist.PostAsync($"/api/reports/{id}/sign", null)).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await physician.PostAsync($"/api/reports/{id}/sign", null)).StatusCode);

        (await physician.PutAsJsonAsync($"/api/reports/{id}/conclusion", new { conclusion = "Normal study." })).EnsureSuccessStatusCode();
        var signed = await Json(await physician.PostAsync($"/api/reports/{id}/sign", null));
        Assert.Equal("Final", (string)signed["status"]!);
        Assert.Equal("Dr. Avery Morgan", (string)signed["signedBy"]!);

        Assert.Equal(HttpStatusCode.Conflict, (await Patch(typist, id, new { lv_size = "mild" })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await typist.PutAsJsonAsync($"/api/reports/{id}/conclusion", new { conclusion = "x" })).StatusCode);

        var withAddendum = await Json(await physician.PostAsJsonAsync($"/api/reports/{id}/addenda", new { text = "Images reviewed again; no change." }));
        Assert.Single(withAddendum["addenda"]!.AsArray());

        var history = (await typist.GetFromJsonAsync<JsonArray>($"/api/reports/{id}/history"))!;
        Assert.Contains(history, h => (string)h!["field"]! == "$status" && (string)h["user"]! == "Dr. Avery Morgan");
    }

    [Fact]
    public async Task Required_fields_block_signing()
    {
        var typist = await factory.SignInAsync("demo.typist");
        var physician = await factory.SignInAsync("demo.physician");
        var id = await NewEchoReportAsync(typist);
        (await physician.PutAsJsonAsync($"/api/reports/{id}/conclusion", new { conclusion = "Something." })).EnsureSuccessStatusCode();

        var res = await physician.PostAsync($"/api/reports/{id}/sign", null);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, res.StatusCode);
        Assert.Contains("Image quality", await res.Content.ReadAsStringAsync());
    }

    private const string TinyTemplate = """
        { "key": "holter-24h", "name": "24-hour Holter", "service": "Holter",
          "sections": [ { "id": "summary", "title": "Summary", "fields": [
            { "id": "rhythm", "label": "Predominant rhythm", "type": "choice",
              "options": [ { "value": "sinus", "label": "Sinus", "phrase": "Predominant rhythm is sinus." } ] } ] } ] }
        """;

    [Fact]
    public async Task Admin_designs_and_publishes_a_new_service_template()
    {
        var admin = await factory.SignInAsync("demo.admin");
        var typist = await factory.SignInAsync("demo.typist");

        Assert.Equal(HttpStatusCode.Forbidden, (await typist.PostAsJsonAsync("/api/templates", new { definitionJson = TinyTemplate })).StatusCode);

        var created = await Json(await admin.PostAsJsonAsync("/api/templates", new { definitionJson = TinyTemplate }));
        var id = created["template"]!["id"]!.GetValue<int>();
        Assert.Empty(created["errors"]!.AsArray());

        // Not visible to typists until published.
        Assert.DoesNotContain((await typist.GetFromJsonAsync<JsonArray>("/api/templates"))!, t => (string)t!["key"]! == "holter-24h");

        // A broken draft can be saved but not published.
        var broken = TinyTemplate.Replace("\"phrase\": \"Predominant rhythm is sinus.\"", "\"phrase\": \"{oops}\"");
        var saved = await Json(await admin.PutAsJsonAsync($"/api/templates/{id}/draft", new { definitionJson = broken }));
        Assert.NotEmpty(saved["errors"]!.AsArray());
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await admin.PostAsync($"/api/templates/{id}/publish", null)).StatusCode);

        // A misspelt property is rejected outright instead of being silently dropped.
        var typo = TinyTemplate.Replace("\"phrase\":", "\"phrse\":");
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync($"/api/templates/{id}/draft", new { definitionJson = typo })).StatusCode);

        await Json(await admin.PutAsJsonAsync($"/api/templates/{id}/draft", new { definitionJson = TinyTemplate }));
        var published = await Json(await admin.PostAsync($"/api/templates/{id}/publish", null));
        Assert.NotNull(published["publishedVersionId"]);
        Assert.Null(published["draftVersionId"]);

        Assert.Contains((await typist.GetFromJsonAsync<JsonArray>("/api/templates"))!, t => (string)t!["key"]! == "holter-24h");

        var preview = await Json(await admin.PostAsJsonAsync("/api/templates/preview", new { definitionJson = TinyTemplate, values = new { rhythm = "sinus" } }));
        Assert.Equal("Predominant rhythm is sinus.", (string)preview["rendered"]!["sections"]![0]!["sentences"]![0]!);
    }

    [Fact]
    public async Task Admin_creates_a_user_who_signs_in_with_a_password()
    {
        var admin = await factory.SignInAsync("demo.admin");
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/users", new
        {
            userName = "short.pw", displayName = "Short", role = "Typist", password = "123",
        })).StatusCode);

        await Json(await admin.PostAsJsonAsync("/api/users", new
        {
            userName = "jamie.typist", displayName = "Jamie", role = "Typist", password = "correct-horse-battery",
        }));

        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        client.DefaultRequestHeaders.Add("X-ClinicReport", "1");
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await client.PostAsJsonAsync("/api/auth/login", new { userName = "jamie.typist", password = "wrong-password!!" })).StatusCode);
        var me = await Json(await client.PostAsJsonAsync("/api/auth/login", new { userName = "JAMIE.typist", password = "correct-horse-battery" }));
        Assert.Equal("Typist", (string)me["role"]!);

        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/users")).StatusCode);
    }

    [Fact]
    public async Task Deactivated_user_loses_access_immediately()
    {
        var admin = await factory.SignInAsync("demo.admin");
        var created = await Json(await admin.PostAsJsonAsync("/api/users", new
        {
            userName = "temp.user", displayName = "Temp", role = "Typist", password = "temporary-password",
        }));
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        client.DefaultRequestHeaders.Add("X-ClinicReport", "1");
        await Json(await client.PostAsJsonAsync("/api/auth/login", new { userName = "temp.user", password = "temporary-password" }));
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/reports")).StatusCode);

        await Json(await admin.PutAsJsonAsync($"/api/users/{created["id"]}", new { displayName = "Temp", role = "Typist", isActive = false }));
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/reports")).StatusCode);
    }
}

/// <summary>The public demo as deployed: accounts locked.</summary>
public sealed class LockedDemoFactory : ApiFactory
{
    protected override bool LockAccounts => true;
}

public class PublicDemoTests(LockedDemoFactory factory) : IClassFixture<LockedDemoFactory>
{
    [Fact]
    public async Task Demo_visitors_cannot_create_accounts_or_change_passwords()
    {
        var admin = await factory.SignInAsync("demo.admin");
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/api/users")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.PostAsJsonAsync("/api/users", new
        {
            userName = "intruder", displayName = "X", role = "Admin", password = "long-enough-password",
        })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.PostAsJsonAsync("/api/auth/password", new
        {
            currentPassword = "x", newPassword = "long-enough-password",
        })).StatusCode);
    }
}
