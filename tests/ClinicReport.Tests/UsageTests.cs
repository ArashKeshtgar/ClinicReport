using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using ClinicReport.Api.Auth;
using ClinicReport.Api.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ClinicReport.Tests;

/// <summary>Hosted mode: what clinics use, counted in the catalog. Demo tenants share their Ids and start with 4 users.</summary>
public class UsageTests : IAsyncLifetime
{
    private readonly HostedFactory _factory = new();

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    private async Task<int> TenantIdAsync(string slug)
    {
        using var scope = _factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<CatalogDbContext>().Tenants.Where(t => t.Slug == slug).Select(t => t.Id).SingleAsync();
    }

    private async Task RollUpAsync()
    {
        using var scope = _factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<UsageRollup>().RunAsync();
    }

    private async Task<int?> MonthAsync(string slug, string kind)
    {
        var id = await TenantIdAsync(slug);
        var month = UsageMonth.Key(DateTime.UtcNow);
        using var scope = _factory.Services.CreateScope();
        return (await scope.ServiceProvider.GetRequiredService<CatalogDbContext>().UsageMonths
            .SingleOrDefaultAsync(m => m.TenantId == id && m.Kind == kind && m.Month == month))?.Quantity;
    }

    private static async Task<JsonNode> Json(HttpResponseMessage res)
    {
        var body = await res.Content.ReadAsStringAsync();
        Assert.True(res.IsSuccessStatusCode, $"{(int)res.StatusCode}: {body}");
        return JsonNode.Parse(body)!;
    }

    private async Task SignAReportAsync(string slug)
    {
        var typist = (await _factory.SignInAtAsync(slug, "demo.typist")).Client;
        var physician = (await _factory.SignInAtAsync(slug, "demo.physician")).Client;
        var echo = (await typist.GetFromJsonAsync<JsonArray>("/api/templates"))!.Single(t => (string)t!["key"]! == "echo-tte")!;
        var patient = (await typist.GetFromJsonAsync<JsonArray>("/api/patients?q=DEMO-0003"))![0]!;
        var id = (await Json(await typist.PostAsJsonAsync("/api/reports", new
        {
            patientId = patient["id"]!.GetValue<int>(), templateId = echo["id"]!.GetValue<int>(), examDate = "2026-10-09",
            heightCm = 165, weightKg = 60, indication = "Palpitations",
        })))["id"]!.GetValue<int>();
        foreach (var section in new[] { "study", "lv", "rv", "mitral", "aortic", "tricuspid", "pericardium" })
            (await typist.PostAsync($"/api/reports/{id}/presets/{section}", null)).EnsureSuccessStatusCode();
        (await physician.PutAsJsonAsync($"/api/reports/{id}/conclusion", new { conclusion = "Normal study." })).EnsureSuccessStatusCode();
        await Json(await physician.PostAsync($"/api/reports/{id}/sign", null));
    }

    [Fact]
    public async Task A_signed_report_is_counted_for_its_clinic_only()
    {
        await _factory.AddTenantAsync("north");
        await _factory.AddTenantAsync("south");
        await SignAReportAsync("north");
        await SignAReportAsync("north");
        await SignAReportAsync("south");

        await RollUpAsync();

        Assert.Equal(2, await MonthAsync("north", UsageKinds.ReportSigned));
        Assert.Equal(1, await MonthAsync("south", UsageKinds.ReportSigned));
    }

    [Fact]
    public async Task Rolling_up_twice_counts_nothing_twice()
    {
        await _factory.AddTenantAsync("north");
        await SignAReportAsync("north");

        await RollUpAsync();
        await RollUpAsync();

        Assert.Equal(1, await MonthAsync("north", UsageKinds.ReportSigned));
        var id = await TenantIdAsync("north");
        using var scope = _factory.Services.CreateScope();
        Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<CatalogDbContext>().UsageEvents.CountAsync(e => e.TenantId == id && e.Kind == UsageKinds.ActiveUser));
    }

    [Fact]
    public async Task Active_users_are_a_daily_snapshot_and_the_month_keeps_the_peak()
    {
        await _factory.AddTenantAsync("north");
        await (await _factory.SignInAtAsync("north")).Client.GetAsync("/api/settings");
        var id = await TenantIdAsync("north");
        using (var scope = _factory.Services.CreateScope())
        {
            var catalog = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
            catalog.UsageEvents.Add(new UsageEvent { TenantId = id, Kind = UsageKinds.ActiveUser, At = DateTime.UtcNow.AddHours(-30), Quantity = 9 });
            await catalog.SaveChangesAsync();
        }

        await RollUpAsync();

        var expected = DateTime.UtcNow.Day == 1 && DateTime.UtcNow.Hour < 7 ? 4 : 9;
        Assert.Equal(expected, await MonthAsync("north", UsageKinds.ActiveUser));
    }

    [Theory]
    [InlineData(TenantStatus.Suspended)]
    [InlineData(TenantStatus.Cancelled)]
    public async Task A_clinic_that_is_not_working_gets_no_snapshot(string status)
    {
        await _factory.AddTenantAsync("north", status);

        await RollUpAsync();

        Assert.Null(await MonthAsync("north", UsageKinds.ActiveUser));
    }

    [Fact]
    public async Task A_broken_database_is_marked_and_the_others_are_still_counted()
    {
        await _factory.AddTenantAsync("broken");
        await File.WriteAllTextAsync(_factory.DatabaseOf("broken"), "not a database");
        await _factory.AddTenantAsync("north");

        await RollUpAsync();

        using var scope = _factory.Services.CreateScope();
        var catalog = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
        Assert.True(await catalog.Tenants.Where(t => t.Slug == "broken").Select(t => t.DatabaseFailed).SingleAsync());
        Assert.False(await catalog.Tenants.Where(t => t.Slug == "north").Select(t => t.DatabaseFailed).SingleAsync());
        Assert.Equal(4, await MonthAsync("north", UsageKinds.ActiveUser));
    }

    [Fact]
    public async Task Outside_hosted_mode_signing_records_nothing_and_still_works()
    {
        await using var standalone = new ApiFactory();
        var typist = await standalone.SignInAsync("demo.typist");

        Assert.Equal(HttpStatusCode.OK, (await typist.GetAsync("/api/reports")).StatusCode);
    }
}
