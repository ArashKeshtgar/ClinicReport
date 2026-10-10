using System.Security.Claims;
using ClinicReport.Api.Auth;
using ClinicReport.Api.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ClinicReport.Api.Endpoints;

/// <summary>Sign-in, the signed-in user's own account, password and two-factor.</summary>
public static class AuthEndpoints
{
    public const string LoginRateLimit = "login";
    public const int MaxFailedLogins = 5;
    public static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(15);
    private const string TwoFactorPurpose = "ClinicReport.TwoFactorSecret";

    public record LoginRequest(string UserName, string Password, string? Code);
    public record DemoLoginRequest(string UserName);
    public record MeDto(
        int Id, string UserName, string DisplayName, string? Credentials, string[] Roles, string[] Permissions,
        string[]? Services, bool TwoFactorEnabled, bool TwoFactorRequired, string? PendingAction);
    public record ChangePasswordRequest(string CurrentPassword, string NewPassword);
    public record TwoFactorSetup(string Secret, string OtpAuthUri);
    public record CodeBody(string Code);
    public record PasswordBody(string Password);

    public static void MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/auth").WithTags("Auth");

        api.MapPost("/login", async (LoginRequest req, ClinicReportDbContext db, IPasswordHasher<AppUser> hasher,
            IDataProtectionProvider protection, SecurityLog log, TimeProvider clock, HttpContext http) =>
        {
            var now = clock.GetUtcNow();
            var userName = (req.UserName ?? "").Trim();
            var user = await db.Users.Include(u => u.Roles).ThenInclude(ur => ur.Role)
                .SingleOrDefaultAsync(u => u.UserName == userName && u.IsActive);

            if (user?.LockoutEndUtc is { } until && until > now.UtcDateTime)
            {
                await log.WriteAsync(SecurityEventTypes.LoginFailed, null, user.Id, "Account locked");
                return Results.Problem($"Too many failed attempts. Try again after {Math.Ceiling((until - now.UtcDateTime).TotalMinutes)} minutes, or ask an administrator to unlock the account.",
                    statusCode: StatusCodes.Status423Locked);
            }

            // Hash even when the user does not exist so timing does not reveal valid user names.
            var result = hasher.VerifyHashedPassword(user ?? new AppUser(), user?.PasswordHash ?? DummyHash, req.Password ?? "");
            if (user is null || result == PasswordVerificationResult.Failed)
                return await Failed(user, "Wrong password", "Wrong user name or password.");

            if (result == PasswordVerificationResult.SuccessRehashNeeded)
                user.PasswordHash = hasher.HashPassword(user, req.Password!);

            if (user.TwoFactorEnabled)
            {
                if (string.IsNullOrWhiteSpace(req.Code))
                    return Results.Problem("Enter the 6-digit code from your authenticator app.", statusCode: StatusCodes.Status401Unauthorized,
                        extensions: new Dictionary<string, object?> { ["twoFactorRequired"] = true });
                var secret = protection.CreateProtector(TwoFactorPurpose).Unprotect(user.TwoFactorSecret!);
                if (Totp.Verify(secret, req.Code, now, user.TwoFactorLastStep) is not { } step)
                    return await Failed(user, "Wrong two-factor code", "That code is wrong or already used.", twoFactor: true);
                user.TwoFactorLastStep = step;
            }

            user.FailedLoginCount = 0;
            user.LockoutEndUtc = null;
            user.LastLoginAt = now.UtcDateTime;
            log.Add(SecurityEventTypes.LoginSucceeded, user.Id);
            await db.SaveChangesAsync();
            await AuthSetup.SignInAsync(http, user);
            return Results.Ok(ToMe(user));

            async Task<IResult> Failed(AppUser? u, string detail, string message, bool twoFactor = false)
            {
                if (u is not null)
                {
                    u.FailedLoginCount++;
                    if (u.FailedLoginCount >= MaxFailedLogins)
                    {
                        u.FailedLoginCount = 0;
                        u.LockoutEndUtc = now.UtcDateTime + LockoutDuration;
                        log.Add(SecurityEventTypes.LockedOut, u.Id);
                    }
                }
                log.Add(SecurityEventTypes.LoginFailed, u?.Id, detail: u is null ? $"Unknown user name \"{Short(userName)}\"" : detail);
                await db.SaveChangesAsync();
                return Results.Problem(message, statusCode: StatusCodes.Status401Unauthorized,
                    extensions: twoFactor ? new Dictionary<string, object?> { ["twoFactorRequired"] = true } : null);
            }
        }).AllowAnonymous().RequireRateLimiting(LoginRateLimit);

