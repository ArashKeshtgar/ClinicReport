using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;

namespace ClinicReport.Tests;

/// <summary>
/// Every clinic's database numbers its reports from 1, so the live channel must keep clinics apart: someone at one
/// clinic with report 1 open never sees who is in report 1 at another clinic, or its changes.
/// </summary>
public class LiveIsolationTests(ConnectedFactory factory) : IClassFixture<ConnectedFactory>
{
    private static readonly string[] Physician = ["reports.view", "reports.create", "reports.edit", "reports.sign", "patients.manage"];

    private sealed record Session(HttpClient Http, string Cookie);

    /// <summary>Signs in like <see cref="ConnectedFactory.SignInAsync"/>, but keeps the cookie so the hub can use it too.</summary>
    private async Task<Session> SignInAsync(string sub, string user, string clinic)
    {
        factory.TokenEndpoint = () => new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new
            {
                id_token = ConnectedFactory.IdToken([.. Physician.Select(p => ("permission", p)), ("role", "Physician")]),
                access_token = "at", refresh_token = "rt-2", token_type = "Bearer",
            }),
        };
        var c = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false, AllowAutoRedirect = false });
        var res = await c.GetAsync($"/test/hub-signin?sub={sub}&user={user}&clinic={clinic}&perm={string.Join(',', Physician)}");
        res.EnsureSuccessStatusCode();
        var cookie = string.Join("; ", res.Headers.GetValues("Set-Cookie").Select(v => v.Split(';')[0]));
        c.DefaultRequestHeaders.Add("X-ClinicReport", "1");
        c.DefaultRequestHeaders.Add("Cookie", cookie);
        return new Session(c, cookie);
    }

    private static async Task<JsonNode> Json(HttpResponseMessage res)
    {
        var body = await res.Content.ReadAsStringAsync();
        Assert.True(res.IsSuccessStatusCode, $"{(int)res.StatusCode}: {body}");
        return JsonNode.Parse(body)!;
    }

    private static async Task<int> NewReportAsync(HttpClient c, string mrn)
    {
        var patient = await Json(await c.PostAsJsonAsync("/api/patients", new { mrn, firstName = "Test", lastName = "Patient", dateOfBirth = "1980-01-01", sex = "F" }));
        var templates = await Json(await c.GetAsync("/api/templates"));
        var echo = templates.AsArray().Single(t => (string)t!["key"]! == "echo-tte")!;
        var report = await Json(await c.PostAsJsonAsync("/api/reports", new
        {
            patientId = (int)patient["id"]!, templateId = (int)echo["id"]!, examDate = "2026-10-09", heightCm = 165, weightKg = 60,
        }));
        return (int)report["id"]!;
    }

    private HubConnection Hub(Session s) => new HubConnectionBuilder()
        .WithUrl(new Uri(factory.Server.BaseAddress, "/hubs/report"), o =>
        {
            // WebSockets, as in the browser: the hub finds the clinic's database through the request's user.
            o.Transports = HttpTransportType.WebSockets;
            o.SkipNegotiation = true;
            o.WebSocketFactory = async (ctx, ct) =>
            {
                var ws = factory.Server.CreateWebSocketClient();
                ws.ConfigureRequest = r =>
                {
                    r.Headers["Cookie"] = s.Cookie;
                    r.Headers["X-ClinicReport"] = "1";
                };
                return await ws.ConnectAsync(ctx.Uri, ct);
            };
        })
        .Build();

    [Fact]
    public async Task Report_1_of_one_clinic_is_not_live_at_another()
    {
        var north = await SignInAsync("21", "dr.north", "northside");
        var south = await SignInAsync("22", "dr.south", "southside");
        var northReport = await NewReportAsync(north.Http, "N-1");
        var southReport = await NewReportAsync(south.Http, "S-1");
        Assert.Equal(northReport, southReport);

        // Southside has its report open and writes down everything the channel sends it.
        var southSeen = new ConcurrentQueue<(string Kind, string Body)>();
        var southFocused = new TaskCompletionSource();
        await using var southHub = Hub(south);
        southHub.On<JsonElement>("presence", p =>
        {
            southSeen.Enqueue(("presence", p.GetRawText()));
            if (p.EnumerateArray().Any(v => v.GetProperty("fieldId").ValueKind == JsonValueKind.String && v.GetProperty("fieldId").GetString() == "done"))
                southFocused.TrySetResult();
        });
        southHub.On<JsonElement>("reportChanged", r => southSeen.Enqueue(("reportChanged", r.GetRawText())));
        await southHub.StartAsync();
        await southHub.InvokeAsync("JoinReport", southReport);

        // Northside opens its own report 1 and changes it.
        var northChanged = new TaskCompletionSource();
        await using var northHub = Hub(north);
        northHub.On<JsonElement>("reportChanged", _ => northChanged.TrySetResult());
        await northHub.StartAsync();
        await northHub.InvokeAsync("JoinReport", northReport);
        await northHub.InvokeAsync("Focus", "lvef");
        await Json(await north.Http.PutAsJsonAsync($"/api/reports/{northReport}/conclusion", new { conclusion = "Northside only." }));
        await northChanged.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // Messages to one connection arrive in order, so once Southside sees its own focus, anything sent before it has arrived.
        await southHub.InvokeAsync("Focus", "done");
        await southFocused.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.DoesNotContain(southSeen, m => m.Kind == "reportChanged");
        Assert.DoesNotContain(southSeen, m => m.Body.Contains("dr.north") || m.Body.Contains("lvef"));
        Assert.Contains(southSeen, m => m.Kind == "presence" && m.Body.Contains("dr.south"));
    }
}
