using System.Security.Claims;
using ClinicReport.Api.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ClinicReport.Api.Auth;

/// <summary>Hosted mode: the tenant this request is for, found from the host name ({slug}.{base domain}).</summary>
public interface ITenantContext
{
    Tenant? Tenant { get; }

    /// <summary>A suspended tenant can still sign in, read and export, but not change anything.</summary>
    bool ReadOnly { get; }
}

internal sealed class TenantContext : ITenantContext
{
    public Tenant? Tenant { get; set; }
    public bool ReadOnly => Tenant?.Status == TenantStatus.Suspended;
}

public static class Tenancy
{
    public static void AddTenancy(this IServiceCollection services)
    {
        services.AddScoped<TenantContext>();
        services.AddScoped<ITenantContext>(sp => sp.GetRequiredService<TenantContext>());
    }

    /// <summary>The tenant slug in a host name, or null when the host is not one tenant's subdomain of the base domain.</summary>
    public static string? SlugOf(string host, string baseDomain)
    {
        var suffix = "." + baseDomain.Trim('.');
        if (!host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            return null;
        var slug = host[..^suffix.Length].ToLowerInvariant();
        return ClinicReportDatabases.IsValidSlug(slug) ? slug : null;
    }

    /// <summary>
    /// Before authentication: an unknown or cancelled tenant gets 404 for everything but /healthz, so a guessed
    /// subdomain learns nothing. The sign-in cookie needs no Domain: without one it is sent to its own host only.
    /// </summary>
    public static IApplicationBuilder UseTenantResolution(this IApplicationBuilder app)
    {
        if (!app.ApplicationServices.IsHosted())
            return app;
        return app.Use(async (ctx, next) =>
        {
            if (ctx.Request.Path.StartsWithSegments("/healthz"))
            {
                await next();
                return;
            }
            var hosting = ctx.RequestServices.GetRequiredService<IOptions<HostingOptions>>().Value;
            var slug = SlugOf(ctx.Request.Host.Host, hosting.BaseDomain!);
            var tenant = slug is null ? null : await ctx.RequestServices.GetRequiredService<CatalogDbContext>().Tenants
                .AsNoTracking().Include(t => t.Settings).SingleOrDefaultAsync(t => t.Slug == slug);
            if (tenant is null || tenant.Status == TenantStatus.Cancelled)
            {
                ctx.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }
            ctx.RequestServices.GetRequiredService<TenantContext>().Tenant = tenant;
            await next();
        });
    }

    /// <summary>
    /// After authentication: a session belongs to the tenant it was signed in at. Every tenant's cookie is
    /// readable by the same keys, so one carried to another subdomain is refused here (401). A suspended
    /// tenant gets no changes, apart from signing in and out.
    /// </summary>
    public static IApplicationBuilder UseTenantGate(this IApplicationBuilder app)
    {
        if (!app.ApplicationServices.IsHosted())
            return app;
        return app.Use(async (ctx, next) =>
        {
            var tenant = ctx.RequestServices.GetRequiredService<ITenantContext>();
            if (tenant.Tenant is null)
            {
                await next();
                return;
            }
            if (ctx.User.Identity?.IsAuthenticated == true && ctx.User.FindFirstValue(ClinicHubClaims.Clinic) != tenant.Tenant.Slug)
            {
                ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await ctx.Response.WriteAsJsonAsync(new { title = "This session belongs to another clinic." });
                return;
            }
            var method = ctx.Request.Method;
            var unsafeMethod = !(HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || HttpMethods.IsOptions(method));
            if (tenant.ReadOnly && unsafeMethod && ctx.Request.Path.StartsWithSegments("/api") && !ctx.Request.Path.StartsWithSegments("/api/auth"))
            {
                ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                await ctx.Response.WriteAsJsonAsync(new { title = "This clinic's account is suspended. Reports can be read and exported, not changed." });
                return;
            }
            await next();
        });
    }
}
