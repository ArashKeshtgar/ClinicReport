using System.Threading.RateLimiting;
using ClinicReport.Api.Auth;
using ClinicReport.Api.Data;
using ClinicReport.Api.Endpoints;
using ClinicReport.Api.Reports;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("ClinicReport") ?? "Data Source=clinicreport.db";
builder.Services.AddDbContext<ClinicReportDbContext>(o => o.UseSqlite(connectionString));

builder.Services.Configure<DemoOptions>(builder.Configuration.GetSection("Demo"));
builder.Services.Configure<ClinicOptions>(builder.Configuration.GetSection("Clinic"));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<IPasswordHasher<AppUser>, PasswordHasher<AppUser>>();
builder.Services.AddSingleton<PresenceTracker>();
builder.Services.AddScoped<ReportService>();
builder.Services.AddHostedService<DemoResetService>();
builder.Services.AddSignalR();
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<ReportRuleHandler>();
builder.Services.AddOpenApi();
builder.Services.AddClinicAuth();
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

using (var scope = app.Services.CreateScope())
    await DatabaseInitializer.RunAsync(scope.ServiceProvider, wipe: false);

app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseDefaultFiles();
app.UseStaticFiles();
if (app.Environment.IsDevelopment())
    app.MapOpenApi().AllowAnonymous();

app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
app.UseCsrfHeaderCheck();

app.MapGet("/api/settings", (IOptions<ClinicOptions> clinic, IOptions<DemoOptions> demo) => new
{
    clinic.Value.Name, clinic.Value.Address, clinic.Value.Phone, clinic.Value.ReportFooter,
    Demo = demo.Value.Enabled,
    DemoUsers = demo.Value.Enabled ? DemoSeeder.DemoUserNames : [],
}).AllowAnonymous();

app.MapGet("/healthz", () => Results.Ok("ok")).AllowAnonymous();

app.MapAuthEndpoints();
app.MapUserEndpoints();
app.MapPatientEndpoints();
app.MapTemplateEndpoints();
app.MapReportEndpoints();
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
