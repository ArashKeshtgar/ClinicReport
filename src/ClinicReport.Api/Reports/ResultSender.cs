using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ClinicReport.Api.Auth;
using ClinicReport.Api.Data;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ClinicReport.Api.Reports;

/// <summary>
/// The result of an order, as ClinicHub receives it: a FHIR DiagnosticReport naming ClinicHub's order in basedOn,
/// with the conclusion (and any addenda) and a link to the full report here.
/// </summary>
public static class ResultMessages
{
    /// <summary>Adds the message to the context; it is saved with the change it reports (signature or addendum).</summary>
    public static void Enqueue(ClinicReportDbContext db, ReportOrder order, Report report, string status, IEnumerable<ReportAddendum> addenda, string? publicUrl, DateTime now)
    {
        var (slug, orderId) = order.ExternalId.Split(':', 2) is [var s, var id] ? (s, id) : ("", order.ExternalId);
        var conclusion = report.Conclusion ?? "";
        foreach (var a in addenda.OrderBy(a => a.CreatedAt))
            conclusion += $"\n\nAddendum {a.CreatedAt:yyyy-MM-dd HH:mm} UTC{(a.User is null ? "" : $", {a.User.DisplayName}")}: {a.Text}";
        var diagnosticReport = new JsonObject
        {
            ["resourceType"] = "DiagnosticReport",
            ["identifier"] = new JsonArray(new JsonObject { ["system"] = $"urn:clinicreport:{slug}:report", ["value"] = report.Id.ToString() }),
            ["basedOn"] = new JsonArray(new JsonObject
            {
                ["identifier"] = new JsonObject { ["system"] = $"urn:clinichub:{slug}:service-request", ["value"] = orderId },
            }),
            ["status"] = status,
            ["code"] = new JsonObject
            {
                ["coding"] = new JsonArray(new JsonObject { ["system"] = Endpoints.OrderEndpoints.TemplateSystem, ["code"] = report.TemplateVersion.Template.Key }),
                ["text"] = report.TemplateVersion.Template.Name,
            },
            ["subject"] = new JsonObject
            {
                ["identifier"] = new JsonObject { ["system"] = $"urn:clinichub:{slug}:mrn", ["value"] = report.Patient.Mrn },
                ["display"] = $"{report.Patient.LastName}, {report.Patient.FirstName}",
            },
            ["effectiveDateTime"] = report.ExamDate.ToString("yyyy-MM-dd"),
            ["issued"] = now.ToString("yyyy-MM-ddTHH:mm:ssZ"),
            ["resultsInterpreter"] = report.SignedBy is { } by ? new JsonArray(new JsonObject { ["display"] = by.DisplayName }) : null,
            ["conclusion"] = conclusion.Trim(),
            ["presentedForm"] = publicUrl is { Length: > 0 }
                ? new JsonArray(new JsonObject { ["contentType"] = "text/html", ["url"] = $"{publicUrl.TrimEnd('/')}/reports/{report.Id}/print", ["title"] = report.TemplateVersion.Template.Name })
                : null,
        };
        db.ResultMessages.Add(new ResultMessage
        {
            ReportOrderId = order.Id, Status = status, CreatedAt = now,
            PayloadJson = new JsonObject
            {
                ["resourceType"] = "Bundle", ["type"] = "transaction",
                ["entry"] = new JsonArray(new JsonObject
                {
                    ["fullUrl"] = $"urn:uuid:report-{report.Id}", ["resource"] = diagnosticReport,
                    ["request"] = new JsonObject { ["method"] = "POST", ["url"] = "DiagnosticReport" },
                }),
            }.ToJsonString(),
        });
    }
}

