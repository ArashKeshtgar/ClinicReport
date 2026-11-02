using ClinicReport.Api.Auth;
using ClinicReport.Api.Data;
using ClinicReport.Api.Reports;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace ClinicReport.Tests;

/// <summary>Hosted mode: "--migrate-all" and the background senders.</summary>
public class MigrateAllTests : IAsyncLifetime
{
    private readonly HostedFactory _factory = new();

    public async Task InitializeAsync()
    {
        await _factory.AddTenantAsync("north");
        await _factory.AddTenantAsync("paused", TenantStatus.Suspended);
        await _factory.AddTenantAsync("gone", TenantStatus.Cancelled);
    }

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    private async Task AssertMigratedAsync(string slug)
    {
        Assert.True(File.Exists(_factory.DatabaseOf(slug)), slug);
        await using var db = _factory.Services.GetRequiredService<ClinicReportDatabases>().Create(slug);
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
    }

    [Fact]
    public async Task Every_tenant_is_migrated_whatever_its_status()
    {
        Assert.False(File.Exists(_factory.DatabaseOf("north")));

        Assert.Equal(0, await TenantMigration.MigrateAllAsync(_factory.Services));

        foreach (var slug in new[] { "north", "paused", "gone" })
            await AssertMigratedAsync(slug);
    }

    [Fact]
    public async Task A_tenant_that_fails_is_counted_and_the_others_are_still_migrated()
    {
        // A directory where the database file should be: SQLite cannot open it.
        Directory.CreateDirectory(_factory.DatabaseOf("paused"));

        Assert.Equal(1, await TenantMigration.MigrateAllAsync(_factory.Services));

        await AssertMigratedAsync("north");
        await AssertMigratedAsync("gone");
    }

    [Fact]
    public async Task A_tenant_that_failed_to_open_is_tried_again()
    {
        Directory.CreateDirectory(_factory.DatabaseOf("paused"));
        Assert.Equal(1, await TenantMigration.MigrateAllAsync(_factory.Services));

        // Once the cause is gone, the same process opens it: the failure was not kept.
        Directory.Delete(_factory.DatabaseOf("paused"));
        Assert.Equal(0, await TenantMigration.MigrateAllAsync(_factory.Services));
        await AssertMigratedAsync("paused");
    }

    [Fact]
    public void Hosted_mode_runs_no_ClinicHub_senders()
    {
        Assert.Null(_factory.Services.GetService<ResultSender>());
        Assert.Null(_factory.Services.GetService<FinanceSender>());
        Assert.DoesNotContain(_factory.Services.GetServices<IHostedService>(), s => s is ResultSenderWorker or FinanceSenderWorker);
    }
}
