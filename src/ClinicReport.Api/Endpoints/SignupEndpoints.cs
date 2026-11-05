using System.Collections.Frozen;
using System.Net.Mail;
using ClinicReport.Api.Auth;
using Microsoft.Extensions.Options;
using ClinicReport.Api.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace ClinicReport.Api.Endpoints;

/// <summary>
/// Hosted mode: a clinic signs itself up on the root domain and gets a tenant, its own database and an administrator.
/// The steps are in two databases, so there is no one transaction: a failure after the tenant row is saved removes the
/// row and the database file again, and the same address can be tried once more.
/// </summary>
public static class SignupEndpoints
{
    public const string SignupRateLimit = "signup";
    public static readonly TimeSpan TrialLength = TimeSpan.FromDays(14);

    /// <summary>Subdomains that are, or may become, the service's own.</summary>
    public static readonly FrozenSet<string> ReservedSlugs = new[]
    {
        "www", "api", "app", "admin", "administrator", "operator", "console", "mail", "email", "smtp", "imap", "pop", "mx",
        "ns", "ns1", "ns2", "dns", "ftp", "cdn", "static", "assets", "files", "status", "health", "help", "support", "docs",
        "blog", "news", "about", "signup", "login", "signin", "auth", "sso", "account", "accounts", "billing", "pay",
        "stripe", "dashboard", "demo", "test", "dev", "staging", "beta", "internal", "root", "system", "clinicreport",
        "clinichub", "controlpanel",
    }.ToFrozenSet();

    public record SignupRequest(string? ClinicName, string? Slug, string? AdminName, string? Email, string? Password);
    /// <param name="EmailSent">False when the confirmation could not be sent; the administrator can send it again after signing in.</param>
    public record SignupResult(string Slug, string Url, DateTime TrialEndsAt, bool EmailSent);
    public record VerifyEmailBody(string? Token);
    public record SlugCheck(string Slug, bool Available, string? Problem);

    /// <summary>Why a slug cannot be a clinic's address, or null when it can (if it is free).</summary>
    public static string? SlugProblem(string? slug) =>
        !ClinicReportDatabases.IsValidSlug(slug) ? "Use 2 to 40 lower-case letters, digits and dashes."
        : slug!.StartsWith('-') || slug.EndsWith('-') || slug.Contains("--") ? "Start and end with a letter or digit, with single dashes between."
        : ReservedSlugs.Contains(slug) ? "That address is reserved."
        : null;

    /// <summary>The administrator signs in with their email address, so it has to fit a user name.</summary>
    public static string? EmailProblem(string? email) =>
        email is null || !MailAddress.TryCreate(email, out var parsed) || parsed.Address != email || !parsed.Host.Contains('.')
            ? "Enter an email address."
        : email.Length > 64 ? "Use an email address of at most 64 characters."
        : null;

