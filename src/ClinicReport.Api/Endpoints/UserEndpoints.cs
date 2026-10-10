using System.Security.Claims;
using ClinicReport.Api.Auth;
using ClinicReport.Api.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ClinicReport.Api.Endpoints;

/// <summary>User and role administration (permission users.manage) and the audit log (audit.view).</summary>
public static class UserEndpoints
{
    public record UserDto(
        int Id, string UserName, string DisplayName, string? Credentials, bool IsActive,
        int[] RoleIds, string[] Roles, string[] Services, string[] Permissions,
        bool TwoFactorEnabled, bool TwoFactorRequired, bool MustChangePassword, bool IsLockedOut,
        DateTime? LastLoginAt, DateTime CreatedAt);

    public record CreateUser(string UserName, string DisplayName, string? Credentials, int[] RoleIds, string[]? Services, string Password);
    public record UpdateUser(string DisplayName, string? Credentials, int[] RoleIds, string[]? Services, bool IsActive);
    public record ResetPassword(string Password);

    public record RoleDto(int Id, string Name, string? Description, string[] Permissions, bool RequireTwoFactor, bool IsSystem, int UserCount);
    public record SaveRole(string Name, string? Description, string[] Permissions, bool RequireTwoFactor);

    public record AuditRow(
        long Id, DateTime At, string Type, string? Actor, string? Target, int? ReportId, int? PatientId, string? Patient, string? Detail, string? IpAddress);

    public const int MinPasswordLength = 10;

    public static string? PasswordProblem(string? password, string? userName = null) =>
        password is null || password.Length < MinPasswordLength ? $"Use at least {MinPasswordLength} characters."
        : password.Length > 128 ? "Use at most 128 characters."
        : password.Distinct().Count() < 4 ? "Use more different characters."
        : userName is { Length: >= 3 } && password.Contains(userName, StringComparison.OrdinalIgnoreCase) ? "Don't include your user name."
        : null;

