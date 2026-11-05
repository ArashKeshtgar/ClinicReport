using System.Threading.RateLimiting;
using ClinicReport.Api.Auth;
using ClinicReport.Api.Data;
using ClinicReport.Api.Endpoints;
using ClinicReport.Api.Reports;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);
// Secrets as files, one per setting (e.g. ConnectionStrings__Finance), the way Docker mounts them; none outside a container.
// Not under test: real keys on a developer's machine would override the tests' settings.
if (!builder.Environment.IsEnvironment("Testing"))
    builder.Configuration.AddKeyPerFile("/run/secrets", optional: true);

var connectionString = builder.Configuration.GetConnectionString("ClinicReport") ?? "Data Source=clinicreport.db";
builder.Services.AddDbContext<ClinicReportDbContext>(o => o.UseSqlite(connectionString));
// Hosted (multi-tenant) mode: the tenant registry, a database of its own.
builder.Services.AddTenantCatalog(builder.Configuration);

// Keys that encrypt the sign-in cookie. In the container they live in the data volume, so a redeploy
// doesn't sign everyone out.
if (builder.Configuration["DataProtection:KeysDirectory"] is { Length: > 0 } keysDirectory)
    builder.Services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(keysDirectory)).SetApplicationName("ClinicReport");

builder.Services.Configure<DemoOptions>(builder.Configuration.GetSection("Demo"));
builder.Services.Configure<ClinicOptions>(builder.Configuration.GetSection("Clinic"));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<IPasswordHasher<AppUser>, PasswordHasher<AppUser>>();
builder.Services.AddSingleton<PresenceTracker>();
builder.Services.AddScoped<PlanLimits>();
builder.Services.AddScoped<ReportService>();
builder.Services.AddScoped<SecurityLog>();
builder.Services.AddMemoryCache();
builder.Services.AddSingleton<ConfidentialityGate>();
builder.Services.AddHttpContextAccessor();
builder.Services.AddHostedService<DemoResetService>();
builder.Services.AddSignalR();
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<ReportRuleHandler>();
builder.Services.AddOpenApi();
builder.Services.AddClinicAuth(builder.Configuration);
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull);

var loginsPerMinute = builder.Configuration.GetValue("RateLimits:LoginsPerMinute", 10);
var signupsPerHour = builder.Configuration.GetValue("RateLimits:SignupsPerHour", 5);
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.AddPolicy(AuthEndpoints.LoginRateLimit, ctx => RateLimitPartition.GetFixedWindowLimiter(
        ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = loginsPerMinute, Window = TimeSpan.FromMinutes(1) }));
    // Hosted: every sign-up creates a database, so a few an hour from one address.
    o.AddPolicy(SignupEndpoints.SignupRateLimit, ctx => RateLimitPartition.GetFixedWindowLimiter(
        ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = signupsPerHour, Window = TimeSpan.FromHours(1) }));
});

var app = builder.Build();

// Standalone: one database, prepared now. Connected to ClinicHub: one database per clinic, prepared at its first sign-in.
// Hosted: the catalog now, each tenant's database at its first request.
var connected = app.Services.IsConnected();
var hosted = app.Services.IsHosted();
if (hosted)
    await CatalogSetup.MigrateCatalogAsync(app.Services);
if (!connected && !hosted)
    using (var scope = app.Services.CreateScope())
        await DatabaseInitializer.RunAsync(scope.ServiceProvider, wipe: false);

// "--migrate-all" (Hosted): every tenant's database brought up to date, then exit; exit code 1 if any failed.
if (args.Contains(TenantMigration.Command))
{
    if (!hosted)
        throw new InvalidOperationException($"{TenantMigration.Command} is for Hosted mode.");
    Environment.ExitCode = await TenantMigration.MigrateAllAsync(app.Services) == 0 ? 0 : 1;
    return;
}

// "--backup-all folder" and "--restore-tenant slug file" (Hosted): consistent copies, and one clinic put back on its own.
if (Array.IndexOf(args, TenantBackup.BackupCommand) is >= 0 and var backupArg)
{
    if (!hosted || backupArg + 1 >= args.Length)
        throw new InvalidOperationException($"{TenantBackup.BackupCommand} <folder> is for Hosted mode.");
    Environment.ExitCode = await TenantBackup.BackupAllAsync(app.Services, args[backupArg + 1]) == 0 ? 0 : 1;
    return;
}
if (Array.IndexOf(args, TenantBackup.RestoreCommand) is >= 0 and var restoreArg)
{
    if (!hosted || restoreArg + 2 >= args.Length)
        throw new InvalidOperationException($"{TenantBackup.RestoreCommand} <slug> <file> is for Hosted mode.");
    await TenantBackup.RestoreAsync(app.Services, args[restoreArg + 1], args[restoreArg + 2]);
    return;
}

