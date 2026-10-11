using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;

namespace ClinicReport.Tests;

/// <summary>Orders from ClinicHub: a FHIR transaction Bundle in, an exam on the worklist, a draft report out.</summary>
public class OrderTests(ConnectedFactory factory) : IClassFixture<ConnectedFactory>
{
    private static readonly string[] Physician = ["reports.view", "reports.create", "reports.edit", "reports.sign", "patients.manage"];

    /// <summary>What ClinicHub's outbox sends (see ClinicHub's FhirOrderBundle).</summary>
    private static string Bundle(string orderId, string mrn, string template = "echo-tte", string family = "Wilson") => new JsonObject
    {
        ["resourceType"] = "Bundle", ["type"] = "transaction",
        ["identifier"] = new JsonObject { ["system"] = "urn:clinichub:harbourview:requisition", ["value"] = $"R{orderId}" },
        ["entry"] = new JsonArray(
            Entry(new JsonObject
            {
                ["resourceType"] = "Patient",
                ["identifier"] = new JsonArray(new JsonObject { ["system"] = "urn:clinichub:harbourview:mrn", ["value"] = mrn }),
                ["name"] = new JsonArray(new JsonObject { ["family"] = family, ["given"] = new JsonArray("Mei") }),
                ["gender"] = "female", ["birthDate"] = "1986-02-06",
            }),
            Entry(new JsonObject
            {
                ["resourceType"] = "Practitioner",
                ["identifier"] = new JsonArray(new JsonObject { ["system"] = "urn:clinichub:ohip-billing-number", ["value"] = "123456" }),
                ["name"] = new JsonArray(new JsonObject { ["family"] = "Patel", ["given"] = new JsonArray("Nadia"), ["prefix"] = new JsonArray("Dr.") }),
            }),
            Entry(new JsonObject
            {
                ["resourceType"] = "ServiceRequest",
                ["identifier"] = new JsonArray(new JsonObject { ["system"] = "urn:clinichub:harbourview:service-request", ["value"] = orderId }),
                ["status"] = "active", ["intent"] = "order", ["priority"] = "stat",
                ["code"] = new JsonObject { ["coding"] = new JsonArray(new JsonObject { ["system"] = "urn:clinicreport:template", ["code"] = template }) },
                ["subject"] = new JsonObject { ["reference"] = "urn:uuid:patient" },
                ["note"] = new JsonArray(new JsonObject { ["text"] = "Dyspnea on exertion, murmur" }),
            })),
    }.ToJsonString();

    private static JsonObject Entry(JsonObject resource) => new() { ["resource"] = resource, ["request"] = new JsonObject { ["method"] = "POST", ["url"] = (string)resource["resourceType"]! } };