    public static void MapUserEndpoints(this IEndpointRouteBuilder app)
    {
        var admin = app.MapGroup("/api").WithTags("Users").RequireAuthorization(Permissions.UsersManage).AddEndpointFilter(DemoReadOnly);

        admin.MapGet("/permissions", () => Permissions.Catalog);

        // Service names the templates use, for limiting a user to some services.
        admin.MapGet("/services", async (ClinicReportDbContext db) =>
            await db.Templates.AsNoTracking().Select(t => t.Service).Distinct().OrderBy(s => s).ToListAsync());

        admin.MapGet("/users", async (ClinicReportDbContext db, TimeProvider clock) =>
        {
            var now = clock.GetUtcNow().UtcDateTime;
            var users = await db.Users.AsNoTracking().Include(u => u.Roles).ThenInclude(ur => ur.Role).OrderBy(u => u.DisplayName).ToListAsync();
            return users.Select(u => ToDto(u, now));
        });

        admin.MapPost("/users", async (CreateUser req, ClaimsPrincipal me, ClinicReportDbContext db, IPasswordHasher<AppUser> hasher, SecurityLog log, TimeProvider clock) =>
        {
            var errors = new Dictionary<string, string[]>();
            var userName = req.UserName?.Trim() ?? "";
            if (userName.Length is < 3 or > 64 || !userName.All(c => char.IsLetterOrDigit(c) || c is '.' or '_' or '-'))
                errors["userName"] = ["3–64 letters, digits, dots, dashes or underscores."];
            else if (await db.Users.AnyAsync(u => u.UserName == userName))
                errors["userName"] = ["This user name is taken."];
            var roles = await ValidateCommon(db, req.DisplayName, req.RoleIds, req.Services, errors);
            if (PasswordProblem(req.Password, userName) is { } p)
                errors["password"] = [p];
            if (errors.Count > 0)
                return Results.ValidationProblem(errors);

            var now = clock.GetUtcNow().UtcDateTime;
            var user = new AppUser
            {
                UserName = userName, DisplayName = req.DisplayName.Trim(), Credentials = Blank(req.Credentials),
                Services = FormatServices(req.Services), CreatedAt = now,
                // The admin knows this password; the user replaces it at first sign-in.
                MustChangePassword = true,
                Roles = roles.Select(r => new UserRole { RoleId = r.Id }).ToList(),
            };
            user.PasswordHash = hasher.HashPassword(user, req.Password);
            db.Users.Add(user);
            await db.SaveChangesAsync();
            await log.WriteAsync(SecurityEventTypes.UserCreated, me.RequiredUserId(), user.Id,
                $"Roles: {string.Join(", ", roles.Select(r => r.Name))}; services: {ServicesText(req.Services)}");
            var created = await LoadAsync(db, user.Id);
            return Results.Created($"/api/users/{user.Id}", ToDto(created!, now));
        });

        admin.MapPut("/users/{id:int}", async (int id, UpdateUser req, ClaimsPrincipal me, ClinicReportDbContext db, SecurityLog log, TimeProvider clock) =>
        {
            var user = await LoadAsync(db, id, tracked: true);
            if (user is null)
                return Results.NotFound();
            var errors = new Dictionary<string, string[]>();
            var roles = await ValidateCommon(db, req.DisplayName, req.RoleIds, req.Services, errors);
            if (errors.Count > 0)
                return Results.ValidationProblem(errors);

            var before = Describe(user);
            var accessChanged = user.IsActive != req.IsActive
                || !user.Roles.Select(r => r.RoleId).Order().SequenceEqual(roles.Select(r => r.Id).Order());
            user.DisplayName = req.DisplayName.Trim();
            user.Credentials = Blank(req.Credentials);
            user.Services = FormatServices(req.Services);
            user.IsActive = req.IsActive;
            user.Roles.RemoveAll(ur => roles.All(r => r.Id != ur.RoleId));
            foreach (var r in roles.Where(r => user.Roles.All(ur => ur.RoleId != r.Id)))
                user.Roles.Add(new UserRole { UserId = user.Id, RoleId = r.Id, Role = r });

            if (await LastAdminProblem(db, user) is { } problem)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["roleIds"] = [problem] });
            if (!user.IsActive)
                user.SecurityStamp = AuthEndpoints.NewStamp();

            log.Add(SecurityEventTypes.UserUpdated, me.RequiredUserId(), user.Id, $"{before} → {Describe(user)}");
            await db.SaveChangesAsync();
            return Results.Ok(ToDto((await LoadAsync(db, id))!, clock.GetUtcNow().UtcDateTime));
        });

        admin.MapPost("/users/{id:int}/password", async (int id, ResetPassword req, ClaimsPrincipal me, ClinicReportDbContext db,
            IPasswordHasher<AppUser> hasher, SecurityLog log) =>
        {
            var user = await db.Users.FindAsync(id);
            if (user is null)
                return Results.NotFound();
            if (PasswordProblem(req.Password, user.UserName) is { } p)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["password"] = [p] });
            user.PasswordHash = hasher.HashPassword(user, req.Password);
            user.MustChangePassword = true;
            user.FailedLoginCount = 0;
            user.LockoutEndUtc = null;
            user.SecurityStamp = AuthEndpoints.NewStamp();
            log.Add(SecurityEventTypes.PasswordReset, me.RequiredUserId(), id);
            await db.SaveChangesAsync();
            return Results.NoContent();
        });

        admin.MapPost("/users/{id:int}/unlock", async (int id, ClaimsPrincipal me, ClinicReportDbContext db, SecurityLog log) =>
        {
            var user = await db.Users.FindAsync(id);
            if (user is null)
                return Results.NotFound();
            user.FailedLoginCount = 0;
            user.LockoutEndUtc = null;
            log.Add(SecurityEventTypes.UserUnlocked, me.RequiredUserId(), id);
            await db.SaveChangesAsync();
            return Results.NoContent();
        });

        // For a lost phone: the user sets two-factor up again at next sign-in if a role requires it.
        admin.MapPost("/users/{id:int}/two-factor/reset", async (int id, ClaimsPrincipal me, ClinicReportDbContext db, SecurityLog log) =>
        {
            var user = await db.Users.FindAsync(id);
            if (user is null)
                return Results.NotFound();
            user.TwoFactorEnabled = false;
            user.TwoFactorSecret = null;
            user.SecurityStamp = AuthEndpoints.NewStamp();
            log.Add(SecurityEventTypes.TwoFactorReset, me.RequiredUserId(), id);
            await db.SaveChangesAsync();
            return Results.NoContent();
        });

        admin.MapPost("/users/{id:int}/sessions/revoke", async (int id, ClaimsPrincipal me, ClinicReportDbContext db, SecurityLog log) =>
        {
            var user = await db.Users.FindAsync(id);
            if (user is null)
                return Results.NotFound();
            user.SecurityStamp = AuthEndpoints.NewStamp();
            log.Add(SecurityEventTypes.SessionsRevoked, me.RequiredUserId(), id);
            await db.SaveChangesAsync();
            return Results.NoContent();
        });

        // ---- roles ----

        admin.MapGet("/roles", async (ClinicReportDbContext db) =>
            (await db.Roles.AsNoTracking().Include(r => r.Users).OrderByDescending(r => r.IsSystem).ThenBy(r => r.Name).ToListAsync())
                .Select(ToDto));

        admin.MapPost("/roles", async (SaveRole req, ClaimsPrincipal me, ClinicReportDbContext db, SecurityLog log) =>
        {
            if (await ValidateRole(db, req, null) is { } problem)
                return problem;
            var role = new Role { Name = req.Name.Trim(), Description = Blank(req.Description), Permissions = Permissions.Format(req.Permissions), RequireTwoFactor = req.RequireTwoFactor };
            db.Roles.Add(role);
            await db.SaveChangesAsync();
            await log.WriteAsync(SecurityEventTypes.RoleCreated, me.RequiredUserId(), detail: $"{role.Name}: {role.Permissions}");
            return Results.Created($"/api/roles/{role.Id}", ToDto(role));
        });

        admin.MapPut("/roles/{id:int}", async (int id, SaveRole req, ClaimsPrincipal me, ClinicReportDbContext db, SecurityLog log) =>
        {
            var role = await db.Roles.Include(r => r.Users).SingleOrDefaultAsync(r => r.Id == id);
            if (role is null)
                return Results.NotFound();
            if (await ValidateRole(db, req, role) is { } problem)
                return problem;
            var isAdministrator = role.IsSystem && role.Name == SystemRoles.Administrator;
            var before = $"{role.Name}: {role.Permissions}{(role.RequireTwoFactor ? " +2FA" : "")}";
            if (!role.IsSystem)
                role.Name = req.Name.Trim();
            role.Description = Blank(req.Description);
            role.Permissions = isAdministrator ? Permissions.Format(Permissions.All) : Permissions.Format(req.Permissions);
            role.RequireTwoFactor = req.RequireTwoFactor;
            if (await LastAdminProblem(db, null, role) is { } last)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["permissions"] = [last] });
            log.Add(SecurityEventTypes.RoleUpdated, me.RequiredUserId(),
                detail: $"{before} → {role.Name}: {role.Permissions}{(role.RequireTwoFactor ? " +2FA" : "")}");
            await db.SaveChangesAsync();
            return Results.Ok(ToDto(role));
        });

        admin.MapDelete("/roles/{id:int}", async (int id, ClaimsPrincipal me, ClinicReportDbContext db, SecurityLog log) =>
        {
            var role = await db.Roles.Include(r => r.Users).SingleOrDefaultAsync(r => r.Id == id);
            if (role is null)
                return Results.NotFound();
            if (role.IsSystem)
                return Results.Problem("Built-in roles can't be deleted.", statusCode: StatusCodes.Status409Conflict);
            if (role.Users.Count > 0)
                return Results.Problem($"{role.Users.Count} user(s) still have this role.", statusCode: StatusCodes.Status409Conflict);
            db.Roles.Remove(role);
            log.Add(SecurityEventTypes.RoleDeleted, me.RequiredUserId(), detail: role.Name);
            await db.SaveChangesAsync();
            return Results.NoContent();
        });

        // ---- audit log ----

        app.MapGet("/api/audit", async (string? type, int? userId, int? reportId, DateOnly? from, DateOnly? to, long? before, ClinicReportDbContext db) =>
        {
            var q = db.SecurityEvents.AsNoTracking();
            if (!string.IsNullOrEmpty(type))
                q = q.Where(e => e.Type == type);
            if (userId is not null)
                q = q.Where(e => e.ActorUserId == userId || e.TargetUserId == userId);
            if (reportId is not null)
                q = q.Where(e => e.ReportId == reportId);
            if (from is not null)
                q = q.Where(e => e.At >= from.Value.ToDateTime(TimeOnly.MinValue));
            if (to is not null)
                q = q.Where(e => e.At < to.Value.AddDays(1).ToDateTime(TimeOnly.MinValue));
            if (before is not null)
                q = q.Where(e => e.Id < before);
            return await q.OrderByDescending(e => e.Id).Take(200)
                .Select(e => new AuditRow(e.Id, e.At, e.Type,
                    db.Users.Where(u => u.Id == e.ActorUserId).Select(u => u.DisplayName).FirstOrDefault(),
                    db.Users.Where(u => u.Id == e.TargetUserId).Select(u => u.DisplayName).FirstOrDefault(),
                    e.ReportId, e.PatientId,
                    db.Patients.Where(p => p.Id == e.PatientId).Select(p => p.LastName + ", " + p.FirstName + " (" + p.Mrn + ")").FirstOrDefault(),
                    e.Detail, e.IpAddress))
                .ToListAsync();
        }).WithTags("Audit").RequireAuthorization(Permissions.AuditView);
    }

    /// <summary>In the public demo every visitor can be an administrator, so accounts, roles and passwords stay as seeded.</summary>
    public static async ValueTask<object?> DemoReadOnly(EndpointFilterInvocationContext ctx, EndpointFilterDelegate next)
    {
        var demo = ctx.HttpContext.RequestServices.GetRequiredService<IOptions<DemoOptions>>().Value;
        if (demo.Enabled && demo.LockAccounts && !HttpMethods.IsGet(ctx.HttpContext.Request.Method))
            return Results.Problem("Accounts, roles and passwords can't be changed in the public demo.", statusCode: StatusCodes.Status403Forbidden);
        return await next(ctx);
    }

    private static async Task<AppUser?> LoadAsync(ClinicReportDbContext db, int id, bool tracked = false)
    {
        var q = db.Users.Include(u => u.Roles).ThenInclude(ur => ur.Role).Where(u => u.Id == id);
        return await (tracked ? q : q.AsNoTracking()).SingleOrDefaultAsync();
    }

    private static UserDto ToDto(AppUser u, DateTime now)
    {
        var access = Access.For(u);
        return new UserDto(u.Id, u.UserName, u.DisplayName, u.Credentials, u.IsActive,
            u.Roles.Select(r => r.RoleId).ToArray(), access.Roles, u.ServiceList().ToArray(), access.Permissions,
            u.TwoFactorEnabled, access.TwoFactorRequired, u.MustChangePassword, u.LockoutEndUtc > now, u.LastLoginAt, u.CreatedAt);
    }

    private static RoleDto ToDto(Role r) =>
        new(r.Id, r.Name, r.Description,
            r.IsSystem && r.Name == SystemRoles.Administrator ? Permissions.All : Permissions.Parse(r.Permissions),
            r.RequireTwoFactor, r.IsSystem, r.Users.Count);

    private static async Task<List<Role>> ValidateCommon(ClinicReportDbContext db, string? displayName, int[]? roleIds, string[]? services, Dictionary<string, string[]> errors)
    {
        if (string.IsNullOrWhiteSpace(displayName) || displayName.Trim().Length > 128)
            errors["displayName"] = ["Required, at most 128 characters."];
        var ids = (roleIds ?? []).Distinct().ToArray();
        var roles = await db.Roles.Where(r => ids.Contains(r.Id)).ToListAsync();
        if (ids.Length == 0)
            errors["roleIds"] = ["Give the user at least one role."];
        else if (roles.Count != ids.Length)
            errors["roleIds"] = ["Unknown role."];
        if ((services ?? []).Any(s => string.IsNullOrWhiteSpace(s) || s.Length > 64 || s.Contains('\n')))
            errors["services"] = ["Invalid service name."];
        return roles;
    }

    private static async Task<IResult?> ValidateRole(ClinicReportDbContext db, SaveRole req, Role? existing)
    {
        var errors = new Dictionary<string, string[]>();
        var name = req.Name?.Trim() ?? "";
        if (existing is not { IsSystem: true })
        {
            if (name.Length is < 2 or > 64)
                errors["name"] = ["2–64 characters."];
            else if (await db.Roles.AnyAsync(r => r.Name == name && r.Id != (existing == null ? 0 : existing.Id)))
                errors["name"] = ["Another role has this name."];
        }
        if ((req.Description?.Length ?? 0) > 300)
            errors["description"] = ["At most 300 characters."];
        if ((req.Permissions ?? []).Any(p => !Permissions.All.Contains(p)))
            errors["permissions"] = ["Unknown permission."];
        return errors.Count > 0 ? Results.ValidationProblem(errors) : null;
    }

    /// <summary>
    /// Someone must always be able to manage users, or the installation is locked for good. Checked
    /// against the pending (unsaved) change to one user or one role.
    /// </summary>
    private static async Task<string?> LastAdminProblem(ClinicReportDbContext db, AppUser? changedUser, Role? changedRole = null)
    {
        var others = await db.Users.AsNoTracking().Include(u => u.Roles).ThenInclude(ur => ur.Role)
            .Where(u => u.IsActive && (changedUser == null || u.Id != changedUser.Id)).ToListAsync();
        if (changedRole is not null)
            foreach (var ur in others.SelectMany(u => u.Roles).Where(ur => ur.RoleId == changedRole.Id))
                ur.Role = changedRole;
        var candidates = changedUser is { IsActive: true } ? others.Append(changedUser) : others;
        return candidates.Any(u => Access.For(u).Permissions.Contains(Permissions.UsersManage))
            ? null
            : "At least one active user must keep the \"Manage users and roles\" permission.";
    }

    private static string Describe(AppUser u) =>
        $"{(u.IsActive ? "active" : "inactive")}; roles: {string.Join(", ", u.Roles.Select(r => r.Role?.Name ?? r.RoleId.ToString()))}; services: {ServicesText(u.ServiceList())}";

    private static string ServicesText(IEnumerable<string>? services) =>
        services?.Any() == true ? string.Join(", ", services) : "all";

    private static string? FormatServices(string[]? services)
    {
        var list = (services ?? []).Select(s => s.Trim()).Where(s => s.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        return list.Length == 0 ? null : string.Join('\n', list);
    }

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
