using System.Security.Claims;
using ClinicReport.Api.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;

namespace ClinicReport.Api.Auth;

/// <summary>Claim types put on the request principal (rebuilt from the database on every request).</summary>
public static class AppClaims
{
    public const string Stamp = "stamp";
    public const string Permission = "perm";
    public const string Service = "svc";
    public const string AllServices = "*";

    /// <summary>"change-password" or "setup-two-factor": the user must do this before using the app.</summary>
    public const string Pending = "pending";
}

public static class PendingActions
{
    public const string ChangePassword = "change-password";
    public const string SetupTwoFactor = "setup-two-factor";
}

/// <summary>What a user may do, computed from their roles.</summary>
public record Access(string[] Roles, string[] Permissions, string[]? Services, bool TwoFactorRequired, string? Pending)
{
    public static Access For(AppUser user)
    {
        var roles = user.Roles.Select(ur => ur.Role).ToList();
        var permissions = roles.Any(r => r.IsSystem && r.Name == SystemRoles.Administrator)
            ? Auth.Permissions.All
            : Auth.Permissions.All.Where(p => roles.Any(r => Auth.Permissions.Parse(r.Permissions).Contains(p))).ToArray();
        var services = user.ServiceList();
        var twoFactorRequired = roles.Any(r => r.RequireTwoFactor);
        var pending = user.MustChangePassword ? PendingActions.ChangePassword
            : twoFactorRequired && !user.TwoFactorEnabled ? PendingActions.SetupTwoFactor
            : null;
        return new Access(roles.Select(r => r.Name).OrderBy(n => n).ToArray(), permissions,
            services.Count == 0 ? null : services.ToArray(), twoFactorRequired, pending);
    }
}

public static class AuthSetup
{
    /// <summary>Unsafe API requests must carry this header. Browsers cannot add it cross-site without a CORS preflight, which this API never allows.</summary>
    public const string CsrfHeader = "X-ClinicReport";

    public static void AddClinicAuth(this IServiceCollection services, IConfiguration config)
    {
        var connected = config.GetSection("ClinicHub").Get<ClinicHubOptions>()?.Enabled == true;
        var authentication = services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
            .AddCookie(o =>
            {
                o.Cookie.Name = "clinicreport.auth";
                o.Cookie.HttpOnly = true;
                // Connected: the browser arrives from ClinicHub's sign-in, a navigation a Strict cookie would not survive.
                o.Cookie.SameSite = connected ? SameSiteMode.Lax : SameSiteMode.Strict;
                o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
                o.ExpireTimeSpan = TimeSpan.FromHours(12);
                o.SlidingExpiration = true;
                // An API answers 401/403; it never redirects to a login page.
                o.Events.OnRedirectToLogin = ctx => { ctx.Response.StatusCode = 401; return Task.CompletedTask; };
                o.Events.OnRedirectToAccessDenied = ctx => { ctx.Response.StatusCode = 403; return Task.CompletedTask; };
                o.Events.OnValidatePrincipal = ctx => ctx.Principal?.HasClaim(c => c.Type == ClinicHubClaims.Clinic) == true
                    ? ClinicHubConnection.ValidateAsync(ctx)
                    : LoadCurrentAccess(ctx);
            });
        services.AddClinicHubSignIn(config, authentication);

        var auth = services.AddAuthorizationBuilder()
            .SetFallbackPolicy(new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());
        // One policy per permission, named after it: .RequireAuthorization(Permissions.ReportsSign)
        foreach (var p in Permissions.All)
            auth.AddPolicy(p, policy => policy.RequireClaim(AppClaims.Permission, p));
        auth.AddPolicy(PatientsRead, policy => policy.RequireClaim(AppClaims.Permission,
            Permissions.ReportsView, Permissions.ReportsCreate, Permissions.PatientsManage));
    }

    public const string PatientsRead = "patients.read";