        // Public demo only: one click signs in as a seeded synthetic user. Not available unless Demo:Enabled.
        api.MapPost("/demo-login", async (DemoLoginRequest req, ClinicReportDbContext db, IOptions<DemoOptions> demo, SecurityLog log, HttpContext http) =>
        {
            if (!demo.Value.Enabled)
                return Results.NotFound();
            var user = await db.Users.Include(u => u.Roles).ThenInclude(ur => ur.Role)
                .SingleOrDefaultAsync(u => u.UserName == req.UserName && u.IsActive);
            if (user is null || !DemoSeeder.DemoUserNames.Contains(user.UserName))
                return Results.NotFound();
            user.LastLoginAt = DateTime.UtcNow;
            log.Add(SecurityEventTypes.LoginSucceeded, user.Id, detail: "Demo sign-in");
            await db.SaveChangesAsync();
            await AuthSetup.SignInAsync(http, user);
            return Results.Ok(ToMe(user));
        }).AllowAnonymous().RequireRateLimiting(LoginRateLimit);

        api.MapPost("/logout", async (ClaimsPrincipal principal, SecurityLog log, HttpContext http) =>
        {
            if (principal.UserId() is { } id)
                await log.WriteAsync(SecurityEventTypes.Logout, id);
            await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.NoContent();
        }).AllowAnonymous();

        api.MapGet("/me", async (ClaimsPrincipal principal, ClinicReportDbContext db) =>
            await LoadUser(db, principal.UserId()) is { } user ? Results.Ok(ToMe(user)) : Results.NoContent()).AllowAnonymous();

        var account = api.MapGroup("").AddEndpointFilter(UserEndpoints.DemoReadOnly);

