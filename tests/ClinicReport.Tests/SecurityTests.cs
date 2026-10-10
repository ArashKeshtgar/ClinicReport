using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using ClinicReport.Api.Auth;
using ClinicReport.Api.Endpoints;
using Microsoft.AspNetCore.Mvc.Testing;

namespace ClinicReport.Tests;

/// <summary>Users, roles, permissions, service scoping, lockout, two-factor and the audit log.</summary>
public class SecurityTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private const string Password = "correct-horse-battery";
    private const string NewPassword = "another-long-passphrase";

    private static async Task<JsonNode> Json(HttpResponseMessage res)
    {
        var body = await res.Content.ReadAsStringAsync();
        Assert.True(res.IsSuccessStatusCode, $"{(int)res.StatusCode}: {body}");
        return JsonNode.Parse(body)!;
    }

    private HttpClient Anonymous()
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        client.DefaultRequestHeaders.Add("X-ClinicReport", "1");
        return client;
    }

    private static Task<HttpResponseMessage> Login(HttpClient c, string user, string password, string? code = null) =>
        c.PostAsJsonAsync("/api/auth/login", new { userName = user, password, code });

    private static async Task<int> RoleId(HttpClient admin, string name) =>
        (await admin.GetFromJsonAsync<JsonArray>("/api/roles"))!.Single(r => (string)r!["name"]! == name)!["id"]!.GetValue<int>();

    private async Task<(HttpClient Admin, int UserId)> CreateUser(string userName, string role, string[]? services = null)
    {
        var admin = await factory.SignInAsync("demo.admin");
        var created = await Json(await admin.PostAsJsonAsync("/api/users", new
        {
            userName, displayName = userName, roleIds = new[] { await RoleId(admin, role) }, services, password = Password,
        }));
        return (admin, created["id"]!.GetValue<int>());
    }

    /// <summary>Creates a user and signs them in with their own password already changed.</summary>
    private async Task<HttpClient> ReadyUser(string userName, string role, string[]? services = null)
    {
        await CreateUser(userName, role, services);
        var c = Anonymous();
        await Json(await Login(c, userName, Password));
        await Json(await c.PostAsJsonAsync("/api/auth/password", new { currentPassword = Password, newPassword = NewPassword }));
        return c;
    }

    [Fact]
    public async Task New_user_must_choose_a_password_before_anything_else()
    {
        await CreateUser("first.login", "Typist");
        var c = Anonymous();

        var me = await Json(await Login(c, "FIRST.login", Password));
        Assert.Equal("change-password", (string)me["pendingAction"]!);
        Assert.Equal(HttpStatusCode.Forbidden, (await c.GetAsync("/api/reports")).StatusCode);

        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync("/api/auth/password",
            new { currentPassword = Password, newPassword = "first.login-123456" })).StatusCode); // contains the user name
        me = await Json(await c.PostAsJsonAsync("/api/auth/password", new { currentPassword = Password, newPassword = NewPassword }));
        Assert.Null(me["pendingAction"]);
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/api/reports")).StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await Login(Anonymous(), "first.login", Password)).StatusCode);
    }

    [Fact]
    public async Task Five_wrong_passwords_lock_the_account_until_an_admin_unlocks_it()
    {
        var (admin, id) = await CreateUser("locked.out", "Typist");
        var c = Anonymous();
        for (var i = 0; i < AuthEndpoints.MaxFailedLogins; i++)
            Assert.Equal(HttpStatusCode.Unauthorized, (await Login(c, "locked.out", "wrong-password!!")).StatusCode);

        Assert.Equal(HttpStatusCode.Locked, (await Login(c, "locked.out", Password)).StatusCode);
        var row = (await admin.GetFromJsonAsync<JsonArray>("/api/users"))!.Single(u => (int)u!["id"]! == id)!;
        Assert.True((bool)row["isLockedOut"]!);

        (await admin.PostAsync($"/api/users/{id}/unlock", null)).EnsureSuccessStatusCode();
        await Json(await Login(c, "locked.out", Password));

        var audit = (await admin.GetFromJsonAsync<JsonArray>($"/api/audit?userId={id}"))!.Select(e => (string)e!["type"]!).ToList();
        Assert.Contains("LockedOut", audit);
        Assert.Contains("UserUnlocked", audit);
    }

    [Fact]
    public async Task Deactivating_a_user_ends_their_session_at_once()
    {
        var (admin, id) = await CreateUser("temp.user", "Typist");
        var c = Anonymous();
        await Json(await Login(c, "temp.user", Password));
        await Json(await c.PostAsJsonAsync("/api/auth/password", new { currentPassword = Password, newPassword = NewPassword }));
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/api/reports")).StatusCode);

        await Json(await admin.PutAsJsonAsync($"/api/users/{id}", new
        {
            displayName = "Temp", roleIds = new[] { await RoleId(admin, "Typist") }, isActive = false,
        }));
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.GetAsync("/api/reports")).StatusCode);
    }

    [Fact]
    public async Task Admin_password_reset_signs_the_user_out_everywhere()
    {
        var c = await ReadyUser("reset.me", "Typist");
        var admin = await factory.SignInAsync("demo.admin");
        var id = (await admin.GetFromJsonAsync<JsonArray>("/api/users"))!.Single(u => (string)u!["userName"]! == "reset.me")!["id"]!;

        (await admin.PostAsJsonAsync($"/api/users/{id}/password", new { password = "temporary-pass-42" })).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.GetAsync("/api/reports")).StatusCode);

        var me = await Json(await Login(Anonymous(), "reset.me", "temporary-pass-42"));
        Assert.Equal("change-password", (string)me["pendingAction"]!);
    }

    [Fact]
    public async Task Custom_role_grants_exactly_its_permissions()
    {
        var admin = await factory.SignInAsync("demo.admin");
        await Json(await admin.PostAsJsonAsync("/api/roles", new
        {
            name = "Read-only reviewer", description = "Can read reports", permissions = new[] { Permissions.ReportsView }, requireTwoFactor = false,
        }));
        var c = await ReadyUser("reviewer", "Read-only reviewer");

        var me = await Json(await c.GetAsync("/api/auth/me"));
        Assert.Equal([Permissions.ReportsView], me["permissions"]!.AsArray().Select(p => (string)p!));

        var reports = (await c.GetFromJsonAsync<JsonArray>("/api/reports"))!;
        var id = reports[0]!["id"]!.GetValue<int>();
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync($"/api/reports/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await c.PatchAsJsonAsync($"/api/reports/{id}/values", new { values = new { lv_size = "normal" } })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await c.PostAsJsonAsync("/api/reports", new { patientId = 1, templateId = 1, examDate = "2026-10-10" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await c.GetAsync("/api/users")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await c.GetAsync("/api/audit")).StatusCode);

        // Changing the role applies on the next request, without signing in again.
        var roleId = await RoleId(admin, "Read-only reviewer");
        await Json(await admin.PutAsJsonAsync($"/api/roles/{roleId}", new
        {
            name = "Read-only reviewer", permissions = new[] { Permissions.ReportsView, Permissions.AuditView }, requireTwoFactor = false,
        }));
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/api/audit")).StatusCode);
    }

    [Fact]
    public async Task Service_scope_hides_other_services_reports_and_templates()
    {
        var echoOnly = await factory.SignInAsync("demo.echo");

        var services = (await echoOnly.GetFromJsonAsync<JsonArray>("/api/reports"))!.Select(r => (string)r!["service"]!).Distinct().ToList();
        Assert.Equal(["Echocardiography"], services);
        Assert.Equal(["Echocardiography"], (await echoOnly.GetFromJsonAsync<JsonArray>("/api/templates"))!.Select(t => (string)t!["service"]!).Distinct());

        var typist = await factory.SignInAsync("demo.typist");
        var ecg = (await typist.GetFromJsonAsync<JsonArray>("/api/reports"))!.First(r => (string)r!["service"]! == "Electrocardiography")!["id"]!;
        Assert.Equal(HttpStatusCode.NotFound, (await echoOnly.GetAsync($"/api/reports/{ecg}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await echoOnly.PatchAsJsonAsync($"/api/reports/{ecg}/values", new { values = new { rhythm = "sinus" } })).StatusCode);
    }

    [Fact]
    public async Task Two_factor_codes_are_required_and_work_only_once()
    {
        var c = await ReadyUser("two.factor", "Physician");
        var setup = await Json(await c.PostAsync("/api/auth/two-factor/setup", null));
        var secret = (string)setup["secret"]!;
        Assert.StartsWith("otpauth://totp/", (string)setup["otpAuthUri"]!);

        var step = Totp.StepAt(DateTimeOffset.UtcNow);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync("/api/auth/two-factor/enable", new { code = "000000" })).StatusCode);
        var me = await Json(await c.PostAsJsonAsync("/api/auth/two-factor/enable", new { code = Totp.Code(secret, step) }));
        Assert.True((bool)me["twoFactorEnabled"]!);

        var fresh = Anonymous();
        var noCode = await Login(fresh, "two.factor", NewPassword);
        Assert.Equal(HttpStatusCode.Unauthorized, noCode.StatusCode);
        Assert.Contains("twoFactorRequired", await noCode.Content.ReadAsStringAsync());

        // The code used to enable two-factor cannot be replayed; the next step's code works once.
        Assert.Equal(HttpStatusCode.Unauthorized, (await Login(fresh, "two.factor", NewPassword, Totp.Code(secret, step))).StatusCode);
        await Json(await Login(fresh, "two.factor", NewPassword, Totp.Code(secret, step + 1)));
        Assert.Equal(HttpStatusCode.Unauthorized, (await Login(Anonymous(), "two.factor", NewPassword, Totp.Code(secret, step + 1))).StatusCode);
    }

    [Fact]
    public async Task Role_requiring_two_factor_forces_setup()
    {
        var admin = await factory.SignInAsync("demo.admin");
        await Json(await admin.PostAsJsonAsync("/api/roles", new
        {
            name = "Signing physician (2FA)", permissions = new[] { Permissions.ReportsView, Permissions.ReportsSign }, requireTwoFactor = true,
        }));
        var c = await ReadyUser("needs.2fa", "Signing physician (2FA)");

        var me = await Json(await c.GetAsync("/api/auth/me"));
        Assert.Equal("setup-two-factor", (string)me["pendingAction"]!);
        Assert.Equal(HttpStatusCode.Forbidden, (await c.GetAsync("/api/reports")).StatusCode);

        var secret = (string)(await Json(await c.PostAsync("/api/auth/two-factor/setup", null)))["secret"]!;
        await Json(await c.PostAsJsonAsync("/api/auth/two-factor/enable", new { code = Totp.Code(secret, Totp.StepAt(DateTimeOffset.UtcNow)) }));
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/api/reports")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await c.PostAsJsonAsync("/api/auth/two-factor/disable", new { password = NewPassword })).StatusCode);
    }

    [Fact]
    public async Task Opening_a_report_is_logged_once_per_window()
    {
        var admin = await factory.SignInAsync("demo.admin");
        var physician = await factory.SignInAsync("demo.physician");
        var id = (await physician.GetFromJsonAsync<JsonArray>("/api/reports"))![0]!["id"]!.GetValue<int>();

        await physician.GetAsync($"/api/reports/{id}");
        await physician.GetAsync($"/api/reports/{id}");

        var views = (await admin.GetFromJsonAsync<JsonArray>($"/api/audit?type=ReportViewed&reportId={id}"))!
            .Where(e => (string)e!["actor"]! == "Dr. Avery Morgan").ToList();
        Assert.Single(views);
        Assert.Contains("DEMO-", (string)views[0]!["patient"]!);
    }

    [Fact]
    public async Task The_last_user_manager_cannot_remove_their_own_access()
    {
        var admin = await factory.SignInAsync("demo.admin");
        var users = (await admin.GetFromJsonAsync<JsonArray>("/api/users"))!;
        var me = users.Single(u => (string)u!["userName"]! == "demo.admin")!;
        // Administrator always keeps every permission, whatever is sent.
        var adminRole = await RoleId(admin, "Administrator");
        var saved = await Json(await admin.PutAsJsonAsync($"/api/roles/{adminRole}", new
        {
            name = "Administrator", permissions = Array.Empty<string>(), requireTwoFactor = false,
        }));
        Assert.Contains(Permissions.UsersManage, saved["permissions"]!.AsArray().Select(p => (string)p!));

        // demo.admin is the only user who can manage users (no test creates another), so it can't demote itself.
        var typistRole = await RoleId(admin, "Typist");
        Assert.Single(users, u => u!["permissions"]!.AsArray().Any(p => (string)p! == Permissions.UsersManage));
        var res = await admin.PutAsJsonAsync($"/api/users/{me["id"]}", new { displayName = "Demo Administrator", roleIds = new[] { typistRole }, isActive = true });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Contains("Manage users and roles", await res.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Signing_out_everywhere_ends_other_sessions_only()
    {
        await ReadyUser("many.devices", "Typist");
        var laptop = Anonymous();
        var phone = Anonymous();
        await Json(await Login(laptop, "many.devices", NewPassword));
        await Json(await Login(phone, "many.devices", NewPassword));

        (await laptop.PostAsync("/api/auth/sessions/revoke", null)).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.OK, (await laptop.GetAsync("/api/reports")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await phone.GetAsync("/api/reports")).StatusCode);
    }
}