    public static void MapSignupEndpoints(this IEndpointRouteBuilder app)
    {
        if (!app.ServiceProvider.IsHosted())
            return;
        var api = app.MapGroup("/api/signup").WithTags("Signup").AllowAnonymous().AddEndpointFilter(RootDomainOnly);

        api.MapGet("/slug", async (string? value, CatalogDbContext catalog, ClinicReportDatabases databases, CancellationToken ct) =>
        {
            var slug = (value ?? "").Trim().ToLowerInvariant();
            var problem = SlugProblem(slug) ?? (await IsTakenAsync(slug, catalog, databases, ct) ? "That address is taken." : null);
            return new SlugCheck(slug, problem is null, problem);
        });

        api.MapPost("/", async (SignupRequest req, HttpContext http, CatalogDbContext catalog, ClinicReportDatabases databases,
            TenantDirectory directory, IPasswordHasher<AppUser> hasher, IMailSender mail, IOptions<HostingOptions> hosting, TimeProvider clock,
            ILoggerFactory logs, CancellationToken ct) =>
        {
            var name = (req.ClinicName ?? "").Trim();
            var slug = (req.Slug ?? "").Trim().ToLowerInvariant();
            var adminName = (req.AdminName ?? "").Trim();
            var email = (req.Email ?? "").Trim().ToLowerInvariant();

            var errors = new Dictionary<string, string[]>();
            if (name.Length is < 2 or > 200)
                errors["clinicName"] = ["Enter the clinic's name (2 to 200 characters)."];
            if (SlugProblem(slug) is { } slugProblem)
                errors["slug"] = [slugProblem];
            if (adminName.Length is < 2 or > 128)
                errors["adminName"] = ["Enter your name."];
            if (EmailProblem(email) is { } emailProblem)
                errors["email"] = [emailProblem];
            if (UserEndpoints.PasswordProblem(req.Password, email) is { } passwordProblem)
                errors["password"] = [passwordProblem];
            if (errors.Count > 0)
                return Results.ValidationProblem(errors);

            if (await IsTakenAsync(slug, catalog, databases, ct))
                return Taken();
            if (hosting.Value.MaxTenants is { } most && await catalog.Tenants.CountAsync(t => t.DeletedAt == null, ct) >= most)
                return Results.Problem("This service isn't taking new clinics at the moment.", statusCode: StatusCodes.Status503ServiceUnavailable);

            var now = clock.GetUtcNow().UtcDateTime;
            var tenant = new Tenant
            {
                Slug = slug, Name = name, Status = TenantStatus.Trial, Plan = Plans.Trial, TrialEndsAt = now + TrialLength,
                CreatedAt = now, Settings = new TenantSettings(), OwnerEmail = email,
            };
            var verify = hosting.Value.VerifyEmail;
            var token = verify ? EmailVerification.NewToken(tenant, now) : null;
            if (!verify)
                tenant.EmailVerifiedAt = now;
            catalog.Tenants.Add(tenant);
            try
            {
                await catalog.SaveChangesAsync(ct);
            }
            catch (DbUpdateException)
            {
                // Another sign-up took the address between the check and here (the unique index on Slug).
                return Taken();
            }

            try
            {
                // Migrates and seeds roles and the bundled templates, like any tenant's first request.
                await using var db = await databases.OpenAsync(slug);
                var admin = new AppUser
                {
                    UserName = email, DisplayName = adminName, CreatedAt = now,
                    Roles = [new UserRole { Role = await RoleSeeder.GetAsync(db, SystemRoles.Administrator) }],
                };
                admin.PasswordHash = hasher.HashPassword(admin, req.Password!);
                db.Users.Add(admin);
                await db.SaveChangesAsync(CancellationToken.None);
            }
            catch (Exception e)
            {
                logs.CreateLogger("Signup").LogError(e, "Sign-up of tenant {Slug} failed; nothing was kept.", slug);
                catalog.Tenants.Remove(tenant);
                await catalog.SaveChangesAsync(CancellationToken.None);
                databases.Delete(slug);
                directory.Forget(slug);
                return Results.Problem("The clinic could not be created. Nothing was kept; please try again.",
                    statusCode: StatusCodes.Status500InternalServerError);
            }

            // A request to the address before now found no tenant, which is never cached; this is for whatever else is.
            directory.Forget(slug);
            var url = $"{http.Request.Scheme}://{slug}.{http.Request.Host}/";
            var sent = token is not null && await EmailVerification.SendAsync(tenant, token, url, mail, logs.CreateLogger("Signup"), CancellationToken.None);
            return Results.Created(url, new SignupResult(slug, url, tenant.TrialEndsAt!.Value, sent));
        }).RequireRateLimiting(SignupRateLimit);

        // At the clinic's own address: the link from the email. Under /api/auth, so it works before signing in and while suspended.
        var verify = app.MapGroup("/api/auth/verify-email").WithTags("Signup").AddEndpointFilter(TenantOnly);

        verify.MapPost("/", async (VerifyEmailBody body, ITenantContext current, CatalogDbContext catalog, TenantDirectory directory,
            TimeProvider clock, CancellationToken ct) =>
        {
            var tenant = await catalog.Tenants.SingleAsync(t => t.Id == current.Tenant!.Id, ct);
            if (!tenant.AwaitingEmailVerification)
                return Results.Ok(new { verified = true });
            if (!EmailVerification.TryConfirm(tenant, body.Token, clock.GetUtcNow().UtcDateTime))
                return Results.Problem("This link is wrong, used or expired. Sign in and send a new one.", statusCode: StatusCodes.Status400BadRequest);
            await catalog.SaveChangesAsync(ct);
            directory.Forget(tenant.Slug);
            return Results.Ok(new { verified = true });
        }).AllowAnonymous().RequireRateLimiting(AuthEndpoints.LoginRateLimit);

        // A new link (the old one stops working), for the clinic's administrators.
        verify.MapPost("/resend", async (HttpContext http, ITenantContext current, CatalogDbContext catalog, IMailSender mail,
            TimeProvider clock, ILoggerFactory logs, CancellationToken ct) =>
        {
            var tenant = await catalog.Tenants.SingleAsync(t => t.Id == current.Tenant!.Id, ct);
            if (!tenant.AwaitingEmailVerification)
                return Results.Ok(new { sent = false, verified = true });
            var token = EmailVerification.NewToken(tenant, clock.GetUtcNow().UtcDateTime);
            await catalog.SaveChangesAsync(ct);
            var url = $"{http.Request.Scheme}://{http.Request.Host}/";
            var sent = await EmailVerification.SendAsync(tenant, token, url, mail, logs.CreateLogger("Signup"), CancellationToken.None);
            return Results.Ok(new { sent, verified = false });
        }).RequireAuthorization(Permissions.UsersManage).RequireRateLimiting(SignupRateLimit);
    }

    /// <summary>
    /// Taken by a tenant, or by a database file without one: that file is somebody's data (a tenant removed from the
    /// catalog by hand, say), never to be handed to a new clinic.
    /// </summary>
    private static async Task<bool> IsTakenAsync(string slug, CatalogDbContext catalog, ClinicReportDatabases databases, CancellationToken ct) =>
        await catalog.Tenants.AnyAsync(t => t.Slug == slug, ct) || databases.Exists(slug);

    private static IResult Taken() => Results.Problem("That address is taken.", statusCode: StatusCodes.Status409Conflict);

    private static async ValueTask<object?> TenantOnly(EndpointFilterInvocationContext ctx, EndpointFilterDelegate next) =>
        ctx.HttpContext.RequestServices.GetRequiredService<ITenantContext>().Tenant is null ? Results.NotFound() : await next(ctx);

    /// <summary>Sign-up is the service's, on the root domain; a tenant's subdomain has none.</summary>
    private static async ValueTask<object?> RootDomainOnly(EndpointFilterInvocationContext ctx, EndpointFilterDelegate next) =>
        ctx.HttpContext.RequestServices.GetRequiredService<ITenantContext>().Tenant is null ? await next(ctx) : Results.NotFound();
}
