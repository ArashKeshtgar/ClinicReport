using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClinicReport.Api.Auth;
using ClinicReport.Api.Endpoints;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ClinicReport.Tests;

/// <summary>Hosted without demo data: a signed-up clinic starts empty, as a real one would.</summary>
public class EmptyHostedFactory : HostedFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("Demo:Enabled", "false");
    }
}

/// <summary>Hosted mode: a new clinic's letterhead and its onboarding checklist.</summary>
public class OnboardingTests : IAsyncLifetime
{
    private readonly EmptyHostedFactory _factory = new();

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    private async Task<HttpClient> SignUpAndLogInAsync(string slug)
    {
        var email = $"owner@{slug}.example.com";
        var res = await _factory.RootClient().PostAsJsonAsync("/api/signup",
            new { clinicName = $"Clinic {slug}", slug, adminName = "Test Owner", email, password = "a long passphrase 42" });
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        var client = _factory.ClientFor(slug);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/auth/login", new { userName = email, password = "a long passphrase 42" })).StatusCode);
        return client;
    }

    private static async Task<Dictionary<string, bool>> StepsAsync(HttpClient client)
    {
        var onboarding = await client.GetFromJsonAsync<ClinicEndpoints.Onboarding>("/api/clinic/onboarding");
        return onboarding!.Steps.ToDictionary(s => s.Key, s => s.Done);
    }

    [Fact]
    public async Task The_checklist_follows_the_clinic_from_sign_up_to_its_first_report()
    {
        var client = await SignUpAndLogInAsync("harbour");
        Assert.All(await StepsAsync(client), s => Assert.False(s.Value, s.Key));

        await client.PostAsJsonAsync("/api/auth/verify-email", new { token = _factory.Mail.TokenFor("owner@harbour.example.com") });
        Assert.Equal(HttpStatusCode.NoContent, (await client.PutAsJsonAsync("/api/clinic",
            new { name = "Harbour Heart", address = "1 Harbour Road", phone = "555-0100", reportFooter = "Synthetic" })).StatusCode);
        // Not after the tenant cache runs out: at once.
        var settings = await client.GetFromJsonAsync<JsonElement>("/api/settings");
        Assert.Equal("Harbour Heart", settings.GetProperty("name").GetString());
        Assert.Equal("1 Harbour Road", settings.GetProperty("address").GetString());

        // A colleague and a template of the clinic's own, straight into its database.
        await using (var db = _factory.Services.GetRequiredService<ClinicReportDatabases>().Create("harbour"))
        {
            db.Users.Add(new ClinicReport.Api.Data.AppUser { UserName = "colleague", DisplayName = "Colleague", CreatedAt = DateTime.UtcNow });
            var version = await db.TemplateVersions.FirstAsync(v => v.Status == "Published");
            version.PublishedByUserId = 1;
            await db.SaveChangesAsync();
        }
        var patient = await (await client.PostAsJsonAsync("/api/patients",
            new { mrn = "TEST-1", firstName = "Test", lastName = "Patient", dateOfBirth = "1970-01-01", sex = "U" })).Content.ReadFromJsonAsync<JsonElement>();
        var template = (await client.GetFromJsonAsync<JsonElement[]>("/api/templates"))!.First(t => t.GetProperty("publishedVersionId").ValueKind == JsonValueKind.Number);
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync("/api/reports", new
        {
            patientId = patient.GetProperty("id").GetInt32(), templateId = template.GetProperty("id").GetInt32(), examDate = "2026-11-02",
        })).StatusCode);

        var onboarding = await client.GetFromJsonAsync<ClinicEndpoints.Onboarding>("/api/clinic/onboarding");
        Assert.All(onboarding!.Steps, s => Assert.True(s.Done, s.Key));
        Assert.True(onboarding.Complete);
    }

    [Fact]
    public async Task One_clinics_letterhead_is_not_another_ones()
    {
        var east = await SignUpAndLogInAsync("east");
        var west = await SignUpAndLogInAsync("west");

        await east.PutAsJsonAsync("/api/clinic", new { name = "East Heart", address = "1 East Road", phone = (string?)null, reportFooter = (string?)null });

        var westSettings = await west.GetFromJsonAsync<JsonElement>("/api/settings");
        Assert.Equal("Clinic west", westSettings.GetProperty("name").GetString());
        Assert.False(westSettings.TryGetProperty("address", out var address) && address.ValueKind == JsonValueKind.String);
        Assert.False((await StepsAsync(west))["letterhead"]);
        Assert.True((await StepsAsync(east))["letterhead"]);
    }

    [Fact]
    public async Task Only_a_signed_in_administrator_changes_the_letterhead()
    {
        await SignUpAndLogInAsync("harbour");
        var anonymous = _factory.ClientFor("harbour");
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PutAsJsonAsync("/api/clinic", new { name = "Taken over" })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/clinic/onboarding")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _factory.RootClient().GetAsync("/api/clinic/onboarding")).StatusCode);
    }

    [Fact]
    public async Task A_clinic_needs_a_name()
    {
        var client = await SignUpAndLogInAsync("harbour");
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync("/api/clinic", new { name = " " })).StatusCode);
    }
}
