using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using ClinicReport.Api.Auth;
using ClinicReport.Api.Data;
using ClinicReport.Api.Reports;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace ClinicReport.Api.Endpoints;

/// <summary>
/// Hosted mode: the service operator's console, at admin.{base domain}. Its accounts are in the catalog, not in any
/// clinic, and always need a two-factor code. It sees the catalog and the size of each database file, and never opens a
/// clinic's database: it has no way to a patient or a report.
/// </summary>
public static class OperatorEndpoints
{
    public record LoginBody(string? UserName, string? Password, string? Code);
    public record Me(string UserName);
    public record TenantRow(
        int Id, string Slug, string Name, string Plan, string Status, DateTime CreatedAt,
        int? ActiveUsers, int? SignedReports, DateTime? LastActivityAt, long DatabaseBytes, bool DatabaseFailed,
        DateTime? DeletedAt, bool CanDelete);
    public record EventRow(DateTime At, string? Operator, string Type, string? Tenant, string? Detail);
    public record DeleteBody(string? ConfirmSlug);

    private const int MaxFailedLogins = 5;
    private static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(15);

    public static void MapOperatorEndpoints(this IEndpointRouteBuilder app)
    {
        if (!app.ServiceProvider.IsHosted())
            return;

        app.MapPost("/api/operator/login", async (LoginBody body, HttpContext http, CatalogDbContext catalog, IPasswordHasher<OperatorUser> hasher,
            IDataProtectionProvider protection, TimeProvider clock) =>
        {
            var now = clock.GetUtcNow();
            var userName = body.UserName?.Trim() ?? "";
            var user = await catalog.Operators.SingleOrDefaultAsync(o => o.UserName == userName);
            if (user?.LockoutEndUtc is { } until && until > now.UtcDateTime)
                return Results.Problem("Too many failed attempts. Try again later.", statusCode: StatusCodes.Status423Locked);

            // One answer for all three, and the hash is always worked out: nothing tells which part was wrong.
            var password = hasher.VerifyHashedPassword(user ?? new OperatorUser(), user?.PasswordHash ?? OperatorSetup.DummyHash, body.Password ?? "");
            long? step = null;
            if (user is not null && password != PasswordVerificationResult.Failed)
                step = Totp.Verify(protection.CreateProtector(OperatorSetup.TotpPurpose).Unprotect(user.TotpSecret), body.Code, now, user.TotpLastStep);
            if (user is null || step is null)
            {
                if (user is not null && ++user.FailedLoginCount >= MaxFailedLogins)
                {
                    user.FailedLoginCount = 0;
                    user.LockoutEndUtc = now.UtcDateTime + LockoutDuration;
                }
                Log(catalog, now.UtcDateTime, user?.Id, OperatorEventTypes.LoginFailed);
                await catalog.SaveChangesAsync();
                return Results.Problem("Wrong user name, password or code.", statusCode: StatusCodes.Status401Unauthorized);
            }

            user.TotpLastStep = step.Value;
            user.FailedLoginCount = 0;
            user.LockoutEndUtc = null;
            Log(catalog, now.UtcDateTime, user.Id, OperatorEventTypes.LoginSucceeded);
            await catalog.SaveChangesAsync();
            await http.SignInAsync(OperatorSetup.Scheme, OperatorSetup.Principal(user));
            return Results.Ok(new Me(user.UserName));
        }).AllowAnonymous().RequireRateLimiting(AuthEndpoints.LoginRateLimit).AddEndpointFilter(OperatorHostOnly);

        var api = app.MapGroup("/api/operator").WithTags("Operator").RequireAuthorization(OperatorSetup.Policy).AddEndpointFilter(OperatorHostOnly);

        api.MapGet("/me", (ClaimsPrincipal user) => new Me(user.Identity!.Name!));

        api.MapPost("/logout", async (HttpContext http) =>
        {
            await http.SignOutAsync(OperatorSetup.Scheme);
            return Results.NoContent();
        });

        api.MapGet("/tenants", async (CatalogDbContext catalog, ClinicReportDatabases databases, TimeProvider clock, CancellationToken ct) =>
        {
            var now = clock.GetUtcNow().UtcDateTime;
            var month = UsageMonth.Key(now);
            var usage = (await catalog.UsageMonths.AsNoTracking().Where(m => m.Month == month).ToListAsync(ct)).ToLookup(m => m.TenantId);
            var tenants = await catalog.Tenants.AsNoTracking().OrderBy(t => t.Slug).ToListAsync(ct);
            return tenants.Select(t =>
            {
                var file = databases.FileFacts(t.Slug);
                int? Used(string kind) => usage[t.Id].FirstOrDefault(m => m.Kind == kind)?.Quantity;
                return new TenantRow(
                    t.Id, t.Slug, t.Name, t.EffectivePlan(now), t.Status, t.CreatedAt,
                    Used(UsageKinds.ActiveUser), Used(UsageKinds.ReportSigned), file.LastWriteUtc, file.Bytes, t.DatabaseFailed,
                    t.DeletedAt, t.DeletedAt is null && t.Gone(now));
            });
        });

        api.MapGet("/events", async (CatalogDbContext catalog, CancellationToken ct) =>
        {
            var events = await catalog.OperatorEvents.AsNoTracking().OrderByDescending(e => e.Id).Take(100).ToListAsync(ct);
            var names = await catalog.Operators.AsNoTracking().ToDictionaryAsync(o => o.Id, o => o.UserName, ct);
            return events.Select(e => new EventRow(e.At, e.OperatorId is { } id ? names.GetValueOrDefault(id) : null, e.Type, e.TenantSlug, e.Detail));
        });

        api.MapPost("/tenants/{id:int}/suspend", (int id, ClaimsPrincipal me, CatalogDbContext catalog, TenantDirectory directory, TimeProvider clock, CancellationToken ct) =>
            ChangeAsync(id, me, catalog, directory, clock, OperatorEventTypes.TenantSuspended, tenant =>
            {
                if (tenant.Status is TenantStatus.Cancelled or TenantStatus.Suspended)
                    throw new ReportRuleException($"This clinic is already {tenant.Status.ToLowerInvariant()}.");
                // No PastDueSince: this is what marks a suspension as the operator's, which paying doesn't undo.
                tenant.Status = TenantStatus.Suspended;
                tenant.PastDueSince = null;
                tenant.PastDueNoticeAt = null;
            }, ct));

        api.MapPost("/tenants/{id:int}/delete", DeleteAsync);

        // For ControlPanel: counts only, and only with the token. Without one set, it isn't there.
        app.MapGet("/healthz/tenants", async (HttpContext http, IConfiguration config, CatalogDbContext catalog, CancellationToken ct) =>
        {
            var token = config["Health:Token"];
            if (string.IsNullOrEmpty(token) || !CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(http.Request.Headers["X-Health-Token"].ToString()), Encoding.UTF8.GetBytes(token)))
                return Results.NotFound();
            var tenants = await catalog.Tenants.AsNoTracking().Where(t => t.DeletedAt == null).Select(t => new { t.Status, t.DatabaseFailed }).ToListAsync(ct);
            return Results.Ok(new
            {
                Tenants = tenants.Count,
                ByStatus = tenants.GroupBy(t => t.Status).ToDictionary(g => g.Key, g => g.Count()),
                DatabaseFailed = tenants.Count(t => t.DatabaseFailed),
            });
        }).AllowAnonymous();

