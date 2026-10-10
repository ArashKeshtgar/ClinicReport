using System.Collections.Concurrent;
using ClinicReport.Api.Auth;
using ClinicReport.Api.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace ClinicReport.Api.Reports;

public record Viewer(string ConnectionId, int UserId, string Name, string Role, string? FieldId);

/// <summary>
/// Who has which report open, and which field they are on. Kept in memory: one API instance serves a
/// clinic. Running several instances would need a SignalR backplane and a shared store here.
/// </summary>
public class PresenceTracker
{
    private readonly ConcurrentDictionary<string, (int ReportId, Viewer Viewer)> _connections = new();

    public void Join(int reportId, Viewer viewer) => _connections[viewer.ConnectionId] = (reportId, viewer);

    public int? Leave(string connectionId) =>
        _connections.TryRemove(connectionId, out var entry) ? entry.ReportId : null;

    public void Focus(string connectionId, string? fieldId)
    {
        if (_connections.TryGetValue(connectionId, out var entry))
            _connections[connectionId] = (entry.ReportId, entry.Viewer with { FieldId = fieldId });
    }

    public int? ReportOf(string connectionId) =>
        _connections.TryGetValue(connectionId, out var entry) ? entry.ReportId : null;

    public IReadOnlyList<Viewer> ViewersOf(int reportId) =>
        _connections.Values.Where(e => e.ReportId == reportId).Select(e => e.Viewer).OrderBy(v => v.Name).ToList();
}

/// <summary>
/// Live channel for an open report. Changes themselves go through the REST API (so they are validated
/// and audited in one place); the hub only carries presence and the "reportChanged" broadcasts.
/// </summary>
[Authorize]
public class ReportHub(PresenceTracker presence, ClinicReportDbContext db) : Hub
{
    public static string GroupName(int reportId) => $"report-{reportId}";

    public async Task JoinReport(int reportId)
    {
        if (!await db.Reports.AnyAsync(r => r.Id == reportId))
            throw new HubException("Report not found.");

        if (presence.ReportOf(Context.ConnectionId) is { } previous && previous != reportId)
            await LeaveCurrent();

        var user = Context.User!;
        presence.Join(reportId, new Viewer(Context.ConnectionId, user.RequiredUserId(), user.Identity!.Name ?? "?",
            user.FindFirst(System.Security.Claims.ClaimTypes.Role)?.Value ?? "", null));
        await Groups.AddToGroupAsync(Context.ConnectionId, GroupName(reportId));
        await BroadcastPresence(reportId);
    }

    public Task LeaveReport() => LeaveCurrent();

    /// <summary>Tells the others which field this person is typing in (null when none).</summary>
    public async Task Focus(string? fieldId)
    {
        if (fieldId is { Length: > 64 })
            return;
        presence.Focus(Context.ConnectionId, fieldId);
        if (presence.ReportOf(Context.ConnectionId) is { } reportId)
            await BroadcastPresence(reportId);
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        await LeaveCurrent();
        await base.OnDisconnectedAsync(exception);
    }

    private async Task LeaveCurrent()
    {
        if (presence.Leave(Context.ConnectionId) is { } reportId)
        {
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, GroupName(reportId));
            await BroadcastPresence(reportId);
        }
    }

    private Task BroadcastPresence(int reportId) =>
        Clients.Group(GroupName(reportId)).SendAsync("presence",
            presence.ViewersOf(reportId).Select(v => new { v.ConnectionId, v.UserId, v.Name, v.Role, v.FieldId }));
}