        account.MapPost("/password", async (ChangePasswordRequest req, ClaimsPrincipal principal, ClinicReportDbContext db,
            IPasswordHasher<AppUser> hasher, SecurityLog log, HttpContext http) =>
        {
            var user = (await LoadUser(db, principal.RequiredUserId(), tracked: true))!;
            if (hasher.VerifyHashedPassword(user, user.PasswordHash, req.CurrentPassword ?? "") == PasswordVerificationResult.Failed)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["currentPassword"] = ["Current password is wrong."] });
            if (UserEndpoints.PasswordProblem(req.NewPassword, user.UserName) is { } problem)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["newPassword"] = [problem] });
            if (hasher.VerifyHashedPassword(user, user.PasswordHash, req.NewPassword) != PasswordVerificationResult.Failed)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["newPassword"] = ["Choose a password different from the current one."] });

            user.PasswordHash = hasher.HashPassword(user, req.NewPassword);
            user.MustChangePassword = false;
            user.SecurityStamp = NewStamp();
            log.Add(SecurityEventTypes.PasswordChanged, user.Id);
            await db.SaveChangesAsync();
            // Other sessions end; this one continues with the new stamp.
            await AuthSetup.SignInAsync(http, user);
            return Results.Ok(ToMe(user));
        }).RequireRateLimiting(LoginRateLimit);

        account.MapPost("/two-factor/setup", async (ClaimsPrincipal principal, ClinicReportDbContext db,
            IDataProtectionProvider protection, IOptions<ClinicOptions> clinic) =>
        {
            var user = (await LoadUser(db, principal.RequiredUserId(), tracked: true))!;
            if (user.TwoFactorEnabled)
                return Results.Problem("Two-factor sign-in is already on. Turn it off first to use a new device.", statusCode: StatusCodes.Status409Conflict);
            var secret = Totp.NewSecret();
            user.TwoFactorSecret = protection.CreateProtector(TwoFactorPurpose).Protect(secret);
            await db.SaveChangesAsync();
            return Results.Ok(new TwoFactorSetup(secret, Totp.OtpAuthUri(clinic.Value.Name, user.UserName, secret)));
        });

        account.MapPost("/two-factor/enable", async (CodeBody body, ClaimsPrincipal principal, ClinicReportDbContext db,
            IDataProtectionProvider protection, SecurityLog log, TimeProvider clock) =>
        {
            var user = (await LoadUser(db, principal.RequiredUserId(), tracked: true))!;
            if (user.TwoFactorEnabled || user.TwoFactorSecret is null)
                return Results.Problem("Start the setup first.", statusCode: StatusCodes.Status409Conflict);
            var secret = protection.CreateProtector(TwoFactorPurpose).Unprotect(user.TwoFactorSecret);
            if (Totp.Verify(secret, body.Code, clock.GetUtcNow(), user.TwoFactorLastStep) is not { } step)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["code"] = ["That code doesn't match. Check the phone's clock and try the newest code."] });
            user.TwoFactorEnabled = true;
            user.TwoFactorLastStep = step;
            log.Add(SecurityEventTypes.TwoFactorEnabled, user.Id);
            await db.SaveChangesAsync();
            return Results.Ok(ToMe(user));
        });

        account.MapPost("/two-factor/disable", async (PasswordBody body, ClaimsPrincipal principal, ClinicReportDbContext db,
            IPasswordHasher<AppUser> hasher, SecurityLog log) =>
        {
            var user = (await LoadUser(db, principal.RequiredUserId(), tracked: true))!;
            if (Access.For(user).TwoFactorRequired)
                return Results.Problem("One of your roles requires two-factor sign-in.", statusCode: StatusCodes.Status409Conflict);
            if (hasher.VerifyHashedPassword(user, user.PasswordHash, body.Password ?? "") == PasswordVerificationResult.Failed)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["password"] = ["Password is wrong."] });
            user.TwoFactorEnabled = false;
            user.TwoFactorSecret = null;
            log.Add(SecurityEventTypes.TwoFactorDisabled, user.Id);
            await db.SaveChangesAsync();
            return Results.Ok(ToMe(user));
        }).RequireRateLimiting(LoginRateLimit);

        // Ends every other session of this user (a lost laptop, a shared computer).
        account.MapPost("/sessions/revoke", async (ClaimsPrincipal principal, ClinicReportDbContext db, SecurityLog log, HttpContext http) =>
        {
            var user = (await LoadUser(db, principal.RequiredUserId(), tracked: true))!;
            user.SecurityStamp = NewStamp();
            log.Add(SecurityEventTypes.SessionsRevoked, user.Id);
            await db.SaveChangesAsync();
            await AuthSetup.SignInAsync(http, user);
            return Results.NoContent();
        });
    }

    public static async Task<AppUser?> LoadUser(ClinicReportDbContext db, int? id, bool tracked = false)
    {
        if (id is null)
            return null;
        var query = db.Users.Include(u => u.Roles).ThenInclude(ur => ur.Role).Where(u => u.Id == id && u.IsActive);
        return await (tracked ? query : query.AsNoTracking()).SingleOrDefaultAsync();
    }

    public static MeDto ToMe(AppUser u)
    {
        var access = Access.For(u);
        return new MeDto(u.Id, u.UserName, u.DisplayName, u.Credentials, access.Roles, access.Permissions,
            access.Services, u.TwoFactorEnabled, access.TwoFactorRequired, access.Pending);
    }

    public static string NewStamp() => Guid.NewGuid().ToString("N");

    private static string Short(string s) => s.Length > 40 ? s[..40] : s;

    private static readonly string DummyHash = new PasswordHasher<AppUser>().HashPassword(new AppUser(), Guid.NewGuid().ToString());
}
