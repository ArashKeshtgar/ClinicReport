using System.Security.Claims;
using ClinicReport.Api.Auth;
using ClinicReport.Api.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace ClinicReport.Api.Endpoints;

/// <summary>User administration (Admin only).</summary>
public static class UserEndpoints
{
    public record UserDto(int Id, string UserName, string DisplayName, string? Credentials, string Role, bool IsActive);
    public record CreateUser(string UserName, string DisplayName, string? Credentials, string Role, string Password);
    public record UpdateUser(string DisplayName, string? Credentials, string Role, bool IsActive);
    public record ResetPassword(string Password);

    public const int MinPasswordLength = 10;

    public static string? PasswordProblem(string? password) =>
        password is null || password.Length < MinPasswordLength ? $"Use at least {MinPasswordLength} characters."
        : password.Length > 128 ? "Use at most 128 characters."
        : null;

    public static void MapUserEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/users").WithTags("Users").RequireAuthorization(Policies.Admin)
            .AddEndpointFilter(DemoReadOnly);

        api.MapGet("/", async (ClinicReportDbContext db) =>
            await db.Users.AsNoTracking().OrderBy(u => u.DisplayName)
                .Select(u => new UserDto(u.Id, u.UserName, u.DisplayName, u.Credentials, u.Role, u.IsActive))
                .ToListAsync());

        api.MapPost("/", async (CreateUser req, ClinicReportDbContext db, IPasswordHasher<AppUser> hasher, TimeProvider clock) =>
        {
            var errors = new Dictionary<string, string[]>();
            var userName = req.UserName?.Trim() ?? "";
            if (userName.Length is < 3 or > 64 || !userName.All(c => char.IsLetterOrDigit(c) || c is '.' or '_' or '-'))
                errors["userName"] = ["3–64 letters, digits, dots, dashes or underscores."];
            else if (await db.Users.AnyAsync(u => u.UserName == userName))
                errors["userName"] = ["This user name is taken."];
            Validate(req.DisplayName, req.Role, errors);
            if (PasswordProblem(req.Password) is { } p)
                errors["password"] = [p];
            if (errors.Count > 0)
                return Results.ValidationProblem(errors);

            var user = new AppUser
            {
                UserName = userName, DisplayName = req.DisplayName.Trim(), Credentials = Blank(req.Credentials),
                Role = req.Role, CreatedAt = clock.GetUtcNow().UtcDateTime,
            };
            user.PasswordHash = hasher.HashPassword(user, req.Password);
            db.Users.Add(user);
            await db.SaveChangesAsync();
            return Results.Created($"/api/users/{user.Id}", new UserDto(user.Id, user.UserName, user.DisplayName, user.Credentials, user.Role, user.IsActive));
        });

        api.MapPut("/{id:int}", async (int id, UpdateUser req, ClaimsPrincipal me, ClinicReportDbContext db) =>
        {
            var user = await db.Users.FindAsync(id);
            if (user is null)
                return Results.NotFound();
            var errors = new Dictionary<string, string[]>();
            Validate(req.DisplayName, req.Role, errors);
            if (id == me.RequiredUserId() && (!req.IsActive || req.Role != Roles.Admin))
                errors["role"] = ["You cannot remove your own admin access."];
            if (errors.Count > 0)
                return Results.ValidationProblem(errors);

            user.DisplayName = req.DisplayName.Trim();
            user.Credentials = Blank(req.Credentials);
            user.Role = req.Role;
            user.IsActive = req.IsActive;
            await db.SaveChangesAsync();
            return Results.Ok(new UserDto(user.Id, user.UserName, user.DisplayName, user.Credentials, user.Role, user.IsActive));
        });

        api.MapPost("/{id:int}/password", async (int id, ResetPassword req, ClinicReportDbContext db, IPasswordHasher<AppUser> hasher) =>
        {
            var user = await db.Users.FindAsync(id);
            if (user is null)
                return Results.NotFound();
            if (PasswordProblem(req.Password) is { } p)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["password"] = [p] });
            user.PasswordHash = hasher.HashPassword(user, req.Password);
            await db.SaveChangesAsync();
            return Results.NoContent();
        });
    }

    /// <summary>In the public demo anyone is admin, so accounts and passwords stay as seeded.</summary>
    public static async ValueTask<object?> DemoReadOnly(EndpointFilterInvocationContext ctx, EndpointFilterDelegate next)
    {
        var demo = ctx.HttpContext.RequestServices.GetRequiredService<Microsoft.Extensions.Options.IOptions<DemoOptions>>().Value;
        if (demo.Enabled && demo.LockAccounts && !HttpMethods.IsGet(ctx.HttpContext.Request.Method))
            return Results.Problem("User accounts can't be changed in the public demo.", statusCode: StatusCodes.Status403Forbidden);
        return await next(ctx);
    }

    private static void Validate(string? displayName, string? role, Dictionary<string, string[]> errors)
    {
        if (string.IsNullOrWhiteSpace(displayName) || displayName.Trim().Length > 128)
            errors["displayName"] = ["Required, at most 128 characters."];
        if (!Roles.All.Contains(role))
            errors["role"] = [$"One of {string.Join(", ", Roles.All)}."];
    }

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