    /// <summary>ClinicHub keeps confirming the typist's session (it is re-checked on every request in these tests).</summary>
    private void KeepSignedIn() => factory.TokenEndpoint = () => new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = JsonContent.Create(new { id_token = ConnectedFactory.IdToken([.. Physician.Select(p => ("permission", p))]), access_token = "at", refresh_token = "rt" }),
    };

    private async Task<HttpResponseMessage> Send(string bundle, string? token)
    {
        var c = factory.CreateClient();
        var req = new HttpRequestMessage(HttpMethod.Post, "/fhir") { Content = new StringContent(bundle, Encoding.UTF8, "application/fhir+json") };
        if (token is not null)
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await c.SendAsync(req);
    }

    private static async Task<JsonNode> Json(HttpResponseMessage res)
    {
        var body = await res.Content.ReadAsStringAsync();
        Assert.True(res.IsSuccessStatusCode, $"{(int)res.StatusCode}: {body}");
        return JsonNode.Parse(body)!;
    }

    [Fact]
    public async Task An_order_waits_on_the_worklist_and_starts_a_prefilled_report()
    {
        KeepSignedIn();
        var res = await Json(await Send(Bundle("501", "HV000123"), ConnectedFactory.ServiceToken("harbourview")));
        Assert.Equal("transaction-response", (string)res["type"]!);
        Assert.StartsWith("ServiceRequest/", (string)res["entry"]![2]!["response"]!["location"]!);

        // Sent again (ClinicHub retried after a lost answer): still one order.
        await Json(await Send(Bundle("501", "HV000123"), ConnectedFactory.ServiceToken("harbourview")));

        var typist = await factory.SignInAsync("20", "typist", "harbourview", Physician);
        var orders = (await Json(await typist.GetAsync("/api/orders"))).AsArray();
        var order = Assert.Single(orders, o => (string)o!["mrn"]! == "HV000123")!;
        Assert.Equal("Stat", (string)order["priority"]!);
        Assert.Equal("Dr. Nadia Patel (OHIP 123456)", (string)order["referringPhysician"]!);

        var started = await Json(await typist.PostAsync($"/api/orders/{(int)order["id"]!}/start", null));
        var report = await Json(await typist.GetAsync($"/api/reports/{(int)started["reportId"]!}"));
        Assert.Equal("Dyspnea on exertion, murmur", (string)report["header"]!["indication"]!);
        Assert.Equal("Dr. Nadia Patel (OHIP 123456)", (string)report["header"]!["referringPhysician"]!);
        Assert.Equal(HttpStatusCode.Conflict, (await typist.PostAsync($"/api/orders/{(int)order["id"]!}/start", null)).StatusCode);
    }

    private static string Revoke(string orderId) => new JsonObject
    {
        ["resourceType"] = "Bundle", ["type"] = "transaction",
        ["entry"] = new JsonArray(new JsonObject
        {
            ["resource"] = new JsonObject
            {
                ["resourceType"] = "ServiceRequest", ["status"] = "revoked", ["intent"] = "order",
                ["identifier"] = new JsonArray(new JsonObject { ["system"] = "urn:clinichub:harbourview:service-request", ["value"] = orderId }),
                ["note"] = new JsonArray(new JsonObject { ["text"] = "Patient declined" }),
            },
            ["request"] = new JsonObject { ["method"] = "PUT", ["url"] = $"ServiceRequest?identifier=urn:clinichub:harbourview:service-request|{orderId}" },
        }),
    }.ToJsonString();

    [Fact]
    public async Task A_withdrawn_exam_leaves_the_worklist_unless_its_report_is_started()
    {
        KeepSignedIn();
        var token = ConnectedFactory.ServiceToken("harbourview");
        await Json(await Send(Bundle("520", "HV000520"), token));
        await Json(await Send(Bundle("521", "HV000521"), token));
        var typist = await factory.SignInAsync("20", "typist", "harbourview", Physician);

        await Json(await Send(Revoke("520"), token));
        await Json(await Send(Revoke("520"), token));   // sent again: the same answer
        var worklist = (await Json(await typist.GetAsync("/api/orders"))).AsArray();
        Assert.DoesNotContain(worklist, o => (string)o!["mrn"]! == "HV000520");

        var started = worklist.Single(o => (string)o!["mrn"]! == "HV000521")!;
        await Json(await typist.PostAsync($"/api/orders/{(int)started["id"]!}/start", null));
        var refused = await Send(Revoke("521"), token);
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Contains("has been started", await refused.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.NotFound, (await Send(Revoke("599"), token)).StatusCode);
    }

    [Fact]
    public async Task Orders_land_in_the_clinic_the_token_names()
    {
        KeepSignedIn();
        await Json(await Send(Bundle("601", "LS000045", family: "Lakeside"), ConnectedFactory.ServiceToken("lakeshore")));
        var harbour = await factory.SignInAsync("21", "typist2", "harbourview", Physician);
        Assert.DoesNotContain((await Json(await harbour.GetAsync("/api/orders"))).AsArray(), o => (string)o!["mrn"]! == "LS000045");
        var lake = await factory.SignInAsync("21", "typist2", "lakeshore", Physician);
        Assert.Contains((await Json(await lake.GetAsync("/api/orders"))).AsArray(), o => (string)o!["mrn"]! == "LS000045");
    }

    [Fact]
    public async Task Only_ClinicHub_with_the_orders_scope_may_send()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(Bundle("701", "HV1"), null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(Bundle("701", "HV1"), ConnectedFactory.ServiceToken("harbourview", audience: "labflow"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(Bundle("701", "HV1"), ConnectedFactory.ServiceToken("harbourview", scope: "profile"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(Bundle("701", "HV1"), ConnectedFactory.ServiceToken("../x"))).StatusCode);
    }

    [Fact]
    public async Task An_unknown_exam_is_refused_with_an_operation_outcome()
    {
        var res = await Send(Bundle("801", "HV000200", template: "mri-brain"), ConnectedFactory.ServiceToken("harbourview"));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        var outcome = JsonNode.Parse(await res.Content.ReadAsStringAsync())!;
        Assert.Equal("OperationOutcome", (string)outcome["resourceType"]!);
        Assert.Contains("mri-brain", (string)outcome["issue"]![0]!["diagnostics"]!);
    }

    [Fact]
    public async Task A_signed_ordered_report_goes_back_to_ClinicHub_and_an_addendum_follows()
    {
        KeepSignedIn();
        await Json(await Send(Bundle("901", "HV000777"), ConnectedFactory.ServiceToken("harbourview")));
        var physician = await factory.SignInAsync("22", "dr.reader", "harbourview", Physician);
        var order = (await Json(await physician.GetAsync("/api/orders"))).AsArray().Single(o => (string)o!["mrn"]! == "HV000777")!;
        var id = (int)(await Json(await physician.PostAsync($"/api/orders/{(int)order["id"]!}/start", null)))["reportId"]!;
        foreach (var section in new[] { "study", "lv", "rv", "mitral", "aortic", "tricuspid", "pericardium" })
            (await physician.PostAsync($"/api/reports/{id}/presets/{section}", null)).EnsureSuccessStatusCode();
        (await physician.PutAsJsonAsync($"/api/reports/{id}/conclusion", new { conclusion = "Normal study. LVEF 60%." })).EnsureSuccessStatusCode();

        // ClinicHub is down when the report is signed: the result waits.
        factory.Hub.Down = true;
        await Json(await physician.PostAsync($"/api/reports/{id}/sign", null));
        var sender = factory.Services.GetRequiredService<ClinicReport.Api.Reports.ResultSender>();
        Assert.Equal(0, await sender.SendAsync());

        // Back up: the pause after the failure has to pass, so look at the message itself.
        factory.Hub.Down = false;
        await using (var db = factory.Services.GetRequiredService<ClinicReport.Api.Auth.ClinicReportDatabases>().Create("harbourview"))
        {
            var waiting = db.ResultMessages.Single(m => m.SentAt == null);
            Assert.Equal(1, waiting.Attempts);
            waiting.NextAttemptAt = null;
            await db.SaveChangesAsync();
        }
        Assert.Equal(1, await sender.SendAsync());
        var (token, body) = factory.Hub.Results.Last();
        Assert.Equal("app-token", token);
        var report = body["entry"]![0]!["resource"]!;
        Assert.Equal("final", (string)report["status"]!);
        Assert.Equal("901", (string)report["basedOn"]![0]!["identifier"]!["value"]!);
        Assert.Equal("urn:clinichub:harbourview:service-request", (string)report["basedOn"]![0]!["identifier"]!["system"]!);
        Assert.Equal("Normal study. LVEF 60%.", (string)report["conclusion"]!);
        Assert.Equal($"https://report.test/reports/{id}/print", (string)report["presentedForm"]![0]!["url"]!);

        await Json(await physician.PostAsJsonAsync($"/api/reports/{id}/addenda", new { text = "Trace mitral regurgitation." }));
        Assert.Equal(1, await sender.SendAsync());
        var amended = factory.Hub.Results.Last().Body["entry"]![0]!["resource"]!;
        Assert.Equal("amended", (string)amended["status"]!);
        Assert.Contains("Trace mitral regurgitation.", (string)amended["conclusion"]!);
    }

    [Fact]
    public async Task A_report_not_ordered_through_ClinicHub_sends_nothing()
    {
        KeepSignedIn();
        var physician = await factory.SignInAsync("23", "dr.other", "harbourview", Physician);
        var before = factory.Hub.Results.Count;
        var templates = (await Json(await physician.GetAsync("/api/templates"))).AsArray();
        var patient = await Json(await physician.PostAsJsonAsync("/api/patients", new { mrn = "LOCAL-1", firstName = "A", lastName = "Walkin", dateOfBirth = "1970-01-01", sex = "M" }));
        var report = await Json(await physician.PostAsJsonAsync("/api/reports", new
        {
            patientId = (int)patient["id"]!, templateId = (int)templates.Single(t => (string)t!["key"]! == "echo-tte")!["id"]!, examDate = "2026-10-11",
        }));
        var id = (int)report["id"]!;
        foreach (var section in new[] { "study", "lv", "rv", "mitral", "aortic", "tricuspid", "pericardium" })
            (await physician.PostAsync($"/api/reports/{id}/presets/{section}", null)).EnsureSuccessStatusCode();
        (await physician.PutAsJsonAsync($"/api/reports/{id}/conclusion", new { conclusion = "Normal." })).EnsureSuccessStatusCode();
        await Json(await physician.PostAsync($"/api/reports/{id}/sign", null));
        await factory.Services.GetRequiredService<ClinicReport.Api.Reports.ResultSender>().SendAsync();
        Assert.Equal(before, factory.Hub.Results.Count);
    }

    [Fact]
    public async Task A_signed_ordered_exam_becomes_a_receivable_from_ohip_once_the_template_has_fees()
    {
        KeepSignedIn();
        await Json(await Send(Bundle("950", "HV000950", family: "Feeless"), ConnectedFactory.ServiceToken("harbourview")));
        var physician = await factory.SignInAsync("24", "dr.fees", "harbourview", [.. Physician, "templates.design"]);
        var order = (await Json(await physician.GetAsync("/api/orders"))).AsArray().Single(o => (string)o!["mrn"]! == "HV000950")!;
        var id = (int)(await Json(await physician.PostAsync($"/api/orders/{(int)order["id"]!}/start", null)))["reportId"]!;
        foreach (var section in new[] { "study", "lv", "rv", "mitral", "aortic", "tricuspid", "pericardium" })
            (await physician.PostAsync($"/api/reports/{id}/presets/{section}", null)).EnsureSuccessStatusCode();
        (await physician.PutAsJsonAsync($"/api/reports/{id}/conclusion", new { conclusion = "Normal study." })).EnsureSuccessStatusCode();
        await Json(await physician.PostAsync($"/api/reports/{id}/sign", null));

        // Setting fees needs the designer permission, and every request re-checks the session with ClinicHub.
        factory.TokenEndpoint = () => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new { id_token = ConnectedFactory.IdToken([.. Physician.Append("templates.design").Select(p => ("permission", p))]), access_token = "at", refresh_token = "rt" }),
        };
        await using var db = factory.Services.GetRequiredService<ClinicReport.Api.Auth.ClinicReportDatabases>().Create("harbourview");
        // No fees on the template: nothing for the accounting.
        Assert.DoesNotContain(await ClinicReport.Api.Reports.FinanceSender.BuildAsync(db, "harbourview"), d => (string)d["externalId"]! == $"report:{id}:signed");

        var template = (await Json(await physician.GetAsync("/api/templates"))).AsArray().Single(t => (string)t!["key"]! == "echo-tte")!;
        Assert.Equal(HttpStatusCode.BadRequest, (await physician.PutAsJsonAsync($"/api/templates/{(int)template["id"]!}/fees", new { ohipTechnicalFee = -1m })).StatusCode);
        (await physician.PutAsJsonAsync($"/api/templates/{(int)template["id"]!}/fees", new { ohipTechnicalFee = 60.00m, ohipProfessionalFee = 40.00m })).EnsureSuccessStatusCode();

        var doc = (await ClinicReport.Api.Reports.FinanceSender.BuildAsync(db, "harbourview")).Single(d => (string)d["externalId"]! == $"report:{id}:signed");
        Assert.Equal(("Invoice", "OHIP"), ((string)doc["kind"]!, (string)doc["party"]!["type"]!));
        var lines = doc["lines"]!.AsArray();
        Assert.Equal(60.00m, (decimal)lines.Single(l => (string)l!["category"]! == "OHIP.Technical")!["amount"]!);
        Assert.Equal(40.00m, (decimal)lines.Single(l => (string)l!["category"]! == "Diagnostics.Echo")!["amount"]!);
        Assert.DoesNotContain("Feeless", doc.ToJsonString());
    }
}
