using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using ClinicReport.Api.Auth;
using ClinicReport.Api.Data;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ClinicReport.Api.Reports;

/// <summary>
/// Connected mode: each signed report of an exam ClinicHub ordered becomes a financial document for FinancePowerApp,
/// the group's accounting: owed by OHIP, the template's technical fee (OHIP.Technical) and professional fee
/// (Diagnostics.Echo or Diagnostics.ECG). Templates without fees send nothing: the amounts are the clinic's to set
/// (Templates → OHIP fees), not guessed here. No health information leaves: no patient, no findings.
/// Sent once (FinanceExports), with a ClinicHub client-credentials token for the scope "finance".
/// </summary>
public class FinanceSender(ResultSender clinics, ClinicReportDatabases databases, IOptions<ClinicHubOptions> options,
    IOptionsMonitor<OpenIdConnectOptions> oidc, IHttpClientFactory http, TimeProvider clock, ILogger<FinanceSender> logger)
{
    public const string HttpClientName = "finance";
    private string? _token;
    private DateTime _tokenExpires;

    public bool Connected => !string.IsNullOrWhiteSpace(options.Value.FinanceUrl);

    public async Task<int> SendAsync(CancellationToken ct = default)
    {
        if (!Connected)
            return 0;
        var accepted = 0;
        foreach (var slug in clinics.Clinics())
        {
            await using var db = await databases.OpenAsync(slug);
            var sent = (await db.FinanceExports.AsNoTracking().Select(x => x.ExternalId).ToListAsync(ct)).ToHashSet();
            var pending = (await BuildAsync(db, slug, ct)).Where(d => !sent.Contains((string)d["externalId"]!)).ToList();
            foreach (var chunk in pending.Chunk(100))
            {
                try
                {
                    using var request = new HttpRequestMessage(HttpMethod.Post, options.Value.FinanceUrl!.TrimEnd('/') + "/api/integration/documents")
                    {
                        Content = JsonContent.Create(new JsonArray(chunk.Select(d => (JsonNode)d.DeepClone()).ToArray())),
                    };
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await TokenAsync(ct));
                    using var response = await http.CreateClient(HttpClientName).SendAsync(request, ct);
                    if (!response.IsSuccessStatusCode)
                    {
                        if ((int)response.StatusCode == 401) _token = null;
                        logger.LogWarning("FinancePowerApp refused {Count} documents for {Clinic}: {Status} {Body}", chunk.Length, slug, (int)response.StatusCode,
                            await response.Content.ReadAsStringAsync(ct));
                        break;
                    }
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
                {
                    logger.LogWarning("FinancePowerApp could not be reached for {Clinic}: {Error}", slug, ex.Message);
                    break;
                }
                var now = clock.GetUtcNow().UtcDateTime;
                db.FinanceExports.AddRange(chunk.Select(d => new FinanceExport { ExternalId = (string)d["externalId"]!, SentAt = now }));
                await db.SaveChangesAsync(ct);
                accepted += chunk.Length;
            }
        }
        return accepted;
    }

    /// <summary>The documents a clinic's database implies today.</summary>
    public static async Task<List<JsonObject>> BuildAsync(ClinicReportDbContext db, string sourceKey, CancellationToken ct = default)
    {
        var signed = await db.ReportOrders.AsNoTracking()
            .Where(o => o.ReportId != null && o.Report!.Status == ReportStatus.Final
                && (o.Template.OhipTechnicalFee != null || o.Template.OhipProfessionalFee != null))
            .Select(o => new { o.Report!.Id, o.Report.ExamDate, o.Template.Service, o.Template.OhipTechnicalFee, o.Template.OhipProfessionalFee })
            .OrderBy(o => o.Id).ToListAsync(ct);
        var docs = new List<JsonObject>();
        foreach (var r in signed)
        {
            var lines = new JsonArray();
            var department = r.Service.Contains("Electrocardio", StringComparison.OrdinalIgnoreCase) || r.Service.Contains("ECG", StringComparison.OrdinalIgnoreCase) ? "ECG" : "Echo";
            if (r.OhipTechnicalFee is > 0 and var tech)
                lines.Add(Line("OHIP.Technical", tech, department));
            if (r.OhipProfessionalFee is > 0 and var prof)
                lines.Add(Line($"Diagnostics.{department}", prof, department));
            if (lines.Count == 0)
                continue;
            docs.Add(new JsonObject
            {
                ["sourceKey"] = sourceKey, ["externalId"] = $"report:{r.Id}:signed", ["kind"] = "Invoice", ["date"] = r.ExamDate.ToString("yyyy-MM-dd"),
                ["reference"] = $"CR-{r.Id:D6}", ["party"] = new JsonObject { ["type"] = "OHIP", ["code"] = "OHIP" }, ["lines"] = lines,
                ["memo"] = $"{r.Service} report CR-{r.Id:D6}",
            });
        }
        return docs;
    }

    private static JsonObject Line(string category, decimal amount, string department) => new()
    {
        ["category"] = category, ["amount"] = amount, ["taxCode"] = "EXEMPT", ["department"] = department,
    };

    private async Task<string> TokenAsync(CancellationToken ct)
    {
        if (_token is not null && clock.GetUtcNow().UtcDateTime < _tokenExpires)
            return _token;
        var configuration = await oidc.Get(ClinicHubConnection.Scheme).ConfigurationManager!.GetConfigurationAsync(ct);
        using var response = await http.CreateClient(HttpClientName).PostAsync(configuration.TokenEndpoint, new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials", ["client_id"] = options.Value.ClientId, ["client_secret"] = options.Value.ClientSecret ?? "", ["scope"] = "finance",
        }), ct);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"ClinicHub refused ClinicReport's credentials for finance ({(int)response.StatusCode}).");
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        _token = json.RootElement.GetProperty("access_token").GetString();
        var seconds = json.RootElement.TryGetProperty("expires_in", out var e) ? e.GetInt32() : 300;
        _tokenExpires = clock.GetUtcNow().UtcDateTime.AddSeconds(Math.Max(30, seconds - 60));
        return _token!;
    }
}

public class FinanceSenderWorker(FinanceSender sender, IOptions<ClinicHubOptions> options, ILogger<FinanceSenderWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested && options.Value.SendSeconds > 0 && sender.Connected)
        {
            try { await sender.SendAsync(stoppingToken); }
            catch (Exception ex) when (ex is not OperationCanceledException) { logger.LogError(ex, "Sending financial documents failed; trying again shortly."); }
            await Task.Delay(TimeSpan.FromSeconds(Math.Max(options.Value.SendSeconds, 30)), stoppingToken);
        }
    }
}
