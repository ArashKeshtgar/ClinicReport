using System.Collections.Concurrent;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text.Json;
using ClinicReport.Api.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace ClinicReport.Api.Auth;

/// <summary>
/// Connected mode: ClinicReport as one of ClinicHub's applications (https://github.com/ArashKeshtgar/ClinicHub).
/// People sign in through ClinicHub; ClinicHub decides who may use ClinicReport at which clinic and with
/// which permissions. Without this section ClinicReport runs standalone with its own accounts, as before.
/// </summary>
public class ClinicHubOptions
{
    /// <summary>ClinicHub's address (its OpenID Connect issuer), e.g. https://hub.example.ca/. Empty: standalone.</summary>
    public string? Authority { get; set; }
    public string ClientId { get; set; } = "clinicreport";
    public string? ClientSecret { get; set; }

    /// <summary>One database per clinic; {clinic} is the clinic's short name from ClinicHub.</summary>
    public string DatabasePattern { get; set; } = "Data Source=clinicreport-{clinic}.db";

    /// <summary>How often a session is checked with ClinicHub (roles taken away, signed out everywhere).</summary>
    public int RecheckMinutes { get; set; } = 5;

    /// <summary>If ClinicHub can't be reached, sessions keep working this long after the last successful check.</summary>
    public int OfflineGraceMinutes { get; set; } = 30;

    public bool RequireHttpsMetadata { get; set; } = true;

    public bool Enabled => !string.IsNullOrWhiteSpace(Authority);
}

public static class ClinicHubClaims
{
    /// <summary>The clinic's short name (ClinicHub's clinic_slug): it picks this clinic's database.</summary>
    public const string Clinic = "clinic";
    public const string ClinicName = "clinic_name";

    /// <summary>When the session was last confirmed by ClinicHub (Unix seconds).</summary>
    public const string CheckedAt = "checked_at";
}

/// <summary>Opens a clinic's own database, creating and preparing it (migrations, roles, bundled templates) the first time.</summary>
public class ClinicReportDatabases(IOptions<ClinicHubOptions> options, IServiceProvider services)
{
    private readonly ConcurrentDictionary<string, Lazy<Task>> _ready = new();

    public static bool IsValidSlug(string? slug) =>
        slug is { Length: >= 2 and <= 40 } && slug.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '-');

    public ClinicReportDbContext Create(string slug)
    {
        if (!IsValidSlug(slug))
            throw new InvalidOperationException("Invalid clinic name.");
        var cs = options.Value.DatabasePattern.Replace("{clinic}", slug);
        return new ClinicReportDbContext(new DbContextOptionsBuilder<ClinicReportDbContext>().UseSqlite(cs).Options);
    }

    public async Task<ClinicReportDbContext> OpenAsync(string slug)
    {
        await _ready.GetOrAdd(slug, s => new Lazy<Task>(() => PrepareAsync(s))).Value;
        return Create(slug);
    }

    /// <summary>For dependency injection, which cannot await; sign-in has prepared the clinic by then.</summary>
    public ClinicReportDbContext Open(string slug) => OpenAsync(slug).GetAwaiter().GetResult();

    private async Task PrepareAsync(string slug)
    {
        await using var db = Create(slug);
        await db.Database.MigrateAsync();
        await RoleSeeder.SeedAsync(db);
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("ClinicHub");
        await TemplateSeeder.SeedAsync(db, Path.Combine(AppContext.BaseDirectory, "templates"), logger, DateTime.UtcNow);
    }
}

public static class ClinicHubConnection
{
    public const string Scheme = "ClinicHub";

    public static bool IsConnected(this IServiceProvider services) =>
        services.GetRequiredService<IOptions<ClinicHubOptions>>().Value.Enabled;

