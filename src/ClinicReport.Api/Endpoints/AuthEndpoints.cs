using System.Security.Claims;
using ClinicReport.Api.Auth;
using ClinicReport.Api.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ClinicReport.Api.Endpoints;

public static class AuthEndpoints
{
    public const string LoginRateLimit = "login";

    public record LoginRequest(string UserName, string Password);
    public record DemoLoginRequest(string UserName);
    public record MeDto(int Id, string UserName, string DisplayName, string? Credentials, string Role);
    public record ChangePasswordRequest(string CurrentPassword, string NewPassword);

    public static void MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/auth").WithTags("Auth");

        api.MapPost("/login", async (LoginRequest req, ClinicReportDbContext db, IPasswordHasher<AppUser> hasher, HttpContext http) =>
        {
            var user = await db.Users.SingleOrDefaultAsync(u => u.UserName == (req.UserName ?? "").Trim() && u.IsActive);
            // Hash even when the user does not exist so timing does not reveal valid user names.
            var result = hasher.VerifyHashedPassword(user ?? new AppUser(), user?.PasswordHash ?? DummyHash, req.Password ?? "");
            if (user is null || result == PasswordVerificationResult.Failed)
                return Results.Problem("Wrong user name or password.", statusCode: StatusCodes.Status401Unauthorized);
            if (result == PasswordVerificationResult.SuccessRehashNeeded)
            {
                user.PasswordHash = hasher.HashPassword(user, req.Password!);
                await db.SaveChangesAsync();
            }
            await http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, AuthSetup.CreatePrincipal(user));
            return Results.Ok(ToMe(user));
        }).AllowAnonymous().RequireRateLimiting(LoginRateLimit);

        // Public demo only: one click signs in as a seeded synthetic user. Not mapped unless Demo:Enabled.
        api.MapPost("/demo-login", async (DemoLoginRequest req, ClinicReportDbContext db, IOptions<DemoOptions> demo, HttpContext http) =>
        {
            if (!demo.Value.Enabled)
                return Results.NotFound();
            var user = await db.Users.SingleOrDefaultAsync(u => u.UserName == req.UserName && u.IsActive);
            if (user is null || !DemoSeeder.DemoUserNames.Contains(user.UserName))
                return Results.NotFound();
            await http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, AuthSetup.CreatePrincipal(user));
            return Results.Ok(ToMe(user));
        }).AllowAnonymous().RequireRateLimiting(LoginRateLimit);

        api.MapPost("/logout", async (HttpContext http) =>
        {
            await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.NoContent();
        }).AllowAnonymous();

        api.MapGet("/me", async (ClaimsPrincipal principal, ClinicReportDbContext db) =>
        {
            var id = principal.UserId();
            var user = id is null ? null : await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == id);
            return user is null ? Results.NoContent() : Results.Ok(ToMe(user));
        }).AllowAnonymous();

        api.MapPost("/password", async (ChangePasswordRequest req, ClaimsPrincipal principal, ClinicReportDbContext db, IPasswordHasher<AppUser> hasher) =>
        {
            var user = await db.Users.SingleAsync(u => u.Id == principal.RequiredUserId());
            if (hasher.VerifyHashedPassword(user, user.PasswordHash, req.CurrentPassword ?? "") == PasswordVerificationResult.Failed)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["currentPassword"] = ["Current password is wrong."] });
            if (UserEndpoints.PasswordProblem(req.NewPassword) is { } problem)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["newPassword"] = [problem] });
            user.PasswordHash = hasher.HashPassword(user, req.NewPassword);
            await db.SaveChangesAsync();
            return Results.NoContent();
        }).RequireRateLimiting(LoginRateLimit);
    }

    public static MeDto ToMe(AppUser u) => new(u.Id, u.UserName, u.DisplayName, u.Credentials, u.Role);

    private static readonly string DummyHash = new PasswordHasher<AppUser>().HashPassword(new AppUser(), Guid.NewGuid().ToString());
}