// "--add-operator name" (Hosted): an account for the console. Its password and secret are shown here once.
if (Array.IndexOf(args, OperatorSetup.Command) is >= 0 and var operatorArg)
{
    if (!hosted || operatorArg + 1 >= args.Length)
        throw new InvalidOperationException($"{OperatorSetup.Command} <name> is for Hosted mode.");
    var (password, secret) = await OperatorSetup.AddAsync(app.Services, args[operatorArg + 1]);
    Console.WriteLine($"Password: {password}");
    Console.WriteLine($"Two-factor: {Totp.OtpAuthUri("ClinicReport console", args[operatorArg + 1], secret)}");
    return;
}

app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseTenantResolution();
app.UseDefaultFiles();
app.UseStaticFiles();
if (app.Environment.IsDevelopment())
    app.MapOpenApi().AllowAnonymous();

app.UseRateLimiter();
app.UseAuthentication();
app.UseTenantGate();
app.UsePendingActionGate();
app.UseAuthorization();
app.UseCsrfHeaderCheck();

app.MapGet("/api/settings", (HttpContext http, IOptions<DemoOptions> demo, IOptions<ClinicHubOptions> hub, IOptions<HostingOptions> hosting) =>
{
    // Hosted: the tenant of the host, or on the root domain the service's sign-up. Otherwise the "Clinic" settings.
    var signup = http.OnRootDomain();
    var clinic = signup || http.OnOperatorHost() ? new ClinicOptions() : http.CurrentClinic();
    return new
    {
        clinic.Name, clinic.Address, clinic.Phone, clinic.ReportFooter,
        Signup = signup,
        // Hosted, at the console's host: not a clinic and not the sign-up page.
        Operator = http.OnOperatorHost(),
        SyntheticDataOnly = hosting.Value.SyntheticDataOnly,
        Hosted = http.RequestServices.IsHosted(),
        // Hosted: signed up, email not confirmed yet, so no reports can be started.
        AwaitingEmailVerification = http.RequestServices.GetService<ITenantContext>()?.Tenant?.AwaitingEmailVerification == true,
        BaseDomain = signup ? http.Request.Host.Value : null,
        Demo = demo.Value.Enabled && !hub.Value.Enabled,
        AccountsLocked = demo.Value.Enabled && demo.Value.LockAccounts,
        DemoUsers = demo.Value.Enabled && !hub.Value.Enabled ? DemoSeeder.DemoUserNames : [],
        // "clinichub": people sign in through ClinicHub, which also manages passwords, two-factor and roles.
        SignIn = hub.Value.Enabled ? "clinichub" : "local",
        ClinicHubUrl = hub.Value.Enabled ? hub.Value.Authority : null,
    };
}).AllowAnonymous();

app.MapGet("/healthz", () => Results.Ok("ok")).AllowAnonymous();

app.MapSignupEndpoints();
app.MapClinicEndpoints();
app.MapBillingEndpoints();
app.MapOperatorEndpoints();
app.MapAuthEndpoints();
app.MapUserEndpoints();
app.MapPatientEndpoints();
app.MapTemplateEndpoints();
app.MapReportEndpoints();
app.MapOrderEndpoints();
app.MapHub<ReportHub>("/hubs/report");

// The React app is served from wwwroot in production; client-side routes fall back to index.html.
app.MapFallbackToFile("index.html").AllowAnonymous();

app.Run();

/// <summary>Turns business-rule violations into ProblemDetails with the right status code.</summary>
internal sealed class ReportRuleHandler(IProblemDetailsService problems) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext http, Exception exception, CancellationToken ct)
    {
        if (exception is not ReportRuleException rule)
            return false;
        http.Response.StatusCode = rule.Status;
        return await problems.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = http,
            ProblemDetails = { Status = rule.Status, Title = rule.Message },
            Exception = exception,
        });
    }
}

public partial class Program;