    /// <summary>Registers the ClinicHub sign-in and the per-clinic databases. Returns false (and changes nothing) when standalone.</summary>
    public static bool AddClinicHubSignIn(this IServiceCollection services, IConfiguration config, AuthenticationBuilder auth)
    {
        services.Configure<ClinicHubOptions>(config.GetSection("ClinicHub"));
        var hub = config.GetSection("ClinicHub").Get<ClinicHubOptions>() ?? new ClinicHubOptions();
        if (!hub.Enabled)
            return false;

        services.AddSingleton<ClinicReportDatabases>();
        // The database of the request is the signed-in person's clinic.
        services.AddScoped(sp =>
        {
            var slug = sp.GetRequiredService<IHttpContextAccessor>().HttpContext?.User.FindFirstValue(ClinicHubClaims.Clinic)
                ?? throw new InvalidOperationException("Not signed in to a clinic.");
            return sp.GetRequiredService<ClinicReportDatabases>().Open(slug);
        });

        auth.AddOpenIdConnect(Scheme, "ClinicHub", o =>
        {
            o.Authority = hub.Authority;
            o.ClientId = hub.ClientId;
            o.ClientSecret = hub.ClientSecret;
            o.RequireHttpsMetadata = hub.RequireHttpsMetadata;
            o.SignInScheme = CookieAuthenticationDefaults.AuthenticationScheme;
            o.ResponseType = OpenIdConnectResponseType.Code;
            // A redirect, not a cross-site form post, so the correlation cookies can stay SameSite=Lax over plain HTTP in development.
            o.ResponseMode = OpenIdConnectResponseMode.Query;
            o.UsePkce = true;
            o.Scope.Clear();
            foreach (var s in new[] { "openid", "profile", "clinic", "offline_access" })
                o.Scope.Add(s);
            o.SaveTokens = true;
            o.MapInboundClaims = false;
            o.GetClaimsFromUserInfoEndpoint = false;
            o.TokenValidationParameters.NameClaimType = "name";
            o.TokenValidationParameters.RoleClaimType = "role";
            o.CorrelationCookie.SameSite = SameSiteMode.Lax;
            o.CorrelationCookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
            o.NonceCookie.SameSite = SameSiteMode.Lax;
            o.NonceCookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
            o.Events.OnTokenValidated = async ctx =>
            {
                ctx.Principal = await ToLocalPrincipal(ctx.HttpContext.RequestServices, ctx.Principal!);
                ctx.Properties!.IsPersistent = false;
            };
            // ClinicHub said no (e.g. no role allows ClinicReport at this clinic): show its reason on the sign-in page.
            o.Events.OnRemoteFailure = ctx =>
            {
                ctx.Response.Redirect("/?signInError=" + Uri.EscapeDataString(ctx.Failure?.Message is { } m && m.Contains("access_denied")
                    ? "ClinicHub: you have no role that allows ClinicReport at the clinic you chose."
                    : "Signing in through ClinicHub did not complete. Try again."));
                ctx.HandleResponse();
                return Task.CompletedTask;
            };
        });
        return true;
    }

    /// <summary>
    /// ClinicHub's person becomes a user of this clinic's ClinicReport database (created on first sign-in,
    /// linked by ClinicHub's account id), so signatures, addenda and the report-access log refer to them.
    /// Permissions and roles come from ClinicHub; the services a user may work on stay ClinicReport's.
    /// </summary>
    public static async Task<ClaimsPrincipal> ToLocalPrincipal(IServiceProvider services, ClaimsPrincipal hub)
    {
        var slug = hub.FindFirstValue("clinic_slug");
        if (!ClinicReportDatabases.IsValidSlug(slug))
            throw new InvalidOperationException("ClinicHub did not say which clinic.");
        await using var db = await services.GetRequiredService<ClinicReportDatabases>().OpenAsync(slug!);
        var externalId = $"clinichub:{hub.FindFirstValue("sub")}";
        var now = DateTime.UtcNow;
        var user = await db.Users.SingleOrDefaultAsync(u => u.ExternalId == externalId);
        if (user is null)
        {
            var userName = hub.FindFirstValue("preferred_username") ?? externalId;
            // A local account with the same user name (from before the clinic connected) is taken over, keeping its history.
            user = await db.Users.SingleOrDefaultAsync(u => u.UserName == userName && u.ExternalId == null)
                ?? db.Users.Add(new AppUser { UserName = userName, CreatedAt = now }).Entity;
            user.ExternalId = externalId;
            user.PasswordHash = "";
            user.TwoFactorEnabled = false;
            user.TwoFactorSecret = null;
            user.MustChangePassword = false;
        }
        user.DisplayName = hub.FindFirstValue("name") ?? user.UserName;
        user.Credentials = hub.FindFirstValue("credentials");
        user.IsActive = true;
        user.LastLoginAt = now;
        await db.SaveChangesAsync();
        db.SecurityEvents.Add(new SecurityEvent { At = now, Type = SecurityEventTypes.LoginSucceeded, ActorUserId = user.Id, Detail = "Through ClinicHub" });
        await db.SaveChangesAsync();

        return Principal(user, slug!, hub.FindFirstValue("clinic_name") ?? slug!,
            hub.FindAll("permission").Select(c => c.Value), hub.FindAll("role").Select(c => c.Value), now);
    }

