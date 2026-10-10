using System.Security.Claims;
using ClinicReport.Api.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;

namespace ClinicReport.Api.Auth;

public static class Policies
{
    public const string Sign = "sign";
    public const string Admin = "admin";
}

public static class AuthSetup
{
    /// <summary>Unsafe API requests must carry this header. Browsers cannot add it cross-site without a CORS preflight, which this API never allows.</summary>
    public const string CsrfHeader = "X-ClinicReport";

    public static void AddClinicAuth(this IServiceCollection services)
    {
        services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
            .AddCookie(o =>
            {
                o.Cookie.Name = "clinicreport.auth";
                o.Cookie.HttpOnly = true;
                o.Cookie.SameSite = SameSiteMode.Strict;
                o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
                o.ExpireTimeSpan = TimeSpan.FromHours(12);
                o.SlidingExpiration = true;
                // An API answers 401/403; it never redirects to a login page.
                o.Events.OnRedirectToLogin = ctx => { ctx.Response.StatusCode = 401; return Task.CompletedTask; };
                o.Events.OnRedirectToAccessDenied = ctx => { ctx.Response.StatusCode = 403; return Task.CompletedTask; };
                o.Events.OnValidatePrincipal = ValidateStillActive;
            });

        services.AddAuthorizationBuilder()
            .SetFallbackPolicy(new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build())
            .AddPolicy(Policies.Sign, p => p.RequireRole(Roles.Physician))
            .AddPolicy(Policies.Admin, p => p.RequireRole(Roles.Admin));
    }

    /// <summary>A deactivated user or a changed role ends the session on the next request, not when the cookie expires.</summary>
    private static async Task ValidateStillActive(CookieValidatePrincipalContext ctx)
    {
        var id = ctx.Principal?.UserId();
        var role = ctx.Principal?.FindFirstValue(ClaimTypes.Role);
        var db = ctx.HttpContext.RequestServices.GetRequiredService<ClinicReportDbContext>();
        var ok = id is not null && await db.Users.AnyAsync(u => u.Id == id && u.IsActive && u.Role == role);
        if (!ok)
        {
            ctx.RejectPrincipal();
            await ctx.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        }
    }

    public static ClaimsPrincipal CreatePrincipal(AppUser user) => new(new ClaimsIdentity(
    [
        new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
        new Claim(ClaimTypes.Name, user.DisplayName),
        new Claim(ClaimTypes.Role, user.Role),
    ], CookieAuthenticationDefaults.AuthenticationScheme));

    public static int? UserId(this ClaimsPrincipal user) =>
        int.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;

    public static int RequiredUserId(this ClaimsPrincipal user) =>
        user.UserId() ?? throw new InvalidOperationException("No signed-in user.");

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
}