/// <summary>
/// Sends waiting results to ClinicHub (connected mode), from every clinic's database, retrying with growing pauses.
/// ClinicReport authenticates as itself: a client-credentials token from ClinicHub with the "results" scope.
/// </summary>
public class ResultSender(ClinicReportDatabases databases, IOptions<ClinicHubOptions> options, IOptionsMonitor<OpenIdConnectOptions> oidc,
    IHttpClientFactory http, TimeProvider clock, ILogger<ResultSender> logger)
{
    public const string HttpClientName = "clinichub";
    public const int MaxAttempts = 12;
    private string? _token;
    private DateTime _tokenExpires;

    /// <summary>Clinics with a database here: the files the database pattern matches.</summary>
    public IEnumerable<string> Clinics()
    {
        var path = new SqliteConnectionStringBuilder(options.Value.DatabasePattern).DataSource;
        var full = Path.GetFullPath(path);
        var dir = Path.GetDirectoryName(full)!;
        var file = Path.GetFileName(full);
        var at = file.IndexOf("{clinic}", StringComparison.Ordinal);
        if (at < 0 || !Directory.Exists(dir))
            return [];
        var (prefix, suffix) = (file[..at], file[(at + "{clinic}".Length)..]);
        return Directory.GetFiles(dir, prefix + "*" + suffix).Select(Path.GetFileName)
            .Select(f => f![prefix.Length..^suffix.Length]).Where(ClinicReportDatabases.IsValidSlug);
    }

    public async Task<int> SendAsync(CancellationToken ct = default)
    {
        var sent = 0;
        foreach (var slug in Clinics())
        {
            await using var db = await databases.OpenAsync(slug);
            var now = clock.GetUtcNow().UtcDateTime;
            var due = await db.ResultMessages.Where(m => m.SentAt == null && m.Attempts < MaxAttempts && (m.NextAttemptAt == null || m.NextAttemptAt <= now))
                .OrderBy(m => m.Id).Take(20).ToListAsync(ct);
            foreach (var m in due)
            {
                m.Attempts++;
                try
                {
                    using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(options.Value.Authority!), "fhir"))
                    {
                        Content = new StringContent(m.PayloadJson, Encoding.UTF8, "application/fhir+json"),
                    };
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await TokenAsync(ct));
                    using var response = await http.CreateClient(HttpClientName).SendAsync(request, ct);
                    if (!response.IsSuccessStatusCode)
                        throw new HttpRequestException($"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync(ct)}");
                    m.SentAt = now;
                    m.LastError = null;
                    sent++;
                }
                catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or TaskCanceledException or JsonException)
                {
                    m.LastError = ex.Message.Length > 1000 ? ex.Message[..1000] : ex.Message;
                    m.NextAttemptAt = now.AddSeconds(Math.Min(10 * Math.Pow(2, m.Attempts - 1), 1800));
                    if (ex.Message.StartsWith("401"))
                        _token = null;
                    logger.LogWarning("Result {Id} for {Clinic} not delivered to ClinicHub (attempt {Attempt}): {Error}", m.Id, slug, m.Attempts, m.LastError);
                }
                await db.SaveChangesAsync(ct);
            }
        }
        return sent;
    }

    private async Task<string> TokenAsync(CancellationToken ct)
    {
        if (_token is not null && clock.GetUtcNow().UtcDateTime < _tokenExpires)
            return _token;
        var configuration = await oidc.Get(ClinicHubConnection.Scheme).ConfigurationManager!.GetConfigurationAsync(ct);
        using var response = await http.CreateClient(HttpClientName).PostAsync(configuration.TokenEndpoint, new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials", ["client_id"] = options.Value.ClientId, ["client_secret"] = options.Value.ClientSecret ?? "",
            ["scope"] = "results",
        }), ct);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"ClinicHub refused ClinicReport's credentials ({(int)response.StatusCode}).");
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        _token = json.RootElement.GetProperty("access_token").GetString();
        var seconds = json.RootElement.TryGetProperty("expires_in", out var e) ? e.GetInt32() : 300;
        _tokenExpires = clock.GetUtcNow().UtcDateTime.AddSeconds(Math.Max(30, seconds - 60));
        return _token!;
    }
}

public class ResultSenderWorker(ResultSender sender, IOptions<ClinicHubOptions> options, ILogger<ResultSenderWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested && options.Value.SendSeconds > 0)
        {
            try { await sender.SendAsync(stoppingToken); }
            catch (Exception ex) when (ex is not OperationCanceledException) { logger.LogError(ex, "Sending results failed; trying again shortly."); }
            await Task.Delay(TimeSpan.FromSeconds(options.Value.SendSeconds), stoppingToken);
        }
    }
}