    private static ClaimsPrincipal Principal(AppUser user, string slug, string clinicName, IEnumerable<string> permissions, IEnumerable<string> roles, DateTime checkedAt)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new(ClaimTypes.Name, user.DisplayName),
            new(AppClaims.Stamp, user.SecurityStamp),
            new(ClinicHubClaims.Clinic, slug),
            new(ClinicHubClaims.ClinicName, clinicName),
            new(ClinicHubClaims.CheckedAt, new DateTimeOffset(checkedAt).ToUnixTimeSeconds().ToString()),
        };
        claims.AddRange(roles.Select(r => new Claim(ClaimTypes.Role, r)));
        // Only keys ClinicReport knows: ClinicHub already sends this application's permissions, without its prefix.
        claims.AddRange(permissions.Where(Permissions.All.Contains).Distinct().Select(p => new Claim(AppClaims.Permission, p)));
        var services = user.ServiceList();
        claims.AddRange(services.Count == 0 ? [new Claim(AppClaims.Service, AppClaims.AllServices)] : services.Select(s => new Claim(AppClaims.Service, s)));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme));
    }

    /// <summary>
    /// Every request: the local user must still be active (a ClinicReport admin can still deactivate or limit them).
    /// Every few minutes: ClinicHub is asked again with the refresh token; a refusal (roles removed, signed out
    /// everywhere, account closed) ends the session; ClinicHub being unreachable is tolerated for a while.
    /// </summary>
    public static async Task ValidateAsync(CookieValidatePrincipalContext ctx)
    {
        var principal = ctx.Principal!;
        var slug = principal.FindFirstValue(ClinicHubClaims.Clinic)!;
        var services = ctx.HttpContext.RequestServices;
        var options = services.GetRequiredService<IOptions<ClinicHubOptions>>().Value;
        await using var db = await services.GetRequiredService<ClinicReportDatabases>().OpenAsync(slug);
        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == principal.UserId());
        if (user is null || !user.IsActive || user.ExternalId is null || user.SecurityStamp != principal.FindFirstValue(AppClaims.Stamp))
        {
            await Reject(ctx);
            return;
        }

        var checkedAt = DateTimeOffset.FromUnixTimeSeconds(long.Parse(principal.FindFirstValue(ClinicHubClaims.CheckedAt) ?? "0")).UtcDateTime;
        var permissions = principal.FindAll(AppClaims.Permission).Select(c => c.Value).ToList();
        var roles = principal.FindAll(ClaimTypes.Role).Select(c => c.Value).ToList();
        var clinicName = principal.FindFirstValue(ClinicHubClaims.ClinicName) ?? slug;
        if (DateTime.UtcNow - checkedAt >= TimeSpan.FromMinutes(options.RecheckMinutes))
        {
            switch (await Refresh(ctx, options))
            {
                case RefreshResult.Refused:
                    await Reject(ctx);
                    return;
                case RefreshResult.Unreachable when DateTime.UtcNow - checkedAt > TimeSpan.FromMinutes(options.OfflineGraceMinutes):
                    await Reject(ctx);
                    return;
                case RefreshResult.Unreachable:
                    break;
                case RefreshResult.Ok(var idToken):
                    permissions = idToken.Claims.Where(c => c.Type == "permission").Select(c => c.Value).ToList();
                    roles = idToken.Claims.Where(c => c.Type == "role").Select(c => c.Value).ToList();
                    checkedAt = DateTime.UtcNow;
                    ctx.ShouldRenew = true;
                    break;
            }
        }
        // Services may have changed in ClinicReport since sign-in.
        ctx.ReplacePrincipal(Principal(user, slug, clinicName, permissions, roles, checkedAt));
    }

    private abstract record RefreshResult
    {
        public sealed record Ok(JwtSecurityToken IdToken) : RefreshResult;
        public sealed record Refused : RefreshResult;
        public sealed record Unreachable : RefreshResult;
    }

    private static async Task<RefreshResult> Refresh(CookieValidatePrincipalContext ctx, ClinicHubOptions options)
    {
        var refreshToken = ctx.Properties.GetTokenValue("refresh_token");
        if (refreshToken is null)
            return new RefreshResult.Refused();
        var services = ctx.HttpContext.RequestServices;
        var oidc = services.GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>().Get(Scheme);
        try
        {
            var configuration = await oidc.ConfigurationManager!.GetConfigurationAsync(ctx.HttpContext.RequestAborted);
            using var response = await oidc.Backchannel.PostAsync(configuration.TokenEndpoint, new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token", ["refresh_token"] = refreshToken,
                ["client_id"] = options.ClientId, ["client_secret"] = options.ClientSecret ?? "",
            }));
            if (!response.IsSuccessStatusCode)
                return (int)response.StatusCode is >= 400 and < 500 ? new RefreshResult.Refused() : new RefreshResult.Unreachable();
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            // Straight from ClinicHub's token endpoint over the authenticated back channel (OpenID Connect Core 3.1.3.7).
            var idToken = json.RootElement.GetProperty("id_token").GetString()!;
            ctx.Properties.UpdateTokenValue("id_token", idToken);
            ctx.Properties.UpdateTokenValue("access_token", json.RootElement.GetProperty("access_token").GetString()!);
            if (json.RootElement.TryGetProperty("refresh_token", out var rt))
                ctx.Properties.UpdateTokenValue("refresh_token", rt.GetString()!);
            return new RefreshResult.Ok(new JwtSecurityTokenHandler().ReadJwtToken(idToken));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            services.GetRequiredService<ILoggerFactory>().CreateLogger("ClinicHub").LogWarning(ex, "ClinicHub could not be reached to check a session.");
            return new RefreshResult.Unreachable();
        }
    }

    private static async Task Reject(CookieValidatePrincipalContext ctx)
    {
        ctx.RejectPrincipal();
        await ctx.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    }
}