        api.MapPost("/tenants/{id:int}/resume", (int id, ClaimsPrincipal me, CatalogDbContext catalog, TenantDirectory directory, TimeProvider clock, CancellationToken ct) =>
            ChangeAsync(id, me, catalog, directory, clock, OperatorEventTypes.TenantResumed, tenant =>
            {
                if (tenant.Status != TenantStatus.Suspended)
                    throw new ReportRuleException("Only a suspended clinic can be resumed.");
                tenant.Status = tenant.Plan == Plans.Trial ? TenantStatus.Trial : TenantStatus.Active;
                tenant.PastDueSince = null;
                tenant.PastDueNoticeAt = null;
            }, ct));
    }

    /// <summary>Only a clinic past its time to export, and only by hand: the slug has to be typed out.</summary>
    private static async Task<IResult> DeleteAsync(int id, DeleteBody body, ClaimsPrincipal me, CatalogDbContext catalog, ClinicReportDatabases databases,
        TenantDirectory directory, TimeProvider clock, CancellationToken ct)
    {
        var tenant = await catalog.Tenants.Include(t => t.Settings).SingleOrDefaultAsync(t => t.Id == id, ct);
        if (tenant is null || tenant.DeletedAt is not null)
            return Results.NotFound();
        var now = clock.GetUtcNow().UtcDateTime;
        if (!tenant.Gone(now))
            throw new ReportRuleException("This clinic is not past its time to export.");
        if (body.ConfirmSlug != tenant.Slug)
            throw new ReportRuleException("Type the clinic's address to confirm.", StatusCodes.Status400BadRequest);

        // The file first: if this stops half-way, the clinic is still listed and it can be run again.
        directory.Forget(tenant.Slug);
        databases.Delete(tenant.Slug);
        tenant.DeletedAt = now;
        tenant.OwnerEmail = null;
        tenant.EmailTokenHash = null;
        if (tenant.Settings is not null)
            catalog.TenantSettings.Remove(tenant.Settings);
        Log(catalog, now, me.UserId(), OperatorEventTypes.TenantDeleted, tenant.Slug);
        await catalog.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    private static async Task<IResult> ChangeAsync(int id, ClaimsPrincipal me, CatalogDbContext catalog, TenantDirectory directory, TimeProvider clock,
        string eventType, Action<Tenant> change, CancellationToken ct)
    {
        var tenant = await catalog.Tenants.FindAsync([id], ct);
        if (tenant is null || tenant.DeletedAt is not null)
            return Results.NotFound();
        change(tenant);
        Log(catalog, clock.GetUtcNow().UtcDateTime, me.UserId(), eventType, tenant.Slug);
        await catalog.SaveChangesAsync(ct);
        directory.Forget(tenant.Slug);
        return Results.NoContent();
    }

    internal static void Log(CatalogDbContext catalog, DateTime at, int? operatorId, string type, string? slug = null, string? detail = null) =>
        catalog.OperatorEvents.Add(new OperatorEvent { At = at, OperatorId = operatorId, Type = type, TenantSlug = slug, Detail = detail });

    /// <summary>The console answers at its own host only: elsewhere these addresses don't exist.</summary>
    private static async ValueTask<object?> OperatorHostOnly(EndpointFilterInvocationContext ctx, EndpointFilterDelegate next) =>
        ctx.HttpContext.OnOperatorHost() ? await next(ctx) : Results.NotFound();
}
