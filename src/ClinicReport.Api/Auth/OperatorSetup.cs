using System.Security.Claims;
using System.Security.Cryptography;
using ClinicReport.Api.Data;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace ClinicReport.Api.Auth;

/// <summary>
/// Hosted mode: the service operator's accounts, separate from every clinic's: a cookie of their own, and no account
/// is ever made from the web. "--add-operator name" prints the password and two-factor secret once.
/// </summary>
public static class OperatorSetup
{
    public const string Scheme = "Operator";
    public const string Policy = "operator";
    public const string Command = "--add-operator";
    public const string TotpPurpose = "ClinicReport.OperatorTotp";

    /// <summary>The console's subdomain, reserved so that no clinic can sign up as it.</summary>
    public const string Subdomain = "admin";

    private const string StampClaim = "operator_stamp";

    public static readonly string DummyHash = new PasswordHasher<OperatorUser>().HashPassword(new OperatorUser(), Guid.NewGuid().ToString());

    public static void AddOperatorAuth(this IServiceCollection services, Microsoft.AspNetCore.Authentication.AuthenticationBuilder authentication)
    {
        services.AddSingleton<IPasswordHasher<OperatorUser>, PasswordHasher<OperatorUser>>();
        authentication.AddCookie(Scheme, o =>
        {
            o.Cookie.Name = "clinicreport.operator";
            o.Cookie.HttpOnly = true;
            o.Cookie.SameSite = SameSiteMode.Strict;
            o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
            o.ExpireTimeSpan = TimeSpan.FromHours(1);
            o.SlidingExpiration = true;
            o.Events.OnRedirectToLogin = ctx => { ctx.Response.StatusCode = 401; return Task.CompletedTask; };
            o.Events.OnRedirectToAccessDenied = ctx => { ctx.Response.StatusCode = 403; return Task.CompletedTask; };
            // A new password or a removed account ends the session at once.
            o.Events.OnValidatePrincipal = async ctx =>
            {
                var id = ctx.Principal?.UserId();
                var stamp = ctx.Principal?.FindFirstValue(StampClaim);
                var catalog = ctx.HttpContext.RequestServices.GetRequiredService<CatalogDbContext>();
                if (id is null || !await catalog.Operators.AnyAsync(u => u.Id == id && u.SecurityStamp == stamp))
                    ctx.RejectPrincipal();
            };
        });
    }

    public static ClaimsPrincipal Principal(OperatorUser user) => new(new ClaimsIdentity(
    [
        new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
        new Claim(ClaimTypes.Name, user.UserName),
        new Claim(StampClaim, user.SecurityStamp),
    ], Scheme));

    public static bool OnOperatorHost(this HttpContext http) =>
        http.RequestServices.IsHosted() && http.Items.ContainsKey(nameof(OnOperatorHost));

    public static void MarkOperatorHost(this HttpContext http) => http.Items[nameof(OnOperatorHost)] = true;

    /// <summary>Makes an account. The password and secret are returned once and kept only as a hash and encrypted.</summary>
    public static async Task<(string Password, string TotpSecret)> AddAsync(IServiceProvider services, string userName, CancellationToken ct = default)
    {
        if (userName.Length is < 3 or > 64 || !userName.All(c => char.IsLetterOrDigit(c) || c is '.' or '_' or '-'))
            throw new InvalidOperationException("3-64 letters, digits, dots, dashes or underscores.");
        using var scope = services.CreateScope();
        var catalog = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
        if (await catalog.Operators.AnyAsync(o => o.UserName == userName, ct))
            throw new InvalidOperationException($"There is already an operator \"{userName}\".");
        var password = RandomNumberGenerator.GetHexString(24, lowercase: true);
        var secret = Totp.NewSecret();
        var user = new OperatorUser
        {
            UserName = userName,
            TotpSecret = scope.ServiceProvider.GetRequiredService<IDataProtectionProvider>().CreateProtector(TotpPurpose).Protect(secret),
            SecurityStamp = Guid.NewGuid().ToString("N"),
            CreatedAt = scope.ServiceProvider.GetRequiredService<TimeProvider>().GetUtcNow().UtcDateTime,
        };
        user.PasswordHash = scope.ServiceProvider.GetRequiredService<IPasswordHasher<OperatorUser>>().HashPassword(user, password);
        catalog.Operators.Add(user);
        await catalog.SaveChangesAsync(ct);
        return (password, secret);
    }
}
