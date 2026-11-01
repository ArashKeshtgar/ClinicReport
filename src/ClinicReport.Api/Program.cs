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
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.AddPolicy(AuthEndpoints.LoginRateLimit, ctx => RateLimitPartition.GetFixedWindowLimiter(
        ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = loginsPerMinute, Window = TimeSpan.FromMinutes(1) }));
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

app.MapGet("/api/settings", (HttpContext http, IOptions<DemoOptions> demo, IOptions<ClinicHubOptions> hub) =>
{
    // Hosted: the tenant of the host. Otherwise the "Clinic" settings.
    var clinic = http.CurrentClinic();
    return new
    {
        clinic.Name, clinic.Address, clinic.Phone, clinic.ReportFooter,
        Demo = demo.Value.Enabled && !hub.Value.Enabled,
        AccountsLocked = demo.Value.Enabled && demo.Value.LockAccounts,
        DemoUsers = demo.Value.Enabled && !hub.Value.Enabled ? DemoSeeder.DemoUserNames : [],
        // "clinichub": people sign in through ClinicHub, which also manages passwords, two-factor and roles.
        SignIn = hub.Value.Enabled ? "clinichub" : "local",
        ClinicHubUrl = hub.Value.Enabled ? hub.Value.Authority : null,
    };
}).AllowAnonymous();

app.MapGet("/healthz", () => Results.Ok("ok")).AllowAnonymous();

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
