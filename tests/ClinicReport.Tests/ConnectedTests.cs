using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ClinicReport.Api.Auth;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace ClinicReport.Tests;

/// <summary>
/// ClinicReport connected to ClinicHub. ClinicHub itself is a separate project, so its discovery document and
/// token endpoint are stand-ins here, and /test/hub-signin plays the part of the sign-in callback: it hands
/// ClinicReport what ClinicHub's id token would carry. (ClinicHub's side is tested in ClinicHub.)
/// </summary>
public class ConnectedFactory : WebApplicationFactory<Program>
{
    private readonly string _dir = Directory.CreateTempSubdirectory("clinicreport-connected-").FullName;

    /// <summary>What ClinicHub's token endpoint answers to a refresh; null means "invalid_grant".</summary>
    public Func<HttpResponseMessage>? TokenEndpoint { get; set; }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ClinicHub:Authority", "https://hub.test/");
        builder.UseSetting("ClinicHub:ClientSecret", "test-secret");
        builder.UseSetting("ClinicHub:DatabasePattern", $"Data Source={Path.Combine(_dir, "clinicreport-{clinic}.db")}");
        builder.UseSetting("ClinicHub:RecheckMinutes", "0");
        builder.UseSetting("Demo:Enabled", "false");
        builder.ConfigureTestServices(services =>
        {
            services.PostConfigure<OpenIdConnectOptions>(ClinicHubConnection.Scheme, o =>
            {
                var config = new OpenIdConnectConfiguration
                {
                    Issuer = "https://hub.test/", AuthorizationEndpoint = "https://hub.test/connect/authorize",
                    TokenEndpoint = "https://hub.test/connect/token", EndSessionEndpoint = "https://hub.test/connect/logout",
                };
                o.Configuration = config;
                o.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(config);
                o.Backchannel = new HttpClient(new Stub(this));
            });
            services.AddSingleton<IStartupFilter>(new HubSignInRoute());
            // ClinicHub's service tokens: signed here with a test key instead of ClinicHub's published one.
            services.PostConfigure<Microsoft.AspNetCore.Authentication.JwtBearer.JwtBearerOptions>(ClinicHubConnection.ServiceScheme, o =>
            {
                var config = new OpenIdConnectConfiguration { Issuer = "https://hub.test/" };
                config.SigningKeys.Add(ServiceKey);
                o.Configuration = config;
                o.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(config);
            });
        });
    }

    private sealed class Stub(ConnectedFactory f) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(f.TokenEndpoint?.Invoke() ?? new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent("""{"error":"invalid_grant"}""", Encoding.UTF8, "application/json"),
            });
    }

    /// <summary>Signs in as if ClinicHub had just returned: ?sub=&user=&clinic=&perm=a,b</summary>
    private sealed class HubSignInRoute : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            // Not app.Map: that would make the path base /test/hub-signin and scope the cookie to it.
            app.Use(async (http, nextMiddleware) =>
            {
                if (http.Request.Path != "/test/hub-signin")
                {
                    await nextMiddleware();
                    return;
                }
                var q = http.Request.Query;
                var hub = new ClaimsPrincipal(new ClaimsIdentity(new List<Claim>
                {
                    new("sub", q["sub"]!), new("preferred_username", q["user"]!), new("name", q["name"].FirstOrDefault() ?? q["user"]!),
                    new("clinic_slug", q["clinic"]!), new("clinic_name", q["clinic"]!), new("role", "Physician"),
                }.Concat(q["perm"].ToString().Split(',', StringSplitOptions.RemoveEmptyEntries).Select(p => new Claim("permission", p))), "test"));
                var principal = await ClinicHubConnection.ToLocalPrincipal(http.RequestServices, hub);
                var props = new AuthenticationProperties();
                props.StoreTokens([new AuthenticationToken { Name = "refresh_token", Value = "rt-1" }]);
                await http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal, props);
            });
            next(app);
        };
    }

    public static readonly Microsoft.IdentityModel.Tokens.RsaSecurityKey ServiceKey = new(System.Security.Cryptography.RSA.Create(2048)) { KeyId = "test" };

    /// <summary>A token like the one ClinicHub sends with an order.</summary>
    public static string ServiceToken(string clinic, string audience = "clinicreport", string scope = "orders") =>
        new Microsoft.IdentityModel.JsonWebTokens.JsonWebTokenHandler().CreateToken(new Microsoft.IdentityModel.Tokens.SecurityTokenDescriptor
        {
            Issuer = "https://hub.test/", Audience = audience, Expires = DateTime.UtcNow.AddMinutes(5),
            SigningCredentials = new Microsoft.IdentityModel.Tokens.SigningCredentials(ServiceKey, Microsoft.IdentityModel.Tokens.SecurityAlgorithms.RsaSha256),
            Claims = new Dictionary<string, object> { ["sub"] = "clinichub", ["scope"] = scope, ["clinic_slug"] = clinic, ["clinic_name"] = clinic },
        });

    public static string IdToken(params (string Type, string Value)[] claims)
    {
        static string B64(string s) => Convert.ToBase64String(Encoding.UTF8.GetBytes(s)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var payload = new JsonObject();
        foreach (var g in claims.GroupBy(c => c.Type))
            payload[g.Key] = g.Count() == 1 ? JsonValue.Create(g.First().Value) : new JsonArray(g.Select(c => (JsonNode?)JsonValue.Create(c.Value)).ToArray());
        return $"{B64("""{"alg":"none"}""")}.{B64(payload.ToJsonString())}.";
    }

    public async Task<HttpClient> SignInAsync(string sub, string user, string clinic, params string[] permissions)
    {
        var c = CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true, AllowAutoRedirect = false });
        c.DefaultRequestHeaders.Add("X-ClinicReport", "1");
        (await c.GetAsync($"/test/hub-signin?sub={sub}&user={user}&clinic={clinic}&perm={string.Join(',', permissions)}")).EnsureSuccessStatusCode();
        return c;
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
    }

    public string DatabaseFile(string clinic) => Path.Combine(_dir, $"clinicreport-{clinic}.db");
}

public class ConnectedTests(ConnectedFactory factory) : IClassFixture<ConnectedFactory>
{
    private static readonly string[] Physician = ["reports.view", "reports.create", "reports.edit", "reports.sign", "patients.manage"];

    private static async Task<JsonNode> Json(HttpResponseMessage res)
    {
        var body = await res.Content.ReadAsStringAsync();
        Assert.True(res.IsSuccessStatusCode, $"{(int)res.StatusCode}: {body}");
        return JsonNode.Parse(body)!;
    }

    private void RefreshKeeps(params string[] permissions) => factory.TokenEndpoint = () => new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = JsonContent.Create(new
        {
            id_token = ConnectedFactory.IdToken([.. permissions.Select(p => ("permission", p)), ("role", "Physician")]),
            access_token = "at", refresh_token = "rt-2", token_type = "Bearer",
        }),
    };

    [Fact]
    public async Task Sign_in_is_ClinicHubs_and_local_accounts_are_closed()
    {
        var anonymous = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        anonymous.DefaultRequestHeaders.Add("X-ClinicReport", "1");
        var settings = await Json(await anonymous.GetAsync("/api/settings"));
        Assert.Equal("clinichub", (string)settings["signIn"]!);
        Assert.Empty(settings["demoUsers"]!.AsArray());
        Assert.Equal(HttpStatusCode.Conflict, (await anonymous.PostAsJsonAsync("/api/auth/login", new { userName = "admin", password = "whatever-123" })).StatusCode);

        var start = await anonymous.GetAsync("/api/auth/sso/start?returnUrl=/reports/7");
        Assert.Equal(HttpStatusCode.Redirect, start.StatusCode);
        var to = start.Headers.Location!.ToString();
        Assert.StartsWith("https://hub.test/connect/authorize?", to);
        Assert.Contains("client_id=clinicreport", to);
        Assert.Contains("code_challenge_method=S256", to);
        // Query is the code flow's default response mode, so it is left out; what matters is that it is not form_post.
        Assert.DoesNotContain("form_post", to);
        var scope = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(start.Headers.Location!.Query)["scope"].ToString().Split(' ');
        Assert.Equal(["clinic", "offline_access", "openid", "profile"], scope.Order());
    }

    [Fact]
    public async Task Each_clinic_has_its_own_database_and_its_own_users()
    {
        RefreshKeeps(Physician);
        var harbour = await factory.SignInAsync("7", "dr.patel", "harbourview", Physician);
        var me = await Json(await harbour.GetAsync("/api/auth/me"));
        Assert.Equal("harbourview", (string)me["clinic"]!);
        Assert.Contains("reports.sign", me["permissions"]!.AsArray().Select(p => (string)p!));
        Assert.NotEmpty((await Json(await harbour.GetAsync("/api/templates"))).AsArray());
        await Json(await harbour.PostAsJsonAsync("/api/patients", new { mrn = "HV-1", firstName = "Ana", lastName = "Harbour", dateOfBirth = "1980-01-01", sex = "F" }));

        var lake = await factory.SignInAsync("7", "dr.patel", "lakeshore", Physician);
        Assert.Empty((await Json(await lake.GetAsync("/api/patients?q=Harbour"))).AsArray());
        Assert.NotEmpty((await Json(await harbour.GetAsync("/api/patients?q=Harbour"))).AsArray());
        Assert.True(File.Exists(factory.DatabaseFile("harbourview")) && File.Exists(factory.DatabaseFile("lakeshore")));

        // Signing in again finds the same local user.
        var again = await factory.SignInAsync("7", "dr.patel", "harbourview", Physician);
        Assert.Equal((int)me["id"]!, (int)(await Json(await again.GetAsync("/api/auth/me")))["id"]!);
    }

    [Fact]
    public async Task Permissions_follow_ClinicHub_at_each_check()
    {
        RefreshKeeps(Physician);
        var c = await factory.SignInAsync("8", "dr.rossi", "harbourview", Physician);
        Assert.Contains("reports.sign", (await Json(await c.GetAsync("/api/auth/me")))["permissions"]!.AsArray().Select(p => (string)p!));

        // ClinicHub took the signing permission away: the next check applies it.
        RefreshKeeps("reports.view");
        var perms = (await Json(await c.GetAsync("/api/auth/me")))["permissions"]!.AsArray().Select(p => (string)p!).ToList();
        Assert.Equal(["reports.view"], perms);
        Assert.Equal(HttpStatusCode.Forbidden, (await c.PostAsJsonAsync("/api/patients", new { mrn = "X-1", firstName = "A", lastName = "B", dateOfBirth = "1990-01-01", sex = "M" })).StatusCode);

        // ClinicHub ended the session (signed out everywhere): the session here ends too.
        factory.TokenEndpoint = null;
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.GetAsync("/api/templates")).StatusCode);
    }

    [Fact]
    public async Task A_ClinicReport_admin_sets_services_but_not_passwords_or_roles()
    {
        RefreshKeeps([.. Physician, "users.manage"]);
        var admin = await factory.SignInAsync("9", "admin", "harbourview", [.. Physician, "users.manage"]);
        RefreshKeeps(Physician);
        await factory.SignInAsync("10", "echo.typist", "harbourview", Physician);
        RefreshKeeps([.. Physician, "users.manage"]);

        var users = (await Json(await admin.GetAsync("/api/users"))).AsArray();
        var typist = users.Single(u => (string)u!["userName"]! == "echo.typist")!;
        Assert.True((bool)typist["managedByClinicHub"]!);
        var saved = await Json(await admin.PutAsJsonAsync($"/api/users/{(int)typist["id"]!}", new
        {
            displayName = "ignored", credentials = (string?)null, roleIds = Array.Empty<int>(), services = new[] { "Echocardiography" }, isActive = true,
        }));
        Assert.Equal(["Echocardiography"], saved["services"]!.AsArray().Select(s => (string)s!));
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsJsonAsync($"/api/users/{(int)typist["id"]!}/password", new { password = "Some-new-pass-1" })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsJsonAsync("/api/roles", new { name = "X", permissions = Array.Empty<string>(), requireTwoFactor = false })).StatusCode);
    }

    [Fact]
    public async Task A_bad_clinic_name_from_the_token_is_refused()
    {
        await Assert.ThrowsAnyAsync<Exception>(() => factory.SignInAsync("11", "x", "../escape", "reports.view"));
    }
}
