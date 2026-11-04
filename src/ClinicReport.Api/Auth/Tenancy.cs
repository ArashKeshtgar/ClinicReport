using System.Security.Claims;
using ClinicReport.Api.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace ClinicReport.Api.Auth;

/// <summary>Hosted mode: the tenant this request is for, found from the host name ({slug}.{base domain}).</summary>
public interface ITenantContext
{
    Tenant? Tenant { get; }

    /// <summary>A suspended tenant, or a cancelled one in its time to export, can still sign in, read and export, but not change anything.</summary>
    bool ReadOnly { get; }
}

internal sealed class TenantContext : ITenantContext
{
    public Tenant? Tenant { get; set; }
    public bool ReadOnly => Tenant?.Status is TenantStatus.Suspended or TenantStatus.Cancelled;
}

/// <summary>
/// Hosted mode: tenants by slug, read from the catalog and kept for a short while, so requests don't query the catalog
/// every time. Whatever changes a tenant (its status, plan or settings) calls <see cref="Forget"/>.
/// </summary>
public sealed class TenantDirectory(IMemoryCache cache)
{
    public static readonly TimeSpan KeepFor = TimeSpan.FromSeconds(30);

    private static string Key(string slug) => "tenant|" + slug;

    /// <summary>The tenant, or null when there is none. A slug with no tenant is not kept: anyone can make up subdomains.</summary>
    public async Task<Tenant?> FindAsync(string slug, CatalogDbContext catalog, CancellationToken ct = default)
    {
        if (cache.TryGetValue(Key(slug), out Tenant? tenant))
            return tenant;
        tenant = await catalog.Tenants.AsNoTracking().Include(t => t.Settings).SingleOrDefaultAsync(t => t.Slug == slug, ct);
        if (tenant is not null)
            cache.Set(Key(slug), tenant, KeepFor);
        return tenant;
    }

    public void Forget(string slug) => cache.Remove(Key(slug));
}

public static class Tenancy
{
    public static void AddTenancy(this IServiceCollection services)
    {
        services.AddSingleton<TenantDirectory>();
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

    /// <summary>Starting a report: a new one, or one from an order.</summary>
    private static bool StartsReport(PathString path) =>
        path.Equals("/api/reports") || path.Equals("/api/reports/")
        || (path.StartsWithSegments("/api/orders") && path.Value!.EndsWith("/start", StringComparison.Ordinal));

    /// <summary>Where Stripe calls: at the root domain, as one webhook serves every tenant.</summary>
    public const string StripeWebhookPath = "/api/billing/stripe";

    /// <summary>The base domain itself: the service's own front page, where clinics sign up.</summary>
    public static bool IsRoot(string host, string baseDomain) => string.Equals(host, baseDomain.Trim('.'), StringComparison.OrdinalIgnoreCase);

    /// <summary>Hosted mode, on the root domain: no tenant, only sign-up.</summary>
    public static bool OnRootDomain(this HttpContext http) =>
        http.RequestServices.IsHosted() && http.RequestServices.GetRequiredService<ITenantContext>().Tenant is null;

    /// <summary>The clinic's name and letterhead: the tenant's in Hosted mode, otherwise the "Clinic" settings.</summary>
    public static ClinicOptions CurrentClinic(this HttpContext http)
    {
        var configured = http.RequestServices.GetRequiredService<IOptions<ClinicOptions>>().Value;
        if (http.RequestServices.GetService<ITenantContext>()?.Tenant is not { } tenant)
            return configured;
        return new ClinicOptions
        {
            Name = tenant.Name, Address = tenant.Settings?.Address, Phone = tenant.Settings?.Phone,
            ReportFooter = tenant.Settings?.ReportFooter,
        };
    }

    /// <summary>
    /// Before authentication: an unknown or gone tenant gets 404 for everything but /healthz, so a guessed
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
            if (IsRoot(ctx.Request.Host.Host, hosting.BaseDomain!))
            {
                // The root domain serves the web app's sign-up page and the sign-up API; nothing of any tenant.
                var path = ctx.Request.Path;
                if (path.StartsWithSegments("/hubs") || (path.StartsWithSegments("/api")
                    && !path.StartsWithSegments("/api/signup") && !path.StartsWithSegments("/api/settings") && !path.StartsWithSegments(StripeWebhookPath)))
                {
                    ctx.Response.StatusCode = StatusCodes.Status404NotFound;
                    return;
                }
                await next();
                return;
            }
            var slug = SlugOf(ctx.Request.Host.Host, hosting.BaseDomain!);
            var tenant = slug is null ? null : await ctx.RequestServices.GetRequiredService<TenantDirectory>()
                .FindAsync(slug, ctx.RequestServices.GetRequiredService<CatalogDbContext>(), ctx.RequestAborted);
            if (tenant is null || tenant.Gone(ctx.RequestServices.GetRequiredService<TimeProvider>().GetUtcNow().UtcDateTime))
            {
                ctx.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }
            ctx.RequestServices.GetRequiredService<TenantContext>().Tenant = tenant;
            // Prepared here, where it can be awaited; the scoped DbContext then opens it without waiting.
            await (await ctx.RequestServices.GetRequiredService<ClinicReportDatabases>().OpenAsync(tenant.Slug)).DisposeAsync();
            await next();
        });
    }

    /// <summary>
    /// After authentication: a session belongs to the tenant it was signed in at. Every tenant's cookie is
    /// readable by the same keys, so one carried to another subdomain is refused here (401). A suspended
    /// tenant gets no changes, apart from signing in and out and paying, the way out of being suspended.
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
            if (tenant.ReadOnly && unsafeMethod && ctx.Request.Path.StartsWithSegments("/api")
                && !ctx.Request.Path.StartsWithSegments("/api/auth") && !ctx.Request.Path.StartsWithSegments("/api/billing"))
            {
                ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                var why = tenant.Tenant.Status == TenantStatus.Cancelled ? "subscription has ended" : "account is suspended";
                await ctx.Response.WriteAsJsonAsync(new { title = $"This clinic's {why}. Reports can be read and exported, not changed." });
                return;
            }
            if (tenant.Tenant.AwaitingEmailVerification && HttpMethods.IsPost(method) && StartsReport(ctx.Request.Path))
            {
                ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                await ctx.Response.WriteAsJsonAsync(new { title = "Confirm the clinic's email address first: open the link we sent when you signed up.", code = "verify-email" });
                return;
            }
            await next();
        });
    }
}
