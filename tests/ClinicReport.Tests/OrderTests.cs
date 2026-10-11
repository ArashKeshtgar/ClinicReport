using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;

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
}