    /// <summary>
    /// The cookie only holds the user id and security stamp. Roles, permissions and services are read
    /// fresh on every request, so an admin's change applies at once; a changed stamp (password reset,
    /// deactivation, "sign out everywhere") ends the session.
    /// </summary>
    private static async Task LoadCurrentAccess(CookieValidatePrincipalContext ctx)
    {
        var id = ctx.Principal?.UserId();
        var stamp = ctx.Principal?.FindFirstValue(AppClaims.Stamp);
        // A local session presented to a connected installation has no database to check against.
        if (ctx.HttpContext.RequestServices.IsConnected())
        {
            ctx.RejectPrincipal();
            return;
        }
        var db = ctx.HttpContext.RequestServices.GetRequiredService<ClinicReportDbContext>();
        var user = id is null ? null : await db.Users.AsNoTracking()
            .Include(u => u.Roles).ThenInclude(ur => ur.Role)
            .SingleOrDefaultAsync(u => u.Id == id);

        if (user is null || !user.IsActive || user.SecurityStamp != stamp)
        {
            ctx.RejectPrincipal();
            await ctx.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return;
        }
        ctx.ReplacePrincipal(CreatePrincipal(user, Access.For(user)));
    }

    public static ClaimsPrincipal CreatePrincipal(AppUser user, Access? access = null)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new(ClaimTypes.Name, user.DisplayName),
            new(AppClaims.Stamp, user.SecurityStamp),
        };
        if (access is not null)
        {
            claims.AddRange(access.Roles.Select(r => new Claim(ClaimTypes.Role, r)));
            if (access.Pending is null)
                claims.AddRange(access.Permissions.Select(p => new Claim(AppClaims.Permission, p)));
            else
                claims.Add(new Claim(AppClaims.Pending, access.Pending));
            claims.AddRange(access.Services is null
                ? [new Claim(AppClaims.Service, AppClaims.AllServices)]
                : access.Services.Select(s => new Claim(AppClaims.Service, s)));
        }
        return new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme));
    }

    public static Task SignInAsync(HttpContext http, AppUser user) =>
        http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, CreatePrincipal(user));

    public static int? UserId(this ClaimsPrincipal user) =>
        int.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;

    public static int RequiredUserId(this ClaimsPrincipal user) =>
        user.UserId() ?? throw new InvalidOperationException("No signed-in user.");

    public static bool Can(this ClaimsPrincipal user, string permission) =>
        user.HasClaim(AppClaims.Permission, permission);

    /// <summary>Null when the user may work on every service; otherwise the allowed service names.</summary>
    public static string[]? ServiceScope(this ClaimsPrincipal user)
    {
        var services = user.FindAll(AppClaims.Service).Select(c => c.Value).ToArray();
        return services.Contains(AppClaims.AllServices) ? null : services;
    }

    public static bool CanAccessService(this ClaimsPrincipal user, string service) =>
        user.ServiceScope() is not { } scope || scope.Contains(service, StringComparer.OrdinalIgnoreCase);

    /// <summary>Rejects POST/PUT/PATCH/DELETE to /api without the custom header (cheap CSRF defence on top of SameSite=Strict).</summary>
    public static IApplicationBuilder UseCsrfHeaderCheck(this IApplicationBuilder app) => app.Use(async (ctx, next) =>
    {
        var unsafeMethod = !(HttpMethods.IsGet(ctx.Request.Method) || HttpMethods.IsHead(ctx.Request.Method) || HttpMethods.IsOptions(ctx.Request.Method));
        if (unsafeMethod && ctx.Request.Path.StartsWithSegments("/api") && !ctx.Request.Headers.ContainsKey(CsrfHeader))
        {
            ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
            await ctx.Response.WriteAsJsonAsync(new { title = $"Missing {CsrfHeader} header." });
            return;
        }
        await next();
    });

    /// <summary>
    /// A user who must change their password or set up two-factor gets nothing but the /api/auth
    /// endpoints until they have. (They also carry no permission claims, so this is belt and braces.)
    /// </summary>
    public static IApplicationBuilder UsePendingActionGate(this IApplicationBuilder app) => app.Use(async (ctx, next) =>
    {
        var pending = ctx.User.FindFirstValue(AppClaims.Pending);
        var path = ctx.Request.Path;
        if (pending is not null && (path.StartsWithSegments("/hubs")
            || (path.StartsWithSegments("/api") && !path.StartsWithSegments("/api/auth") && !path.StartsWithSegments("/api/settings"))))
        {
            ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
            await ctx.Response.WriteAsJsonAsync(new { title = pending == PendingActions.ChangePassword
                ? "Choose a new password first." : "Set up two-factor sign-in first.", pending });
            return;
        }
        await next();
    });
}
